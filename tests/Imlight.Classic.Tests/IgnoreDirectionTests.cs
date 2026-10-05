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
 * IGNORE DIRECTION TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Multiplayer audit item C: an ignore belongs to the wizard who made it. A ignoring B (on a shared friend row whose
 * first player is B) hides B from A; it no longer reads as B ignoring A. Old rows (Blocked, no BlockedBy) migrate as
 * the first player's ignore of the second.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System.Linq;
using Imlight.Classic.Admin;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class IgnoreDirectionTests {

    private const ulong A = 9_300_001, B = 9_300_002, C = 9_300_003;

    // A friendship B asked for: B is the row's first player.
    private static Relationship FriendRow() => new() { FirstPlayerId = B, SecondPlayerId = A };

    [Fact]
    public void IgnoringAFriendIsTheIgnorersOnly() {
        var row = FriendRow();

        Assert.True(IgnoreRules.SetIgnore(row, A, ignore: true));

        Assert.True(IgnoreRules.Ignores(row, A, B));   // B's chat, whispers and requests no longer reach A
        Assert.False(IgnoreRules.Ignores(row, B, A));  // A still reaches B
        Assert.True(row.Blocked);                      // the friendship is suspended
        Assert.Equal([B], IgnoreRules.IgnoredBy([row], A));
        Assert.Empty(IgnoreRules.IgnoredBy([row], B)); // A is not on B's ignore list
        Assert.Equal([A], IgnoreRules.WhoIgnore([row], B));
        Assert.Empty(IgnoreRules.WhoIgnore([row], A));
    }

    [Fact]
    public void EachSideLiftsOnlyItsOwnIgnore() {
        var row = FriendRow();
        IgnoreRules.SetIgnore(row, A, ignore: true);
        IgnoreRules.SetIgnore(row, B, ignore: true);

        Assert.False(IgnoreRules.SetIgnore(row, A, ignore: true)); // no duplicate
        Assert.True(IgnoreRules.SetIgnore(row, B, ignore: false));

        Assert.True(IgnoreRules.Ignores(row, A, B));
        Assert.False(IgnoreRules.Ignores(row, B, A));
        Assert.True(row.Blocked);

        Assert.True(IgnoreRules.SetIgnore(row, A, ignore: false));
        Assert.False(row.Blocked);
        Assert.Empty(row.BlockedBy);
    }

    [Fact]
    public void ARowBetweenOthersSaysNothing() {
        var row = FriendRow();
        IgnoreRules.SetIgnore(row, A, ignore: true);

        Assert.False(IgnoreRules.Ignores(row, A, C));
        Assert.Empty(IgnoreRules.WhoIgnore([row], C));
    }

    [Fact]
    public void OldRowsMigrateAsTheFirstPlayersIgnore() {
        // A row stored before schema 3: only Blocked, no BlockedBy in the document.
        var legacy = JsonConvert.DeserializeObject<Relationship>(
            $"{{\"RelationshipId\":1,\"FirstPlayerId\":{A},\"SecondPlayerId\":{B},\"Blocked\":true,\"IsBrokenUp\":true}}")!;
        Assert.Empty(legacy.BlockedBy);

        Assert.True(IgnoreRules.MigrateLegacy(legacy));
        Assert.Equal([A], legacy.BlockedBy);
        Assert.True(IgnoreRules.Ignores(legacy, A, B));
        Assert.False(IgnoreRules.Ignores(legacy, B, A));

        Assert.False(IgnoreRules.MigrateLegacy(legacy)); // idempotent
    }

    [Fact]
    public void MigrationRepairsANullListAndStrayIds() {
        var row = JsonConvert.DeserializeObject<Relationship>(
            $"{{\"FirstPlayerId\":{A},\"SecondPlayerId\":{B},\"Blocked\":false,\"BlockedBy\":null}}")!;
        Assert.True(IgnoreRules.MigrateLegacy(row));
        Assert.NotNull(row.BlockedBy);
        Assert.False(row.Blocked);

        row.BlockedBy.AddRange([C, B, B]);
        Assert.True(IgnoreRules.MigrateLegacy(row));
        Assert.Equal([B], row.BlockedBy);
        Assert.True(row.Blocked);
    }

    [Fact]
    public void AnUnblockedRowIsUntouchedByMigration() {
        var row = FriendRow();
        Assert.False(IgnoreRules.MigrateLegacy(row));
        Assert.Empty(row.BlockedBy);
        Assert.False(row.Blocked);
    }

    [Fact]
    public void TheBehaviorIgnoresAndListsByOwner() {
        // A's in-memory rows: a friend B (B asked first) and a stranger C.
        var friends = new ServerFriendBehavior { Relationships = [FriendRow()] };

        friends.Ignore(A, B);
        friends.Ignore(A, C);

        Assert.True(friends.HasPlayerBlocked(A, B));
        Assert.False(friends.HasPlayerBlocked(B, A));
        // The stranger's row is born broken up and is still on the list (it was filtered out before).
        Assert.Equal([B, C], friends.GetIgnoredCharacterIds(A).OrderBy(x => x));

        Assert.NotNull(friends.Unignore(A, B));
        Assert.Equal([C], friends.GetIgnoredCharacterIds(A));
        Assert.False(friends.TryGetRelationship(C, out var stranger) && !stranger.IsBrokenUp); // no friendship made
    }

    [Fact]
    public void TheIgnoreOfTheOtherSideSurvivesAMirroredWrite() {
        // B's copy of the row was loaded before A ignored B; the stored row (A's ignore) replaces it.
        var bCopy = new ServerFriendBehavior { Relationships = [FriendRow()] };
        var stored = FriendRow();
        IgnoreRules.SetIgnore(stored, A, ignore: true);

        bCopy.MirrorRelationship(stored);
        bCopy.Ignore(B, A);

        var row = Assert.Single(bCopy.Relationships);
        Assert.Equal([A, B], row.BlockedBy.OrderBy(x => x));
    }

    [Fact]
    public void FriendRulesStillSuspendAFriendshipEitherSideIgnores() {
        var row = FriendRow();
        IgnoreRules.SetIgnore(row, A, ignore: true);

        Assert.False(FriendRules.IsFriend(row));
    }

    [Fact]
    public void TheSchemaIsBumpedForBlockedBy() => Assert.True(PlayerDataSchema.Current >= 3);

}
