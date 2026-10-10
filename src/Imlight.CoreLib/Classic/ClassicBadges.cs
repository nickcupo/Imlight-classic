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
 * CLASSIC BADGES
 * ========================================================================
 *
 * PURPOSE:
 * Awards the profile's badges (ClassicProgression.Badges) when a quest
 * completes, a duel is won or a zone is entered, keeps each wizard's kill
 * and zone-visit counters, and builds the client's badge messages.
 *
 * USAGE EXAMPLE:
 * ClassicBadges.QuestCompleted(wizard, questName, SendToSocket);    // QuestService
 * ClassicBadges.MobsDefeated(wizard, mobTemplateIds, SendToSocket); // CombatService
 *
 * NOTE:
 * Badges, counters and visits are quest-registry entries
 * (BadgeRules.BadgeKey/KillCounterKey/ZoneVisitKey), which the registry
 * persists with the rest of ServerQuestBehavior; the registry is a
 * ConcurrentDictionary, so a badge earned on two actors at once is
 * awarded once. A new badge is sent as GAME MSG_BADGES with Add and
 * Display set; the whole list goes out on attach (BadgeService). The badge
 * id the client echoes back (BadgeNameID) is the string hash of the name
 * key. How the official client lays out MSG_BADGES is inferred from its
 * field names, not from a capture.
 *
 * TODO:
 * - Verify MSG_BADGES against the official client's badge page.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class ClassicBadges {

    // CLASSIC: a compound quest claim prepares native messages before its single save.
    internal sealed record PreparedQuestBadgeAward(Badge Badge, GAME_5_PROTOCOL.MSG_BADGES Message);

    /// <summary>
    /// Saves the wizard's quest registry; tests replace it.
    /// </summary>
    private static readonly Action<Wizard> DefaultPersist = wizard => WizardCollection.UpdateCharacterQuestBehavior(wizard);
    internal static Action<Wizard> Persist { get; set; } = DefaultPersist;
    private static bool UsesSavedRegistry => ClassicQuestEngine.IsActive && Persist == DefaultPersist;

    /// <summary>
    /// Logs an award; tests replace it (Logger needs the server's configuration).
    /// </summary>
    internal static Action<Wizard, Badge> Awarded { get; set; } = (wizard, badge)
        => Logger.Information("Wizard {CharId} earned the badge {Badge}.", Logger.Args(wizard.CharId, badge.Name));

    /// <summary>
    /// The profile's badges; tests replace them.
    /// </summary>
    internal static Func<BadgeRules?> Rules { get; set; } = () => ClassicProgression.Badges;

    /// <summary>
    /// CLASSIC: the Ranked arena's ranks (classic-data/pvp); tests replace them.
    /// </summary>
    internal static Func<IReadOnlyList<Imlight.Classic.Pvp.ArenaRank>?> ArenaRanks { get; set; } = () => Arena.ClassicArena.Config?.Ranks;

    /// <summary>
    /// The id the client uses for a badge (MSG_BADGES.BadgeNameID, MSG_SELECT_BADGE.BadgeNameID).
    /// </summary>
    internal static uint NameId(Badge badge) => StringHash.Compute(badge.NameKey);

    /// <summary>
    /// The wizard's badges, in data order.
    /// </summary>
    internal static List<Badge> Earned(BadgeRules rules, Wizard wizard)
        => [.. rules.Badges.Where(badge => Has(wizard, badge))];

    internal static bool Has(Wizard wizard, Badge badge)
        => wizard.QuestBehavior?.Registry.TryGetValue(BadgeRules.BadgeKey(badge.Id), out var value) == true && value > 0;

    /// <summary>
    /// A quest completed: awards the badges it finishes.
    /// </summary>
    internal static void QuestCompleted(Wizard wizard, string questName, Action<IMessage> send) {
        if (Rules() is not { } rules || wizard?.QuestBehavior is null || string.IsNullOrEmpty(questName)) {
            return;
        }

        if (UsesSavedRegistry) {
            AwardSavedRegistry(wizard, rules.ForQuest(questName), [], send);
            return;
        }
        AwardEarned(wizard, rules.ForQuest(questName), send);
    }

    /// <summary>
    /// CLASSIC: stages quest badges on an already completion-stamped saved wizard, without saving or publishing.
    /// The caller commits this registry with the quest claim and mirrors it to the live wizard after acknowledgement.
    /// </summary>
    internal static IReadOnlyList<PreparedQuestBadgeAward> StageQuestCompleted(Wizard saved, string canonicalName) {
        if (Rules() is not { } rules || saved?.QuestBehavior is null || string.IsNullOrEmpty(canonicalName)) {
            return [];
        }

        var progress = new WizardBadgeProgress(saved);
        var awards = new List<PreparedQuestBadgeAward>();
        foreach (var badge in rules.ForQuest(canonicalName)) {
            if (TryStageAward(saved, badge, progress) is { } award) {
                awards.Add(award);
            }
        }

        return awards;
    }

    /// <summary>
    /// CLASSIC: publishes prepared awards only after the caller acknowledged its save and mirrored the registry.
    /// This method neither changes the registry nor opens another database write.
    /// </summary>
    internal static void PublishQuestCompleted(Wizard live, IReadOnlyList<PreparedQuestBadgeAward> awards,
                                               Action<IMessage> send) {
        foreach (var award in awards) {
            Awarded(live, award.Badge);
            send(award.Message);
        }
    }

    /// <summary>
    /// A duel was won: counts every defeated mob under each adjective a kill badge counts, and awards the badges reached.
    /// </summary>
    internal static void MobsDefeated(Wizard wizard, IEnumerable<ulong>? mobTemplateIds, Action<IMessage> send) {
        if (Rules() is not { } rules || wizard?.QuestBehavior is null || mobTemplateIds is null) {
            return;
        }

        var counted = new HashSet<string>(rules.CountedAdjectives, StringComparer.Ordinal);
        var touched = new List<Badge>();
        var increments = new List<string>();
        foreach (var templateId in mobTemplateIds) {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
                continue;
            }

            foreach (var adjective in (template.m_adjectiveList ?? []).Where(counted.Contains).Distinct()) {
                increments.Add(BadgeRules.KillCounterKey(adjective));
                if (!UsesSavedRegistry) wizard.QuestBehavior.Registry.AddOrUpdate(BadgeRules.KillCounterKey(adjective), 1UL, (_, count) => count + 1);
                touched.AddRange(rules.ForAdjective(adjective));
            }
        }

        if (touched.Count == 0) {
            return;
        }

        if (UsesSavedRegistry) {
            AwardSavedRegistry(wizard, touched.Distinct(), increments, send);
            return;
        }
        Persist(wizard);
        AwardEarned(wizard, touched.Distinct(), send);
    }

    /// <summary>
    /// A zone was entered: counts the visit if a badge counts that zone, and awards the badges reached.
    /// </summary>
    internal static void ZoneEntered(Wizard wizard, string? zone, Action<IMessage> send) {
        if (Rules() is not { } rules || wizard?.QuestBehavior is null || string.IsNullOrEmpty(zone)) {
            return;
        }

        var badges = rules.ForZone(zone);
        if (badges.IsEmpty) {
            return;
        }

        if (UsesSavedRegistry) {
            AwardSavedRegistry(wizard, badges, [BadgeRules.ZoneVisitKey(zone)], send);
            return;
        }
        wizard.QuestBehavior.Registry.AddOrUpdate(BadgeRules.ZoneVisitKey(zone), 1UL, (_, count) => count + 1);
        Persist(wizard);
        AwardEarned(wizard, badges, send);
    }

    /// <summary>
    /// CLASSIC: a Ranked result (or attach): awards the PvP rank badges <paramref name="rating"/> reaches. A rank badge,
    /// once earned, stays earned.
    /// </summary>
    internal static void PvpRatingChanged(Wizard wizard, int rating, Action<IMessage> send) {
        if (Rules() is not { } rules || wizard?.QuestBehavior is null || rules.PvpRankBadges.IsEmpty) {
            return;
        }

        Reevaluate(wizard, rules.PvpRankBadges, view => new WizardBadgeProgress(view, wizard, rating), send);
    }

    /// <summary>
    /// CLASSIC: a spell was learned (or attach): awards the learn-every-spell-of-your-school badge it completes.
    /// </summary>
    internal static void SpellsChanged(Wizard wizard, Action<IMessage> send) {
        if (Rules() is not { } rules || wizard?.QuestBehavior is null || wizard.SpellbookBehavior is null
                || rules.SchoolSpellBadges.IsEmpty) {
            return;
        }

        Reevaluate(wizard, rules.SchoolSpellBadges, view => new WizardBadgeProgress(view, wizard), send);
    }

    private static void Reevaluate(Wizard wizard, IEnumerable<Badge> candidates, Func<Wizard, IBadgeProgress> progressFor,
        Action<IMessage> send) {
        var selected = candidates.Where(badge => !Has(wizard, badge)).ToArray();
        if (selected.Length == 0 || !selected.Any(badge => BadgeRules.IsEarned(badge, progressFor(wizard)))) {
            return; // CLASSIC: no write when nothing new is earned (the common case on attach).
        }

        if (UsesSavedRegistry) {
            AwardSavedRegistry(wizard, selected, [], send, progressFor);
            return;
        }
        var progress = progressFor(wizard);
        foreach (var badge in selected) {
            if (TryStageAward(wizard, badge, progress) is not { } award) continue;
            Persist(wizard);
            PublishQuestCompleted(wizard, [award], send);
        }
    }

    // CLASSIC: counters and the badges they earn share one fresh registry ACK. The existing rule-test Persist
    // hook remains explicit; production never sends or changes live registry before this selected transaction.
    private static void AwardSavedRegistry(Wizard live, IEnumerable<Badge> candidates,
        IReadOnlyList<string> increments, Action<IMessage> send, Func<Wizard, IBadgeProgress>? progressFor = null) {
        var selected = candidates.ToArray();
        var awards = new List<PreparedQuestBadgeAward>();
        WizardQuestTransactions.TryChangeRegistry(live, journal => {
            foreach (var key in increments) journal.Registry.AddOrUpdate(key, 1UL, (_, count) => count + 1);
            var savedView = new Wizard { QuestBehavior = journal };
            var progress = progressFor?.Invoke(savedView) ?? new WizardBadgeProgress(savedView);
            foreach (var badge in selected) {
                if (TryStageAward(savedView, badge, progress) is { } award) awards.Add(award);
            }
            return increments.Count > 0 || awards.Count > 0;
        }, out _, preparePublication: _ => awards.All(award => WizardProgressionTransactions.Prepare(award.Message)),
            afterCommit: _ => PublishQuestCompleted(live, awards, send));
    }

    private static void AwardEarned(Wizard wizard, IEnumerable<Badge> candidates, Action<IMessage> send) {
        var progress = new WizardBadgeProgress(wizard);
        foreach (var badge in candidates) {
            if (TryStageAward(wizard, badge, progress) is not { } award) { // CLASSIC: same staging as a quest claim.
                continue;
            }

            Persist(wizard);
            PublishQuestCompleted(wizard, [award], send);
        }
    }

    private static PreparedQuestBadgeAward? TryStageAward(Wizard wizard, Badge badge, IBadgeProgress progress) { // CLASSIC
        if (!badge.IsGranted || Has(wizard, badge) || !BadgeRules.IsEarned(badge, progress)) {
            return null;
        }

        // TryAdd: of two actors earning the same badge at once, only one awards it. An existing zero still blocks it.
        if (!wizard.QuestBehavior.Registry.TryAdd(BadgeRules.BadgeKey(badge.Id), 1)) {
            return null;
        }

        return new PreparedQuestBadgeAward(badge, AddMessage(badge));
    }

    /// <summary>
    /// MSG_BADGES announcing one new badge.
    /// </summary>
    internal static GAME_5_PROTOCOL.MSG_BADGES AddMessage(Badge badge) => new() {
        CurrentBadge = 0,
        UpdateAll = 0,
        TotalBadges = 1,
        Add = 1,
        Remove = 0,
        BadgeName = badge.NameKey,
        BadgeInfo = badge.DescriptionKey ?? "",
        BadgeNameID = NameId(badge),
        BadgeFilterInfo = badge.FilterKey,
        Display = 1,
        LastSegment = 1,
    };

    /// <summary>
    /// The MSG_BADGES that list every earned badge, one per badge (an empty list is one message).
    /// </summary>
    internal static List<GAME_5_PROTOCOL.MSG_BADGES> ListMessages(IReadOnlyList<Badge> earned) {
        if (earned.Count == 0) {
            return [new() { UpdateAll = 1, TotalBadges = 0, BadgeName = "", BadgeInfo = "", BadgeFilterInfo = "", LastSegment = 1 }];
        }

        return [.. earned.Select((badge, index) => new GAME_5_PROTOCOL.MSG_BADGES {
            CurrentBadge = (uint) index,
            UpdateAll = (sbyte) (index == 0 ? 1 : 0),
            TotalBadges = (uint) earned.Count,
            Add = 0,
            Remove = 0,
            BadgeName = badge.NameKey,
            BadgeInfo = badge.DescriptionKey ?? "",
            BadgeNameID = NameId(badge),
            BadgeFilterInfo = badge.FilterKey,
            Display = 0,
            LastSegment = (byte) (index == earned.Count - 1 ? 1 : 0),
        })];
    }

    // CLASSIC: wizard holds the (saved) quest registry; live supplies the spellbook and school; rating the Ranked rating.
    private sealed class WizardBadgeProgress(Wizard wizard, Wizard? live = null, int? rating = null) : IBadgeProgress {

        public bool ReachedPvpRank(string rank)
            => rating is { } value && ArenaRanks() is { } ranks
                && ranks.Any(r => r.Name == rank)
                && value >= Imlight.Classic.Pvp.ArenaRules.MinRatingOf(rank, ranks);

        public string? PrimarySchool => ((live ?? wizard).MagicSchoolBehavior?.MagicSchool is { } school and not MagicSchool.None
                ? school : (live ?? wizard).SpellbookBehavior?.PrimarySchool) switch {
            MagicSchool.Fire => "fire", MagicSchool.Ice => "ice", MagicSchool.Storm => "storm", MagicSchool.Myth => "myth",
            MagicSchool.Life => "life", MagicSchool.Death => "death", MagicSchool.Balance => "balance",
            _ => null,
        };

        public bool IsSpellAvailable(string spellId)
            => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
                && ClassicSpellTemplates.Records.FirstOrDefault(r => r.Id == spellId) is { ClientTemplate: not null } record
                && record.IsInProfile(ClassicRuntime.Rules.Profile.Id);

        public bool KnowsSpell(string spellId)
            => ClassicSpellTemplates.Records.FirstOrDefault(r => r.Id == spellId)?.ClientTemplate is { } path
                && CoreObjectFactory.TryGetTemplateIdByPath(path) is { } id
                && (live ?? wizard).SpellbookBehavior?.HasSpell((uint) id) == true;

        public bool HasCompletedQuest(string quest) => wizard.QuestBehavior.HasCompletedQuest(quest);

        public int KillCount(string adjective) => (int) Math.Min(int.MaxValue, wizard.GetRegistryValue(BadgeRules.KillCounterKey(adjective)));

        public int ZoneVisits(string zone) => (int) Math.Min(int.MaxValue, wizard.GetRegistryValue(BadgeRules.ZoneVisitKey(zone)));

    }

}
