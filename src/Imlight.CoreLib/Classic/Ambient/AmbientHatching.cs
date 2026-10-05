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
 * AMBIENT HATCHING
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): ambient wizards as hatch partners at the Pet
 * Pavilion. Each has a pet (AmbientPets: one of the 2009 pet shop's
 * eight, grown to its level with talents learned the usual way), kept in
 * memory only. A player who waits alone on a hatching spot (PetGameMorph)
 * may claim a free ambient wizard of the same zone whose pet may hatch
 * (Adult or older, not hatched in the last 24 hours); the claim is one
 * atomic reservation, so one wizard never hatches with two players. The
 * player's PetGameService drives the partner's side (AmbientPets' waits)
 * and releases it when the hatch is done, the player leaves, or the
 * player has not confirmed within AmbientPets.HoldLimit. A wizard that
 * hatched stamps its pet's 24-hour cooldown in its stored record.
 * Promises: an ambient wizard that offered a hatch in chat, or answered a
 * player's "hatch?", is held for that player for AmbientPets.PromiseLimit.
 *
 * USAGE EXAMPLE:
 * var partner = AmbientHatching.Claim(zone, playerCharId, DateTime.UtcNow);
 * if (partner is not null) { var parent = AmbientHatching.ParentOf(partner); ... AmbientHatching.Release(partner, player, AmbientHatchEnd.Hatched); }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Ambient;
using Imlight.Classic.Pets;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>How an ambient wizard's part in a hatch ended.</summary>
internal enum AmbientHatchEnd { Joined, Hatched, PlayerLeft, TimedOut }

/// <summary>A hatch event for the ambient wizard's zone actor (it stands still, talks, and goes back to its day).</summary>
internal sealed record AmbientHatchNews(AmbientWizard Wizard, ulong PlayerCharId, AmbientHatchEnd What);

/// <summary>Ambient wizards as hatch partners (see the file header).</summary>
internal static class AmbientHatching {

    private static readonly ConcurrentDictionary<ulong, ulong> s_busy = new();                       // ambient -> player
    private static readonly ConcurrentDictionary<ulong, (ulong Ambient, DateTime Until)> s_promises = new(); // player -> ambient
    private static readonly ConcurrentDictionary<ulong, WizClientObjectItem> s_pets = new();

    /// <summary>True when ambient wizards hatch on this server now (the switch and the profile's hatching).</summary>
    internal static bool Enabled
        => AmbientWizards.Settings.Hatching && AmbientWizards.Count > 0 && ProfileHatches();

    /// <summary>The profile's pet hatching switch (the Pet Pavilion's owner extra); tests replace it.</summary>
    internal static Func<bool> ProfileHatches { get; set; }
        = () => ClassicRuntime.IsInitialized && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsHatching);

    /// <summary>The wizard's pet plan (its seed's).</summary>
    internal static AmbientPet PlanOf(AmbientWizard wizard) => AmbientPets.For(wizard.Record.Seed);

    /// <summary>The wizard's pet item, made on first use (in memory only; never saved).</summary>
    internal static WizClientObjectItem PetOf(AmbientWizard wizard)
        => s_pets.GetOrAdd(wizard.CharId, _ => BuildPet(wizard.CharId, PlanOf(wizard)));

    /// <summary>The pet grown as planned: hatched, experience to its level (talents learned on the way), stats trained.</summary>
    internal static WizClientObjectItem BuildPet(ulong ownerCharId, AmbientPet plan) {
        var pet = PetFactory.CreateHatchedPet(ownerCharId, (uint) plan.TemplateId);
        if (pet is null || PetProgress.Behavior(pet) is not { } b) {
            return null;
        }

        var thresholds = PetProgress.Thresholds((uint) plan.TemplateId);
        var random = new Random(plan.GrowthSeed);
        var target = thresholds[Math.Clamp(plan.Level, PetRules.Baby, PetRules.MaxLevel)];
        if (target > 0) {
            PetProgress.AddXp(pet, target + random.Next(0, 40), random);
        }

        var start = PetProgress.Stats(b.m_currentStats);
        var max = PetProgress.Stats(b.m_maxStats);
        b.m_currentStats = [.. PetRules.StatNames.Select(name => new PetStat {
            m_name = name, m_statID = PetProgress.StatId(name),
            m_value = AmbientPets.TrainedStat(start.GetValueOrDefault(name, 1), max.GetValueOrDefault(name, 1), plan.TrainedShare),
        })];
        PetProgress.EnsureInitialized(pet);
        return pet;
    }

    /// <summary>The wizard's side of a hatch, as PetHatchRules sees a parent.</summary>
    internal static HatchParent ParentOf(AmbientWizard wizard) {
        var pet = PetOf(wizard);
        if (PetProgress.Behavior(pet) is not { } b || b.m_level == 0) {
            return null;
        }

        return new HatchParent(pet.m_templateID.Full, b.m_level, PetProgress.Stats(b.m_maxStats),
            [.. (b.m_allTalents ?? []).Select(PetProgress.TalentName).Where(n => n is not null)],
            [.. (b.m_expressedTalents ?? []).Select(PetProgress.TalentName).Where(n => n is not null)],
            (int) b.m_overallRating, wizard.Record.PetLastHatchUnix);
    }

    /// <summary>True when the wizard could hatch now: present, not busy, its pet Adult or older and off its cooldown.</summary>
    internal static bool IsFree(AmbientWizard wizard, long nowUnix)
        => wizard.Present && !s_busy.ContainsKey(wizard.CharId)
           && wizard.Activity is AmbientActivity.Idle or AmbientActivity.Walking or AmbientActivity.Shopping or AmbientActivity.Following
           && AmbientPets.MayHatch(PlanOf(wizard).Level, wizard.Record.PetLastHatchUnix, nowUnix);

    /// <summary>
    /// Reserves a partner in <paramref name="zone"/> for <paramref name="playerCharId"/>: the one promised to the player,
    /// else a free one at random. Null when none is free.
    /// </summary>
    internal static AmbientWizard Claim(string zone, ulong playerCharId, DateTime now, Random random = null) {
        if (!Enabled || string.IsNullOrEmpty(zone) || AmbientWizards.IsAmbientChar(playerCharId)) {
            return null;
        }

        var nowUnix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var here = AmbientWizards.All.Where(w => string.Equals(w.Zone, zone, StringComparison.OrdinalIgnoreCase)).ToList();
        if (s_promises.TryRemove(playerCharId, out var promise) && promise.Until >= now
            && here.FirstOrDefault(w => w.CharId == promise.Ambient) is { } promised && IsFree(promised, nowUnix)
            && s_busy.TryAdd(promised.CharId, playerCharId)) {
            return Joined(promised, playerCharId);
        }

        var promisedToOthers = s_promises.Values.Where(p => p.Until >= now).Select(p => p.Ambient).ToHashSet();
        var free = here.Where(w => IsFree(w, nowUnix) && !promisedToOthers.Contains(w.CharId)).ToList();
        random ??= Random.Shared;
        while (free.Count > 0) {
            var pick = free[random.Next(free.Count)];
            if (s_busy.TryAdd(pick.CharId, playerCharId)) {
                return Joined(pick, playerCharId);
            }

            free.Remove(pick);
        }

        return null;
    }

    private static AmbientWizard Joined(AmbientWizard wizard, ulong player) {
        Logger.Information("Ambient hatching: {Name} ({Pet}, {Stage}) takes the other hatching spot for {Player}.",
            Logger.Args(Who(wizard), AmbientPets.NameOf(PlanOf(wizard).TemplateId), PetRules.LevelName(PlanOf(wizard).Level), player));
        Tell(wizard, player, AmbientHatchEnd.Joined);
        return wizard;
    }

    /// <summary>
    /// Frees the wizard (once; later calls do nothing). After a hatch its pet's cooldown is stamped and stored.
    /// </summary>
    internal static void Release(AmbientWizard wizard, ulong playerCharId, AmbientHatchEnd end) {
        if (wizard is null || !s_busy.TryRemove(new System.Collections.Generic.KeyValuePair<ulong, ulong>(wizard.CharId, playerCharId))) {
            return;
        }

        if (end == AmbientHatchEnd.Hatched) {
            wizard.Record.PetLastHatchUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AmbientWizardCollection.Save(wizard.Record);
        }

        Logger.Information("Ambient hatching: {Name} is done with {Player} ({End}).", Logger.Args(Who(wizard), playerCharId, end));
        Tell(wizard, playerCharId, end);
    }

    /// <summary>
    /// Holds <paramref name="wizard"/> for <paramref name="playerCharId"/>'s next hatch. An open offer
    /// (<paramref name="replace"/> false) never displaces a wizard that already said yes to that player.
    /// </summary>
    internal static void Promise(ulong playerCharId, AmbientWizard wizard, DateTime now, bool replace = true)
        => s_promises.AddOrUpdate(playerCharId, (wizard.CharId, now + AmbientPets.PromiseLimit),
            (_, old) => replace || old.Until < now ? (wizard.CharId, now + AmbientPets.PromiseLimit) : old);

    /// <summary>True when the wizard is in a hatch now.</summary>
    internal static bool IsBusy(ulong ambientCharId) => s_busy.ContainsKey(ambientCharId);

    private static void Tell(AmbientWizard wizard, ulong player, AmbientHatchEnd what)
        => wizard.Group?.Tell(new AmbientHatchNews(wizard, player, what), ActorRefs.NoSender);

    private static string Who(AmbientWizard wizard)
        => wizard.Wizard?.PlayerNameBehavior is null ? $"ambient {wizard.CharId:x}" : wizard.Name;

    /// <summary>Forgets reservations, promises and pets (tests).</summary>
    internal static void Reset() {
        s_busy.Clear();
        s_promises.Clear();
        s_pets.Clear();
    }

}
