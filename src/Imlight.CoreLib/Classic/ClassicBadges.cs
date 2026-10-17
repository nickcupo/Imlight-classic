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
 * key.
 *
 * MSG_BADGES as the r806919 client reads it (2026-10-17, Ghidra:
 * WizardGraphicalClient handler 0x1413482d0, GameClient 0x14170f040):
 * BadgeFilterInfo is always passed through SerializerBinary::Uncompress
 * (0x141454f80: int32 length, then zlib) and loaded as a BadgeFilterInfoList.
 * With Add = 1 and Remove = 0, BadgeInfo is uncompressed the same way and
 * loaded as a BadgeInfoList; each BadgeInfo is added to the badge page, and
 * with UpdateAll = 0 the client also shows the earned popup from its
 * m_badgeTitle. Add = 0 with Remove = 0 adds nothing. UpdateAll = 1 sets
 * TotalBadges and CurrentBadge, which is the m_badgeNameID the page marks as
 * selected (BadgeButton::SetBadge 0x14067b9a0). Filter ids are the string
 * hash of BadgeFilterDescriptions.xml's m_filterName (0x1417837c0). Until
 * then the list went out as one Add = 0 message per badge with text keys,
 * which this client ignored: the badge page stayed empty and no popup showed.
 *
 * TODO:
 * - Unverified: m_numberOfBadges (sent as the earned count per filter) and
 *   the empty m_badgeImage/m_badgeOutline (the badge button does not read them).
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
using Imcodec.IO;
using Imcodec.ObjectProperty;
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
    /// CLASSIC: the client's badge filter name for a filter display key (BadgeFilterDescriptions.xml in Root.wad), or null;
    /// tests replace it.
    /// </summary>
    internal static Func<string, string?> FilterName { get; set; } = DefaultFilterName;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_filterNames = new(LoadFilterNames);

    // Only once the server has opened Root.wad (at startup, CoreObjectFactory): never opens it itself.
    private static string? DefaultFilterName(string displayKey)
        => displayKey is not null && RootArchiveLoader.IsLoaded && s_filterNames.Value.TryGetValue(displayKey, out var name) ? name : null;

    private static IReadOnlyDictionary<string, string> LoadFilterNames() {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try {
            if (RootArchiveLoader.GetFile<BadgeFilterDescriptionList>("BadgeFilterDescriptions.xml") is { } list) {
                foreach (var filter in list.m_badgeFilterDescriptionList ?? []) {
                    if (!string.IsNullOrEmpty(filter?.m_displayKey) && !string.IsNullOrEmpty(filter.m_filterName)) {
                        names.TryAdd(filter.m_displayKey, filter.m_filterName);
                    }
                }
            }
        }
        catch (Exception ex) {
            try { Logger.Warning("Badges: could not read BadgeFilterDescriptions.xml: {0}", Logger.Args(ex.Message)); }
            catch (Exception) { /* no server configuration (tests) */ }
        }

        return names;
    }

    /// <summary>
    /// The client's id of a badge's filter (BadgeInfo.m_badgeFilterID, BadgeFilterInfo.m_badgeFilterNameID): the string
    /// hash of the filter's name; 0 when the client does not know the filter.
    /// </summary>
    internal static uint FilterId(Badge badge)
        => FilterName(badge.FilterKey) is { Length: > 0 } name ? StringHash.Compute(name) : 0;

    /// <summary>
    /// One earned badge as the client's badge page holds it.
    /// </summary>
    internal static BadgeInfo Info(Badge badge, uint index, uint filterIndex) => new() {
        m_badgeNameID = NameId(badge),
        m_badgeTitle = string.IsNullOrEmpty(badge.TitleKey) ? badge.NameKey : badge.TitleKey,
        m_badgeInfo = badge.DescriptionKey ?? "",
        m_badgeProgressInfo = "",
        m_badgeProgress = 0,
        m_badgeMax = 0,
        m_badgeComplete = true,
        m_index = index,
        m_badgeOutline = "",
        m_badgeImage = "",
        m_dynamicBadge = false,
        m_badgeFilterID = FilterId(badge),
        m_filterIndex = filterIndex,
        m_overcount = false,
        m_hasLoot = false,
        m_badgeLoot = "",
        m_extraCredit = false,
        m_overcountInterval = 0,
        m_recurringRewardsDescription = "",
        m_overcountLoot = "",
        m_originalProgress = 0,
        m_rewardOverrideDescription = "",
    };

    /// <summary>
    /// An object as MSG_BADGES carries it: binary (no flags), compressed the way SerializerBinary::Uncompress reads it.
    /// </summary>
    internal static ByteString Blob(PropertyClass value) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.Compress);
        if (!serializer.Serialize(value, (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit), out var data)) {
            throw new InvalidOperationException($"Badges: could not serialize {value?.GetType().Name}.");
        }

        return data;
    }

    private static BadgeFilterInfoList FilterInfo(IReadOnlyList<Badge> earned) => new() {
        m_badgeFilterInfoList = [.. earned.GroupBy(FilterId).Where(group => group.Key != 0).Select(group => new BadgeFilterInfo {
            m_badgeFilterNameID = group.Key,
            m_numberOfBadges = group.Count(),
            m_numberOfExtraCreditBadges = 0,
        })],
    };

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
        UpdateAll = 0, // the client shows the earned popup for each badge it adds outside a full update
        TotalBadges = 1,
        Add = 1,
        Remove = 0,
        BadgeName = badge.NameKey,
        BadgeInfo = Blob(new BadgeInfoList { m_badges = [Info(badge, 0, 0)] }),
        BadgeNameID = NameId(badge),
        BadgeFilterInfo = Blob(FilterInfo([badge])),
        Display = 1,
        LastSegment = 1,
    };

    /// <summary>
    /// The MSG_BADGES that list every earned badge: one full update (UpdateAll = 1, Add = 1) carrying them all, with the
    /// selected badge's id as CurrentBadge; with no badges, one update that adds nothing.
    /// </summary>
    /// <param name="earned">The wizard's badges, in data order.</param>
    /// <param name="selectedTitle">The title the wizard chose (PlayerNameBehavior.BadgeTitle), or null.</param>
    internal static List<GAME_5_PROTOCOL.MSG_BADGES> ListMessages(IReadOnlyList<Badge> earned, string? selectedTitle = null) {
        var selected = string.IsNullOrEmpty(selectedTitle) ? null : earned.FirstOrDefault(badge => badge.TitleKey == selectedTitle);
        var current = selected is null ? 0u : NameId(selected);
        var filters = Blob(FilterInfo(earned));
        if (earned.Count == 0) {
            return [new() {
                UpdateAll = 1, TotalBadges = 0, CurrentBadge = 0, Add = 0, Remove = 0, BadgeName = "", BadgeInfo = "",
                BadgeFilterInfo = filters, Display = 0, LastSegment = 1,
            }];
        }

        var inFilter = new Dictionary<uint, uint>();
        var infos = new List<BadgeInfo>(earned.Count);
        for (var i = 0; i < earned.Count; i++) {
            var filter = FilterId(earned[i]);
            var position = inFilter.GetValueOrDefault(filter);
            inFilter[filter] = position + 1;
            infos.Add(Info(earned[i], (uint) i, position));
        }

        // MSG fields are STR (16-bit length): a list too long for one message goes out in several full updates, each a
        // whole BadgeInfoList (never a partial segment: the client joins those with a buffer it keeps).
        var messages = new List<GAME_5_PROTOCOL.MSG_BADGES>();
        foreach (var part in Chunks(infos)) {
            messages.Add(new GAME_5_PROTOCOL.MSG_BADGES {
                CurrentBadge = current,
                UpdateAll = 1,
                TotalBadges = (uint) earned.Count,
                Add = 1,
                Remove = 0,
                BadgeName = "",
                BadgeInfo = part,
                BadgeNameID = 0,
                BadgeFilterInfo = filters,
                Display = 0,
                LastSegment = 1,
            });
        }

        return messages;
    }

    /// <summary>The longest compressed BadgeInfoList one MSG_BADGES carries; tests lower it.</summary>
    internal static int MaxBlobBytes { get; set; } = 60_000;

    private static IEnumerable<ByteString> Chunks(List<BadgeInfo> infos) {
        var blob = Blob(new BadgeInfoList { m_badges = infos });
        if (blob.Length <= MaxBlobBytes || infos.Count < 2) {
            yield return blob;
            yield break;
        }

        var half = infos.Count / 2;
        foreach (var part in Chunks(infos[..half])) yield return part;
        foreach (var part in Chunks(infos[half..])) yield return part;
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
