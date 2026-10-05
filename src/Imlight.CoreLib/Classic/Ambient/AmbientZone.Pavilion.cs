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
 * AMBIENT ZONE: PET PAVILION AND BAZAAR
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): the part of an AmbientZone that makes its wizards
 * take part in hatching and the Bazaar.
 *   - Pet Pavilion (AmbientSettings.PetPavilionZone, AmbientWizardHatching):
 *     a wizard in a hatch (AmbientHatchNews from AmbientHatching) stops
 *     and stays on the spot, says hello and thanks, and goes back to its
 *     day when it is over. With real players around, now and then (every
 *     4-8 minutes for the whole room) a free wizard whose pet may hatch
 *     offers a hatch in chat and is held for the first taker; a player who
 *     asks ("anyone hatch?") gets one wizard's yes and that wizard is held
 *     for them (AmbientPets.PromiseLimit).
 *   - The Bazaar (AmbientSettings.BazaarZone, AmbientWizardBazaar): each
 *     wizard there trades at the counter every 3-8 minutes
 *     (ClassicBazaar.AmbientVisit: 1-3 sales or purchases, within the
 *     server-wide hourly budget), off the actor.
 *
 * USAGE EXAMPLE:
 * (called from AmbientZone's constructor and PreStart)
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Classic.Ambient;
using Imlight.Common;

namespace Imlight.CoreLib.Classic.Ambient;

internal sealed partial class AmbientZone {

    private sealed record PavilionTick;
    private sealed record BazaarTick;

    private readonly Dictionary<ulong, DateTime> _hatchAnswered = [];
    private readonly Dictionary<ulong, DateTime> _nextBazaarVisit = [];
    private DateTime _nextHatchOffer;

    private bool IsPavilion => string.Equals(_zone, AmbientSettings.PetPavilionZone, StringComparison.OrdinalIgnoreCase);
    private bool IsBazaar => string.Equals(_zone, AmbientSettings.BazaarZone, StringComparison.OrdinalIgnoreCase);

    private void ReceivePavilionAndBazaar() {
        Receive<AmbientHatchNews>(OnHatchNews);
        Receive<PavilionTick>(_ => OnPavilionTick());
        Receive<BazaarTick>(_ => OnBazaarTick());
    }

    private void StartPavilionAndBazaar() {
        var now = DateTime.UtcNow;
        if (IsPavilion && AmbientWizards.Settings.Hatching) {
            _nextHatchOffer = now.AddMinutes(2 + _rng.Next(3));
            Timers.StartPeriodicTimer("pavilion", new PavilionTick(), TimeSpan.FromSeconds(20));
        }

        if (IsBazaar && AmbientWizards.Settings.Bazaar) {
            foreach (var wizard in _wizards) {
                // The first visits are spread out, as people drift in.
                _nextBazaarVisit[wizard.CharId] = now.AddSeconds(60 + _rng.Next(AmbientBazaarTrader.VisitMaxMinutes * 60));
            }

            Timers.StartPeriodicTimer("bazaar", new BazaarTick(), TimeSpan.FromSeconds(30));
        }
    }

    // ---- hatching -------------------------------------------------------------------------------

    private void OnHatchNews(AmbientHatchNews news) {
        var wizard = news.Wizard;
        if (!_wizards.Contains(wizard)) {
            return;
        }

        var plan = AmbientHatching.PlanOf(wizard);
        var pet = AmbientPets.NameOf(plan.TemplateId);
        var now = DateTime.UtcNow;
        switch (news.What) {
            case AmbientHatchEnd.Joined:
                // Stop where it is (a hatching spot is a few steps away at most) and wait for the hatch.
                if (wizard.Moving) {
                    Halt(wizard);
                }

                wizard.Route.Clear();
                wizard.Target = null;
                wizard.Activity = AmbientActivity.Hatching;
                wizard.Until = now.AddSeconds(5);
                Talk(wizard, news.PlayerCharId, AmbientPets.Talk.Joined, pet, plan.Level, now);
                break;
            case AmbientHatchEnd.Hatched:
            case AmbientHatchEnd.PlayerLeft:
            case AmbientHatchEnd.TimedOut:
                if (wizard.Activity == AmbientActivity.Hatching) {
                    wizard.Activity = AmbientActivity.Idle;
                    wizard.Until = now.AddSeconds(6 + _rng.Next(10));
                }

                if (news.What != AmbientHatchEnd.PlayerLeft) {
                    Talk(wizard, news.PlayerCharId,
                        news.What == AmbientHatchEnd.Hatched ? AmbientPets.Talk.Done : AmbientPets.Talk.Gone, pet, plan.Level, now);
                }

                break;
        }
    }

    private void OnPavilionTick() {
        var now = DateTime.UtcNow;
        if (now < _nextHatchOffer || _realPlayers == 0 || !AmbientHatching.Enabled || !AmbientWizards.Settings.Chat) {
            return;
        }

        _nextHatchOffer = now.AddMinutes(4 + _rng.Next(5));
        var nowUnix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var free = _wizards.Where(w => AmbientHatching.IsFree(w, nowUnix)).ToList();
        if (free.Count == 0) {
            return;
        }

        var wizard = free[_rng.Next(free.Count)];
        var plan = AmbientHatching.PlanOf(wizard);
        if (Talk(wizard, 0, AmbientPets.Talk.Offer, AmbientPets.NameOf(plan.TemplateId), plan.Level, now)) {
            // Held for the players who heard it (each gets this wizard first when they step on a hatching spot; the
            // first to do so takes it, and the reservation keeps it to one).
            foreach (var player in _audience) {
                AmbientHatching.Promise(player, wizard, now, replace: false);
            }
        }
    }

    /// <summary>A player asked around for a hatch: one wizard answers (at most once a minute a player) and is held for them.</summary>
    private bool HatchAsked(AmbientWizard wizard, ulong speaker, string text, DateTime now) {
        if (!IsPavilion || !AmbientHatching.Enabled || !AmbientPets.AsksForHatch(text)) {
            return false;
        }

        if (_hatchAnswered.TryGetValue(speaker, out var last) && now - last < TimeSpan.FromMinutes(1)) {
            return true; // another wizard of the room has the answer in hand
        }

        var nowUnix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var answerer = AmbientHatching.IsFree(wizard, nowUnix) ? wizard
            : _wizards.Where(w => AmbientHatching.IsFree(w, nowUnix)).OrderBy(_ => _rng.Next()).FirstOrDefault();
        if (answerer is null) {
            return false; // nobody can; the usual reply (if any)
        }

        _hatchAnswered[speaker] = now;
        if (_nextHatchOffer < now.AddMinutes(3)) {
            _nextHatchOffer = now.AddMinutes(3); // someone just said yes; no open offer on top of it
        }

        AmbientHatching.Promise(speaker, answerer, now);
        var plan = AmbientHatching.PlanOf(answerer);
        var pet = AmbientPets.NameOf(plan.TemplateId);
        Timers.StartSingleTimer($"hatch-yes-{answerer.CharId}-{speaker}", new Later(answerer,
            w => Talk(w, speaker, AmbientPets.Talk.Answer, pet, plan.Level, DateTime.UtcNow)), TimeSpan.FromSeconds(2 + _rng.Next(3)));
        return true;
    }

    private bool Talk(AmbientWizard wizard, ulong player, AmbientPets.Talk kind, string pet, int level, DateTime now) {
        if (!AmbientWizards.Settings.Chat || !wizard.Limiter.TryTake(now, player)) {
            return false;
        }

        var line = AmbientPets.Line(kind, wizard.Turn++ + wizard.Identity.Seed, pet, level);
        if (player != 0) {
            Say(wizard, player, line);
        }
        else {
            AmbientChat.Say(wizard, line);
        }

        return true;
    }

    // ---- the Bazaar ---------------------------------------------------------------------------

    private void OnBazaarTick() {
        if (!AmbientWizards.Settings.Bazaar) {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var wizard in _wizards.Where(w => w.Present && w.Activity is AmbientActivity.Idle or AmbientActivity.Shopping
                                                   or AmbientActivity.Walking)) {
            if (_nextBazaarVisit.GetValueOrDefault(wizard.CharId) > now) {
                continue;
            }

            _nextBazaarVisit[wizard.CharId] = now.AddSeconds(
                _rng.Next(AmbientBazaarTrader.VisitMinMinutes * 60, AmbientBazaarTrader.VisitMaxMinutes * 60 + 1));
            var who = wizard.Name;
            var seed = _rng.Next();
            Task.Run(() => ClassicBazaar.AmbientVisit(who, new Random(seed))).ContinueWith(task => {
                if (task.IsFaulted) {
                    Logger.Warning("Ambient Bazaar visit by {Name} failed: {Error}",
                        Logger.Args(who, task.Exception?.GetBaseException().Message));
                }
            }, TaskScheduler.Default);
        }
    }

}
