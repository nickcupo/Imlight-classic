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
using System.Linq;
using Imlight.CoreLib.WizardData.Databases;

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

/// <summary>The uses in the player database.</summary>
internal sealed class RavenSecondChanceUseStore : ISecondChanceUseStore {

    public IReadOnlyDictionary<ulong, int> Load(ulong charId, DateOnly day) {
        if (PlayerDatabase.Instance.Store is not { } store) {
            return new Dictionary<ulong, int>();
        }

        using var session = store.OpenSession();
        var record = session.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(charId));
        if (record is null || record.Day != SecondChanceUseRecord.DayText(day)) {
            return new Dictionary<ulong, int>(); // none, or an older day's (replaced on the next save)
        }

        return record.Uses
            .Where(entry => ulong.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .ToDictionary(entry => ulong.Parse(entry.Key, CultureInfo.InvariantCulture), entry => entry.Value);
    }

    public void Save(ulong charId, DateOnly day, IReadOnlyDictionary<ulong, int> uses) {
        if (PlayerDatabase.Instance.Store is not { } store) {
            return;
        }

        using var session = store.OpenSession();
        session.Advanced.UseOptimisticConcurrency = false; // only this server writes it, under the chests' lock
        session.Store(new SecondChanceUseRecord {
            CharId = charId,
            Day = SecondChanceUseRecord.DayText(day),
            Uses = uses.ToDictionary(entry => entry.Key.ToString(CultureInfo.InvariantCulture), entry => entry.Value),
        }, SecondChanceUseRecord.DocumentId(charId));
        session.SaveChanges();
    }

}
