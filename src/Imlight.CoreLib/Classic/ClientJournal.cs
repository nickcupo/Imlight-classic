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
 * CLIENT JOURNAL (hidden quests)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: keeps the hidden-quests blob the r806919 client writes, per character, and gives it back at login,
 * byte for byte. The client's HiddenQuestsBehavior serializes its list into m_hiddenQuestsData and sends
 * MSG_UPDATEUSERHIDDENQUESTS {Data} when it changes (FUN_140c12dd0); it reads the same blob back in
 * HiddenQuestsBehavior::OnPostLoad (0x141e83340): uint32 0xFACEACE7, uint32 version 1, uint16 count, count x uint64
 * quest id, little-endian. It is binary (zero bytes, 0xFA/0xCE/0xE7), so it is kept as bytes, never as a string
 * (a UTF-8 round trip mangles it), and goes back through ClassicHiddenQuestsBehavior, which writes it raw.
 * Finished tutorial tips are TutorialTipService's (Imlight.Classic.Net.TutorialTipLog decodes the client's log).
 * The active quest-helper quest (MSG_SWITCHACTIVEQUEST) lives in the client's local CharacterRegistry file
 * (m_activeQuestId, m_activeGoalId) and no server message carries it back, so it is not kept here.
 *
 * NOTE:
 * One small document per character (ClientJournals/<char id>), outside the Wizard document: no player-data
 * schema change. Loaded once at attach into Wizard.Journal (never persisted with the wizard). A blob that is not
 * exactly the client's layout is refused. The first version stored a string and refused any NUL, so it never
 * saved a real blob: there is no old data to migrate.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/17/2026
 */

using System;
using System.Buffers.Binary;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Databases;
using Raven.Client;

namespace Imlight.CoreLib.Classic;

/// <summary>The client's hidden-quests blob of one character.</summary>
internal sealed class ClientJournal {

    public ulong CharId { get; set; }
    public byte[] HiddenQuestsData { get; set; } // RavenDB keeps a byte array as base64 (not the first version's string field)
    public DateTime UpdatedUtc { get; set; }

}

/// <summary>The layout HiddenQuestsBehavior::OnPostLoad (0x141e83340) reads.</summary>
internal static class HiddenQuestsBlob {

    internal const uint Magic = 0xFACEACE7;
    internal const uint Version = 1;
    internal const int HeaderSize = 10;

    /// <summary>The quest ids of a well-formed blob; null for anything else (wrong magic, version or length).</summary>
    internal static ulong[] Read(ReadOnlySpan<byte> blob) {
        if (blob.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(blob) != Magic
                || BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]) != Version) {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(blob[8..]);
        if (blob.Length != HeaderSize + 8 * count) {
            return null;
        }

        var ids = new ulong[count];
        for (var i = 0; i < count; i++) {
            ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(blob[(HeaderSize + 8 * i)..]);
        }

        return ids;
    }

    /// <summary>The blob for these quest ids, as the client writes it (tests).</summary>
    internal static byte[] Write(ReadOnlySpan<ulong> ids) {
        var blob = new byte[HeaderSize + 8 * ids.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), (ushort) ids.Length);
        for (var i = 0; i < ids.Length; i++) {
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(HeaderSize + 8 * i), ids[i]);
        }

        return blob;
    }

}

/// <summary>Loads and saves <see cref="ClientJournal"/> documents.</summary>
internal static class ClientJournalStore {

    internal const string CollectionName = "ClientJournals";
    internal const int MaxLength = HiddenQuestsBlob.HeaderSize + 8 * ushort.MaxValue;

    internal static string IdFor(ulong charId) => $"{CollectionName}/{charId}";

    /// <summary>The blob to keep (a copy), or null when it is not the client's hidden-quests layout.</summary>
    internal static byte[] Accept(byte[] value)
        => value is null || value.Length > MaxLength || HiddenQuestsBlob.Read(value) is null ? null : (byte[]) value.Clone();

    /// <summary>The character's journal; an empty one when none is saved or the database cannot be read.</summary>
    internal static ClientJournal Load(ulong charId) {
        try {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            return session.Load<ClientJournal>(IdFor(charId)) ?? new ClientJournal { CharId = charId };
        }
        catch (Exception ex) {
            Logger.Warning("Client journal of {0} not loaded: {1}", Logger.Args(charId, ex.Message));
            return new ClientJournal { CharId = charId };
        }
    }

    /// <summary>Saves one field of the character's journal; returns whether it was written.</summary>
    internal static bool Save(ulong charId, Action<ClientJournal> change) {
        try {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            var journal = session.Load<ClientJournal>(IdFor(charId));
            if (journal is null) {
                journal = new ClientJournal { CharId = charId };
                session.Store(journal, IdFor(charId));
                session.Advanced.GetMetadataFor(journal)[Constants.Documents.Metadata.Collection] = CollectionName;
            }

            change(journal);
            journal.UpdatedUtc = DateTime.UtcNow;
            session.SaveChanges();
            return true;
        }
        catch (Exception ex) {
            Logger.Warning("Client journal of {0} not saved: {1}", Logger.Args(charId, ex.Message));
            return false;
        }
    }

}

/// <summary>
/// CLASSIC: the player object's HiddenQuestsBehavior with the saved blob written as raw bytes. The generated
/// m_hiddenQuestsData is a string that BitWriter.WriteString encodes as UTF-8, which mangles the binary blob; this
/// writes the same fields in the same order (the generated non-versionable encoding: m_behaviorTemplateNameID, then
/// the string as length + bytes) from the bytes themselves. Its hash is HiddenQuestsBehavior's.
/// </summary>
internal sealed record ClassicHiddenQuestsBehavior : Imcodec.ObjectProperty.TypeCache.HiddenQuestsBehavior {

    public byte[] RawData { get; init; }

    public override bool Encode(Imcodec.IO.BitWriter writer, Imcodec.ObjectProperty.ObjectSerializer serializer) {
        if (RawData is not { Length: > 0 } || serializer.Versionable) {
            return base.Encode(writer, serializer); // as generated (the player object is not sent versionable)
        }

        // HiddenQuestsBehavior.Encode -> BehaviorInstance.Encode, as generated, with the blob's bytes.
        OnPreEncode();
        OnPreEncode();
        if (Eligible((Imcodec.ObjectProperty.PropertyFlags) 39, serializer)) {
            writer.WriteUInt32(m_behaviorTemplateNameID);
        }
        OnPostEncode();
        if (Eligible((Imcodec.ObjectProperty.PropertyFlags) 575, serializer)) {
            writer.WriteString(new Imcodec.IO.ByteString(RawData));
        }
        OnPostEncode();
        return true;
    }

    // The generated IsPropertyEligibleForProcessing (private to each generated class).
    private static bool Eligible(Imcodec.ObjectProperty.PropertyFlags flags, Imcodec.ObjectProperty.ObjectSerializer serializer)
        => (flags & serializer.PropertyMask) == serializer.PropertyMask
            && !flags.HasFlag(Imcodec.ObjectProperty.PropertyFlags.Prop_Deprecated);

}
