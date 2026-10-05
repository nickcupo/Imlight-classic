/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * AMBIENT WIZARD COLLECTION
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the ambient wizards' own RavenDB collection, "AmbientWizards",
 * never mixed with accounts or characters. One document per ambient
 * wizard: its stable character id, its identity (seed, name keys, school,
 * level, look, home zone, temper) and what it remembers of its friends
 * (name, last zone, last quest, last time together, battles together).
 * Real players' friendships with it are ordinary BuddyRelationships rows
 * (the player's friend list), keyed by its character id.
 *
 * Writes go to a background task so an actor never waits on the database.
 *
 * USAGE EXAMPLE:
 * var records = AmbientWizardCollection.LoadAll();
 * AmbientWizardCollection.Save(record);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Databases;
using Raven.Client.Documents;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A friend as the ambient wizard remembers them.</summary>
public sealed class AmbientFriendRecord {

    public ulong CharId { get; set; }
    public string Name { get; set; } = "";
    public string LastZone { get; set; }
    public string LastQuest { get; set; }
    public DateTime? LastPlayedTogether { get; set; }
    public int TimesHelped { get; set; }

    internal FriendMemory ToMemory() => new(CharId, Name, LastZone, LastQuest, LastPlayedTogether, TimesHelped);

}

/// <summary>One ambient wizard, as stored.</summary>
public sealed class AmbientWizardRecord {

    public string Id { get; set; }
    public ulong CharId { get; set; }
    public int Seed { get; set; }
    public string HomeZone { get; set; } = "";
    public uint NameKeys { get; set; }
    public bool Female { get; set; }
    public string School { get; set; } = "";
    public byte Level { get; set; }
    public byte HairModel { get; set; }
    public byte HairColor { get; set; }
    public byte SkinColor { get; set; }
    public byte Face { get; set; }
    public byte ClothingColor { get; set; }
    public byte TrimColor { get; set; }
    public string Temper { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<AmbientFriendRecord> Friends { get; set; } = [];

    /// <summary>CLASSIC (2026-10-04): when its pet last hatched (Unix seconds; 0 never): once every 24 hours.</summary>
    public long PetLastHatchUnix { get; set; }

    internal static AmbientWizardRecord From(AmbientIdentity identity, ulong charId) => new() {
        Id = $"{AmbientWizardCollection.CollectionName}/{charId}",
        CharId = charId,
        Seed = identity.Seed,
        HomeZone = identity.HomeZone,
        NameKeys = identity.NameKeys,
        Female = identity.Look.Female,
        School = identity.School.ToString(),
        Level = identity.Level,
        HairModel = identity.Look.HairModel,
        HairColor = identity.Look.HairColor,
        SkinColor = identity.Look.SkinColor,
        Face = identity.Look.Face,
        ClothingColor = identity.Look.ClothingColor,
        TrimColor = identity.Look.TrimColor,
        Temper = identity.Temper.ToString(),
        CreatedUtc = DateTime.UtcNow,
    };

    internal AmbientIdentity ToIdentity() => new(Seed, NameKeys,
        Enum.TryParse<AmbientSchool>(School, out var school) ? school : AmbientSchool.Balance, Level,
        new AmbientLook(Female, HairModel, HairColor, SkinColor, Face, ClothingColor, TrimColor), HomeZone,
        Enum.TryParse<AmbientTemper>(Temper, out var temper) ? temper : AmbientTemper.Friendly);

}

/// <summary>The AmbientWizards collection.</summary>
public static class AmbientWizardCollection {

    public const string CollectionName = "AmbientWizards";

    /// <summary>Ambient character ids start here (real ones are random 64-bit values from GUIDs).</summary>
    internal const ulong CharIdBase = 0x00A3_B1E0_0000_0000;

    private static IDocumentStore Store => PlayerDatabase.Instance.Store;

    private static readonly Channel<AmbientWizardRecord> s_pending =
        Channel.CreateUnbounded<AmbientWizardRecord>(new UnboundedChannelOptions { SingleReader = true });

    private static Task s_writer;

    /// <summary>Every stored ambient wizard (blocking; called once at boot, off the actors).</summary>
    public static List<AmbientWizardRecord> LoadAll() {
        using var session = Store.OpenSession();
        var all = new List<AmbientWizardRecord>();
        var skip = 0;
        while (true) {
            var page = session.Query<AmbientWizardRecord>(collectionName: CollectionName).Skip(skip).Take(512).ToList();
            all.AddRange(page);
            if (page.Count < 512) {
                return all;
            }

            skip += page.Count;
        }
    }

    /// <summary>Stores (or replaces) a record in the background.</summary>
    public static void Save(AmbientWizardRecord record) {
        if (record is null) {
            return;
        }

        s_writer ??= Task.Run(WriteQueuedAsync);
        s_pending.Writer.TryWrite(Clone(record));
    }

    /// <summary>Stores records now (boot, before the actors start).</summary>
    public static void SaveNow(IEnumerable<AmbientWizardRecord> records) {
        using var session = Store.OpenSession();
        foreach (var record in records) {
            session.Store(record, record.Id);
            session.Advanced.GetMetadataFor(record)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
        }

        session.SaveChanges();
    }

    // A snapshot, so the actor that owns the record can keep changing it while the write is queued.
    private static AmbientWizardRecord Clone(AmbientWizardRecord r) => new() {
        Id = r.Id, CharId = r.CharId, Seed = r.Seed, HomeZone = r.HomeZone, NameKeys = r.NameKeys, Female = r.Female,
        School = r.School, Level = r.Level, HairModel = r.HairModel, HairColor = r.HairColor, SkinColor = r.SkinColor,
        Face = r.Face, ClothingColor = r.ClothingColor, TrimColor = r.TrimColor, Temper = r.Temper, CreatedUtc = r.CreatedUtc,
        PetLastHatchUnix = r.PetLastHatchUnix,
        Friends = [.. r.Friends.Select(f => new AmbientFriendRecord {
            CharId = f.CharId, Name = f.Name, LastZone = f.LastZone, LastQuest = f.LastQuest,
            LastPlayedTogether = f.LastPlayedTogether, TimesHelped = f.TimesHelped,
        })],
    };

    private static async Task WriteQueuedAsync() {
        while (await s_pending.Reader.WaitToReadAsync()) {
            var batch = new Dictionary<string, AmbientWizardRecord>();
            while (batch.Count < 64 && s_pending.Reader.TryRead(out var record)) {
                batch[record.Id] = record; // the latest copy of each wins
            }

            try {
                using var session = Store.OpenAsyncSession();
                foreach (var record in batch.Values) {
                    await session.StoreAsync(record, record.Id);
                    session.Advanced.GetMetadataFor(record)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
                }

                await session.SaveChangesAsync();
            }
            catch (Exception ex) {
                Logger.Error("Ambient wizard write of {Count} record(s) failed: {Error}", Logger.Args(batch.Count, ex.Message));
            }
        }
    }

}
