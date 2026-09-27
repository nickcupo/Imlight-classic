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
 * CLASSIC QUEST ENGINE TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * The left-to-right requirement reading, on the shapes of real r806919 zone
 * triggers, and its agreement with stock Imlight on lists that do not mix
 * AND and OR.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * Each list copies a decoded trigger's items in data order (operator, NOT
 * and what the item checks); the player state is a set of true facts. A
 * census of every r806919 trigger, spawn requirement and SpiralDB quest and
 * drop table found 90 triggers and 15 spawn requirements whose reading
 * changes, and no quest or drop table list.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class RequirementFoldTests {

    private sealed record Req(string Fact, bool Or = false, bool Not = false, Req[]? Nested = null);

    private static Req And(string fact) => new(fact);
    private static Req Or(string fact) => new(fact, Or: true);
    private static Req AndNot(string fact) => new(fact, Not: true);

    private static bool LeftToRight(IReadOnlyList<Req?> list, params string[] facts) {
        var known = facts.ToHashSet(StringComparer.Ordinal);

        return Fold(list);

        bool Fold(IReadOnlyList<Req?> items)
            => RequirementFold.Evaluate(items,
                item => item.Nested is { } nested ? Fold(nested) : known.Contains(item.Fact),
                item => item.Or,
                item => item.Not);
    }

    // RequirementDispatcher's stock reading: every AND item true, and any OR item true when there is one.
    private static bool Stock(IReadOnlyList<Req> list, params string[] facts) {
        var known = facts.ToHashSet(StringComparer.Ordinal);
        var values = list.Select(item => (item.Or, Value: known.Contains(item.Fact) != item.Not)).ToList();
        var ands = values.Where(value => !value.Or).Select(value => value.Value).ToList();
        var ors = values.Where(value => value.Or).Select(value => value.Value).ToList();

        return ands.All(value => value) && (ors.Count == 0 || ors.Any(value => value));
    }

    // WizardCity-WC_Hub "Trigger TelepotToUW", the Commons gate to Unicorn Way.
    private static readonly Req[] s_telepotToUw = [
        Or("HasEntry WC-MAIN-C01-003_Complete"),
        And("HasGoal WC-MAIN-C01-003/WC-MAIN-C01-003_Goal0 Complete"),
    ];

    // WizardCity-WC_Hub "TeleportToShoppingDistrict" (Pet Park and Castle Tours read the same).
    private static readonly Req[] s_toShoppingDistrict = [
        Or("HasEntry WC-UNICORN-MAIN-008_Complete"),
        And("HasEntry WC-ST01-C01-006_Complete"),
    ];

    // WizardCity-WC_Hub "TeleportToGolemCourt": the same two quests, listed the other way round.
    private static readonly Req[] s_toGolemCourt = [
        Or("HasEntry WC-ST01-C01-006_Complete"),
        And("HasEntry WC-UNICORN-MAIN-008_Complete"),
    ];

    // WizardCity-WC_Streets-WC_Unicorn "Teleport To Lady Blackhope's Tower (1)".
    private static readonly Req[] s_blackhopeTower = [
        Or("HasQuest WC-MISC-C01-001"),
        And("HasEntry WC-MISC-C01-001_Complete"),
    ];

    // WizardCity-WC_Hub "Trigger 0 (2)".
    private static readonly Req[] s_trigger0Two = [
        Or("HasQuest WC-MAIN-C01-009"),
        And("HasQuest WC-COMMONS-MAIN-004"),
    ];

    // WizardCity-Interiors-WC_Headmistress_House "Trigger To Backroom".
    private static readonly Req[] s_toBackroom = [
        Or("HasQuest WC-PrePL-MAIN-002-BALANCE"), Or("HasQuest WC-PrePL-MAIN-002-DEATH"),
        Or("HasQuest WC-PrePL-MAIN-002-FIRE"), Or("HasQuest WC-PrePL-MAIN-002-LIFE"),
        Or("HasQuest WC-PrePL-MAIN-002-ICE"), Or("HasQuest WC-PrePL-MAIN-002-MYTH"),
        Or("HasQuest WC-PrePL-MAIN-002-STORM"), And("HasQuest WC-PrePL-MAIN-001"),
    ];

    // WizardCity-WC_Streets-WC_Triton_Underwater-WC_Triton_Underwater "Trigger Minion Quest": one quest per school.
    private static readonly Req[] s_minionQuest = [
        Or("HasQuest MB-SPELL-C01-001"), Or("HasQuest MB-SPELL-C02-001"), Or("HasQuest MB-SPELL-C03-001"),
        Or("HasQuest MB-SPELL-C04-001"), Or("HasQuest MB-SPELL-C05-001"), Or("HasQuest MB-SPELL-C06-001"),
        And("HasQuest MB-SPELL-C07-001"),
    ];

    // WizardCity-WC_Shop_Area "Trigger_Teleport_2_PartyRoom": the one Wizard City list that starts with AND (a post-2010 event).
    private static readonly Req[] s_partyRoom = [
        And("EntryValue Decaversary == 1"),
        Or("HasQuest WC-BIRTHDAY-TEN-001A"), Or("HasQuest WC-BIRTHDAY-TEN-001B"),
        Or("HasEntry WC-BIRTHDAY-TEN-001A_Complete"), Or("HasEntry WC-BIRTHDAY-TEN-001B_Complete"),
    ];

    // WizardCity-Interiors-WC_Headmistress_House "Dialogue": Ambrose keeps a player without enrollment in.
    private static readonly Req[] s_officeDialogue = [AndNot("HasEntry GainedEnrollment")];

    [Fact]
    public void CommonsGateOpensWhileGoal0IsDoneOrOnceTheQuestIsComplete() {
        Assert.True(LeftToRight(s_telepotToUw, "HasGoal WC-MAIN-C01-003/WC-MAIN-C01-003_Goal0 Complete"));
        Assert.True(LeftToRight(s_telepotToUw, "HasEntry WC-MAIN-C01-003_Complete"));
        Assert.False(LeftToRight(s_telepotToUw));

        // A completed quest has no goal instances left, so stock Imlight never opens the gate.
        Assert.False(Stock(s_telepotToUw, "HasGoal WC-MAIN-C01-003/WC-MAIN-C01-003_Goal0 Complete"));
        Assert.False(Stock(s_telepotToUw, "HasEntry WC-MAIN-C01-003_Complete"));
    }

    [Fact]
    public void CommonsExitsOpenAfterEitherRattlebonesReport() {
        foreach (var list in new[] { s_toShoppingDistrict, s_toGolemCourt }) {
            Assert.True(LeftToRight(list, "HasEntry WC-ST01-C01-006_Complete"));
            Assert.True(LeftToRight(list, "HasEntry WC-UNICORN-MAIN-008_Complete"));
            Assert.False(LeftToRight(list));
            Assert.False(Stock(list, "HasEntry WC-ST01-C01-006_Complete"));
        }
    }

    [Fact]
    public void QuestDoorsOpenWhileHeldOrAfterCompletion() {
        Assert.True(LeftToRight(s_blackhopeTower, "HasQuest WC-MISC-C01-001"));
        Assert.True(LeftToRight(s_blackhopeTower, "HasQuest WC-MISC-C01-001", "HasEntry WC-MISC-C01-001_Complete"));
        Assert.False(LeftToRight(s_blackhopeTower));
        Assert.False(Stock(s_blackhopeTower, "HasQuest WC-MISC-C01-001"));

        Assert.True(LeftToRight(s_trigger0Two, "HasQuest WC-MAIN-C01-009"));
        Assert.True(LeftToRight(s_trigger0Two, "HasQuest WC-COMMONS-MAIN-004"));
        Assert.False(Stock(s_trigger0Two, "HasQuest WC-MAIN-C01-009"));
    }

    [Fact]
    public void AnyOneSchoolQuestPasses() {
        Assert.True(LeftToRight(s_minionQuest, "HasQuest MB-SPELL-C07-001"));
        Assert.True(LeftToRight(s_minionQuest, "HasQuest MB-SPELL-C03-001"));
        Assert.False(LeftToRight(s_minionQuest));
        Assert.False(Stock(s_minionQuest, "HasQuest MB-SPELL-C03-001"));

        Assert.True(LeftToRight(s_toBackroom, "HasQuest WC-PrePL-MAIN-002-ICE"));
        Assert.True(LeftToRight(s_toBackroom, "HasQuest WC-PrePL-MAIN-001"));
        Assert.False(LeftToRight(s_toBackroom));
    }

    [Fact]
    public void PartyRoomNeedsTheFlagForTheFirstQuest() {
        // todo: unverified. Does the leading AND bind only to the next item? If it binds to the whole OR run, as
        // stock reads it, HasQuest WC-BIRTHDAY-TEN-001B alone would not pass. Only the cases both readings share are asserted.
        Assert.True(LeftToRight(s_partyRoom, "EntryValue Decaversary == 1", "HasQuest WC-BIRTHDAY-TEN-001A"));
        Assert.True(Stock(s_partyRoom, "EntryValue Decaversary == 1", "HasQuest WC-BIRTHDAY-TEN-001A"));
        Assert.False(LeftToRight(s_partyRoom, "HasQuest WC-BIRTHDAY-TEN-001A"));
        Assert.False(LeftToRight(s_partyRoom, "EntryValue Decaversary == 1"));
    }

    [Fact]
    public void NotAppliesToItsItemOnly() {
        Assert.True(LeftToRight(s_officeDialogue));
        Assert.False(LeftToRight(s_officeDialogue, "HasEntry GainedEnrollment"));

        Req[] list = [new Req("a", Or: true, Not: true), And("b")];
        Assert.True(LeftToRight(list));
        Assert.True(LeftToRight(list, "a", "b"));
        Assert.False(LeftToRight(list, "a"));
    }

    [Fact]
    public void UnmixedListsReadLikeStockImlight() {
        var facts = new[] { "a", "b", "c", "d" };
        foreach (var or in new[] { false, true }) {
            for (var size = 1; size <= facts.Length; size++) {
                for (var notMask = 0; notMask < 1 << size; notMask++) {
                    var list = facts.Take(size).Select((fact, i) => new Req(fact, or, (notMask >> i & 1) == 1)).ToArray();
                    for (var trueMask = 0; trueMask < 1 << size; trueMask++) {
                        var known = facts.Take(size).Where((_, i) => (trueMask >> i & 1) == 1).ToArray();
                        Assert.Equal(Stock(list, known), LeftToRight(list, known));
                    }
                }
            }
        }
    }

    [Fact]
    public void LastItemsOperatorIsUnused() {
        foreach (var known in new string[][] { [], ["a"], ["b"], ["a", "b"] }) {
            Assert.Equal(LeftToRight([And("a"), And("b")], known), LeftToRight([And("a"), Or("b")], known));
            Assert.Equal(LeftToRight([Or("a"), And("b")], known), LeftToRight([Or("a"), Or("b")], known));
        }
    }

    [Fact]
    public void EmptyAndNullItems() {
        Assert.True(LeftToRight([]));
        Assert.True(LeftToRight([null]));
        Assert.True(LeftToRight([Or("a"), null, And("b")], "b"));
        Assert.False(LeftToRight([And("a"), null, And("b")], "b"));
    }

    [Fact]
    public void NestedListsAreOneItem() {
        // WizardCity-WC_Duel_Arena object 1451765's spawn requirement: an AND item, then a nested list.
        Req[] nested = [And("a"), new Req("", Nested: [Or("b"), And("c")])];
        Assert.True(LeftToRight(nested, "a", "c"));
        Assert.False(LeftToRight(nested, "b", "c"));

        Req[] negatedNested = [Or("a"), new Req("", Not: true, Nested: [And("b"), And("c")])];
        Assert.True(LeftToRight(negatedNested));
        Assert.False(LeftToRight(negatedNested, "b", "c"));
        Assert.True(LeftToRight(negatedNested, "a", "b", "c"));
    }

    [Fact]
    public void SkipsItemsTheResultAlreadyDecides() {
        var evaluated = new List<string>();
        var known = new HashSet<string> { "a" };
        bool Evaluate(Req[] list)
            => RequirementFold.Evaluate(list, item => {
                evaluated.Add(item.Fact);

                return known.Contains(item.Fact);
            }, item => item.Or, item => item.Not);

        Assert.True(Evaluate([Or("a"), Or("b"), And("c")]));
        Assert.Equal(new[] { "a" }, evaluated);

        evaluated.Clear();
        Assert.True(Evaluate([And("b"), Or("c"), Or("a")]));
        Assert.Equal(new[] { "b", "a" }, evaluated);
    }

}
