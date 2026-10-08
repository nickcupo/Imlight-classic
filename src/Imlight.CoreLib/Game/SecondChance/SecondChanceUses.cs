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
 * SECOND CHANCE USES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the saved daily uses of the Second Chance chests: one document
 * per wizard (SecondChanceUses/{CharId}) holding the uses of its latest game
 * day only, so a restart does not hand the day's uses back and the
 * collection never grows past one small document per wizard (each save of a
 * new day replaces the old day).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Game.SecondChance;

/// <summary>Where the daily Second Chance uses are kept.</summary>
internal interface ISecondChanceUseStore {

    /// <summary>The uses per chest template of <paramref name="charId"/> on <paramref name="day"/> (empty if none).</summary>
    IReadOnlyDictionary<ulong, int> Load(ulong charId, DateOnly day);

    /// <summary>Replaces <paramref name="charId"/>'s saved uses with <paramref name="uses"/> on <paramref name="day"/>.</summary>
    void Save(ulong charId, DateOnly day, IReadOnlyDictionary<ulong, int> uses);

}

/// <summary>A wizard's Second Chance uses on one game day (the database document).</summary>
public sealed class SecondChanceUseRecord {

    public const string CollectionName = "SecondChanceUses";

    public static string DocumentId(ulong charId) => $"{CollectionName}/{charId}";

    public ulong CharId { get; set; }

    /// <summary>The game day, yyyy-MM-dd.</summary>
    public string Day { get; set; } = "";

    /// <summary>Uses per chest template id (string keys: RavenDB stores dictionary keys as strings).</summary>
    public Dictionary<string, int> Uses { get; set; } = [];

    public static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

}

// CLASSIC: same-session read/stage helpers; no independent saved use counter is reachable by production.
internal static class SecondChanceUses {
    internal static bool TryRead(IDocumentSession session, ulong charId, DateOnly day,
        out SecondChanceUseRecord record, out Dictionary<string, int> uses) {
        record = session.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(charId));
        uses = [];
        if (record is null) return true;
        if (record.CharId != charId || record.Uses is null
            || !DateOnly.TryParseExact(record.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var storedDay) || storedDay > day) return false;
        foreach (var (key, count) in record.Uses) {
            if (!ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var template)
                || template == 0 || key != template.ToString(CultureInfo.InvariantCulture) || count < 0) return false;
        }
        if (storedDay == day) uses = new(record.Uses);
        return true;
    }

    internal static void Stage(IDocumentSession session, ulong charId, DateOnly day,
        SecondChanceUseRecord record, Dictionary<string, int> uses) {
        var created = record is null;
        record ??= new();
        record.CharId = charId;
        record.Day = SecondChanceUseRecord.DayText(day);
        record.Uses = uses;
        if (created) {
            session.Store(record, SecondChanceUseRecord.DocumentId(charId));
            session.Advanced.GetMetadataFor(record)[Raven.Client.Constants.Documents.Metadata.Collection] = SecondChanceUseRecord.CollectionName;
        }
    }
}
