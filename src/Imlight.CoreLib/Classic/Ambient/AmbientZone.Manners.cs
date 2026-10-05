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
 * AMBIENT ZONE: MANNERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): the "would a real player do this?" pass over the
 * street behaviour (owner: "polish the friendly wizards"):
 *   - Help offers (HelpManners): only a wizard that can see the duel
 *     (1200 units, a clear line over the zone's collision), a few seconds
 *     after it noticed, never when the player is plainly winning or said
 *     no lately (to any wizard in the zone). A wizard more than 500 away
 *     walks over first and asks from the edge of the circle; on a yes it
 *     sets off a moment later and walks in.
 *   - Talk: open chat from across the zone is not talk to it (1500
 *     units).
 *   - After a duel: the zone's clients are told the wizard left the duel
 *     (MSG_ENTERSTATE Idle, as a player's client announces its own; without
 *     it the client kept drawing the wizard at its duel spot), it lingers a
 *     few seconds, then always walks on (owner: "the friendly wizards just
 *     stand in the street after combat"). A duel that ended without telling
 *     the wizard (closed, despawned) or that ran for 20 minutes lets it go.
 *   - Where it stands (StreetManners): a few steps off doorways (the
 *     zone's arrival spots), side by side at a shop instead of inside
 *     another wizard, and out of any spot another wizard holds.
 *   - Standing: small turns at uneven times instead of a frozen pose.
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
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.MessageLayer;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>QA (.ambient call): up to <paramref name="Count"/> free wizards wander over near <paramref name="At"/>.</summary>
internal sealed record AmbientCall(Vector3 At, int Count);

internal sealed partial class AmbientZone {

    private sealed record SightReady(SightGrid Grid);
    private sealed record AskAbout(ulong Sigil, ulong Player);

    private readonly ZoneHelpMemory _helpMemory = new();
    private readonly Dictionary<AmbientWizard, AskAbout> _askAbout = [];
    private readonly HashSet<AmbientWizard> _afterDuel = [];
    private readonly Dictionary<AmbientWizard, DateTime> _fightSince = [];
    private SightGrid _sight;
    private List<System.Numerics.Vector2> _doorways;

    private void ReceiveManners() {
        Receive<SightReady>(ready => _sight = ready.Grid);
        Receive<AmbientCall>(OnCall);
    }

    /// <summary>QA (.ambient call): free wizards wander over to 400-800 units from the caller.</summary>
    private void OnCall(AmbientCall call) {
        var free = _wizards.Where(w => w.Present && w.Activity is AmbientActivity.Idle or AmbientActivity.Walking or AmbientActivity.Shopping
                                       && !_askAbout.ContainsKey(w))
            .OrderBy(w => Distance(w.Position, call.At)).Take(call.Count).ToList();
        foreach (var wizard in free) {
            if (wizard.DuelSigil == ulong.MaxValue) {
                wizard.DuelSigil = 0;
                AmbientWizards.RevokeJoin(wizard.Endpoint);
            }

            for (var attempt = 0; attempt < 10; attempt++) {
                var angle = _rng.NextDouble() * Math.PI * 2;
                var reach = 400 + _rng.NextDouble() * 400;
                var at = new Vector3(call.At.X + (float) (Math.Cos(angle) * reach), call.At.Y + (float) (Math.Sin(angle) * reach), call.At.Z);
                if (_nav?.Snap(Num(at), 150f) is not { } open || !CanSee(new Vector3(open.X, open.Y, open.Z), call.At)) {
                    continue;
                }

                var spot = new Vector3(open.X, open.Y, open.Z);
                if (!WalkTo(wizard, spot, AmbientActivity.Walking)) {
                    // Too far for one walk (the far end of a street): QA puts it a street block away first.
                    var back = new Vector3(call.At.X + (float) (Math.Cos(angle) * 1600), call.At.Y + (float) (Math.Sin(angle) * 1600), call.At.Z);
                    if (_nav.Snap(Num(back), 400f) is not { } start) {
                        continue;
                    }

                    Halt(wizard);
                    wizard.Position = new Vector3(start.X, start.Y, start.Z);
                    wizard.Wizard.Location = wizard.Position;
                    Send([Move(wizard)]);
                    if (!WalkTo(wizard, spot, AmbientActivity.Walking)) {
                        continue;
                    }
                }

                Logger.Information("Ambient wizard {Name} wanders over (QA call).", Logger.Args(wizard.Name));
                break;
            }
        }
    }

    private void StartManners() {
        var self = Self;
        AmbientNav.SightFor(_zone).ContinueWith(task => {
            if (task.IsCompletedSuccessfully && task.Result is { } sight) {
                self.Tell(new SightReady(sight));
            }
        }, TaskScheduler.Default);
    }

    /// <summary>A clear line between the two spots (no collision data: taken as clear; distance still applies).</summary>
    private bool CanSee(Vector3 from, Vector3 to) => _sight?.CanSee(Num(from), Num(to)) ?? true;

    // ---- help offers ----------------------------------------------------------------------------

    /// <summary>
    /// One ambient wizard that can see the duel (a friend of a player in it first) walks over if it must, then asks a
    /// player of the duel whether they want help (HelpManners). Called when the duel is announced, each round, and every
    /// few seconds while it runs and nobody has asked.
    /// </summary>
    private void TryOffer(AmbientDuelNotice notice) {
        // No chat, no offer: an ambient wizard never joins a real player's duel without asking first.
        if (!AmbientWizards.Settings.Battles || !AmbientWizards.Settings.Chat || notice.PlayerCharIds.Length == 0
            || _offeredDuels.Contains(notice.SigilId)) {
            return;
        }

        var now = DateTime.UtcNow;
        var seenFor = now - _helpMemory.Seen(notice.SigilId, now);
        var quiet = _helpMemory.AnyQuiet(notice.PlayerCharIds, now);
        AmbientWizard helper = null;
        var reasons = new List<string>();
        foreach (var wizard in _wizards.Where(w => w.Present && w.Activity is AmbientActivity.Idle or AmbientActivity.Walking
                                                       or AmbientActivity.Shopping or AmbientActivity.Following
                                                   && !_askAbout.ContainsKey(w))
                     .OrderByDescending(w => notice.PlayerCharIds.Any(p => w.FriendOf(p) is not null))
                     .ThenBy(w => Distance(w.Position, notice.Location))) {
            var distance = Distance(wizard.Position, notice.Location);
            var verdict = HelpManners.Judge(new OfferFacts(distance,
                distance <= HelpManners.ViewDistance && CanSee(wizard.Position, notice.Location), seenFor,
                HelpManners.ReactionTime(wizard.Identity.Seed), notice.Odds ?? DuelOdds.Unknown, notice.FreePlayerSlots,
                notice.Pvp, quiet));
            if (verdict == OfferVerdict.Offer) {
                helper = wizard;
                break;
            }

            reasons.Add($"{wizard.Name} {verdict} {(int) distance}");
            if (verdict is OfferVerdict.Pvp or OfferVerdict.Full or OfferVerdict.PlayerQuiet or OfferVerdict.WinningEasily) {
                break; // the same for every wizard
            }
        }

        if (helper is null) {
            Logger.Debug("Ambient wizards in {Zone}: no offer for duel {Sigil} yet: {Why}",
                Logger.Args(_zone, notice.SigilId, string.Join("; ", reasons)));
            return;
        }

        var player = notice.PlayerCharIds.FirstOrDefault(p => helper.FriendOf(p) is not null);
        if (player == 0) {
            player = notice.PlayerCharIds[0];
        }

        if (!helper.Offers.MayOffer(player, now)) {
            return;
        }

        _offeredDuels.Add(notice.SigilId);
        if (helper.DuelSigil == ulong.MaxValue) {
            helper.DuelSigil = 0; // it was hunting; this comes first
            AmbientWizards.RevokeJoin(helper.Endpoint);
        }

        if (HelpManners.ApproachSpot(helper.Position.X, helper.Position.Y, notice.Location.X, notice.Location.Y) is { } spot
            && WalkTo(helper, new Vector3(spot.X, spot.Y, notice.Location.Z), AmbientActivity.Walking)) {
            _askAbout[helper] = new AskAbout(notice.SigilId, player);
            Logger.Debug("Ambient wizard {Name} walks over to duel {Sigil}.", Logger.Args(helper.Name, notice.SigilId));
            return;
        }

        Ask(helper, notice, player, now);
    }

    /// <summary>The wizard faces the duel and asks.</summary>
    private void Ask(AmbientWizard helper, AmbientDuelNotice notice, ulong player, DateTime now) {
        if (!helper.Limiter.TryTake(now)) {
            _offeredDuels.Remove(notice.SigilId); // another wizard (or this one, later) may ask
            return;
        }

        if (helper.Moving) {
            Halt(helper); // it stops to ask
        }

        Face(helper, notice.Location);
        Logger.Debug("Ambient wizard {Name} offers help in duel {Sigil}.", Logger.Args(helper.Name, notice.SigilId));
        helper.Offers.Offered(player, notice.SigilId, now);
        helper.Activity = AmbientActivity.Idle;
        helper.Until = now + HelpOffers.AnswerWindow; // it watches the duel while it waits for the answer
        var facts = AmbientKnowledge.Facts(player);
        var line = AmbientChatBrain.HelpOffer(ChatFor(helper, player, facts), helper.Turn++);
        if (helper.FriendOf(player) is null || !AmbientChat.Whisper(helper, player, line)) {
            AmbientChat.Say(helper, line);
        }
    }

    /// <summary>
    /// The walk was to ask about a duel (or to a dungeon sigil): do that. False for an ordinary arrival.
    /// </summary>
    private bool ArrivedWithPurpose(AmbientWizard wizard, DateTime now) {
        if (ArrivedAtSigil(wizard, now)) {
            return true;
        }

        if (!_askAbout.Remove(wizard, out var ask)) {
            return false;
        }

        wizard.Activity = AmbientActivity.Idle;
        wizard.Until = now.AddSeconds(3 + _rng.Next(5));
        if (_duels.TryGetValue(ask.Sigil, out var notice) && notice.FreePlayerSlots > 0
            && !HelpManners.WinningEasily(notice.Odds ?? DuelOdds.Unknown) && !_helpMemory.IsQuiet(ask.Player, now)) {
            Ask(wizard, notice, ask.Player, now);
        }
        else {
            Face(wizard, notice?.Location ?? wizard.Position); // over already, or won: it just watches a moment
        }

        return true;
    }

    /// <summary>Turns the wizard toward <paramref name="at"/> where it stands (the zone's clients are told).</summary>
    private void Face(AmbientWizard wizard, Vector3 at) {
        if (Distance(at, wizard.Position) < 1 || wizard.Moving) {
            return;
        }

        wizard.Yaw = MathF.Atan2(at.Y - wizard.Position.Y, at.X - wizard.Position.X);
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        Send([Move(wizard)]);
    }

    // ---- hearing --------------------------------------------------------------------------------

    /// <summary>True when the speaker (a game object id) is close enough for the wizard to hear open chat.</summary>
    private bool CanHear(AmbientWizard wizard, ulong sourceGid)
        => !Wizard.TryGetCharacterId(sourceGid, out var speaker) || !ActiveWizardDirectory.TryGetByCharId(speaker, out var who)
           || !string.Equals(who.Zone, _zone, StringComparison.OrdinalIgnoreCase)
           || Distance(who.Location, wizard.Position) <= HelpManners.HearingDistance;

    // ---- after a duel ----------------------------------------------------------------------------

    /// <summary>
    /// The duel is over for the wizard: the zone's clients see it leave the duel, it lingers a few seconds where it
    /// fought (a glance around), and its next choice is a walk.
    /// </summary>
    private void LingerAfterDuel(AmbientWizard wizard) {
        var now = DateTime.UtcNow;
        _fightSince.Remove(wizard);
        _afterDuel.Add(wizard);
        wizard.Until = now + StreetManners.AfterDuelPause(wizard.Identity.Seed + wizard.Turn);
        wizard.Wizard.Location = wizard.Position;
        Send([
            new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = wizard.Wizard.GameObjectID, State = (uint) NPCStates.Idle,
            },
            Move(wizard),
            new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 },
        ]);
        var glance = TimeSpan.FromMilliseconds(1200 + _rng.Next(1500));
        Timers.StartSingleTimer($"glance-{wizard.CharId}", new Later(wizard, Glance), glance);
    }

    /// <summary>A small look around where it stands (only while it still stands there).</summary>
    private void Glance(AmbientWizard wizard) {
        if (!wizard.Present || wizard.Moving || wizard.Activity != AmbientActivity.Idle) {
            return;
        }

        var turn = StreetManners.IdleTurn(_rng.NextDouble());
        if (turn == 0) {
            return;
        }

        wizard.Yaw += turn;
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        Send([Move(wizard)]);
    }

    /// <summary>Right after a duel the wizard walks on: a spot, else any open ground a little way off. False: it stays.</summary>
    private bool MoveOnAfterDuel(AmbientWizard wizard, DateTime now) {
        if (!_afterDuel.Remove(wizard)) {
            return false;
        }

        if (WalkToSpot(wizard, 0.5 + _rng.NextDouble() * 0.3)) {
            return true;
        }

        return WalkSomewhereNear(wizard);
    }

    /// <summary>A walk to open ground 300 to 900 units off in some direction; false when none is found.</summary>
    private bool WalkSomewhereNear(AmbientWizard wizard) {
        if (_nav is null) {
            return false;
        }

        for (var attempt = 0; attempt < 8; attempt++) {
            var angle = _rng.NextDouble() * Math.PI * 2;
            var reach = 300 + _rng.NextDouble() * 600;
            var at = new Vector3(wizard.Position.X + (float) (Math.Cos(angle) * reach),
                wizard.Position.Y + (float) (Math.Sin(angle) * reach), wizard.Position.Z);
            if (_nav.Snap(Num(at), 200f) is { } open && WalkTo(wizard, new Vector3(open.X, open.Y, open.Z), AmbientActivity.Walking)) {
                return true;
            }
        }

        // Where it stands is no route's start (a duel slot off the walkable ground): step to the nearest open ground.
        if (_nav.Snap(Num(wizard.Position), 600f) is { } ground && Distance(new Vector3(ground.X, ground.Y, ground.Z), wizard.Position) > 1) {
            wizard.Route.Clear();
            wizard.Target = new Vector3(ground.X, ground.Y, ground.Z);
            wizard.Activity = AmbientActivity.Walking;
            wizard.Moving = true;
            return true;
        }

        return false;
    }

    /// <summary>The duel told the zone it is over: a wizard still seated in it after a moment is let go.</summary>
    private void DuelClosed(ulong sigil) {
        _helpMemory.Forget(sigil);
        foreach (var wizard in _wizards.Where(w => w.DuelSigil == sigil && w.Activity is AmbientActivity.Fighting)) {
            Timers.StartSingleTimer($"closed-{wizard.CharId}", new Later(wizard, w => {
                if (w.Activity == AmbientActivity.Fighting && w.DuelSigil == sigil) {
                    Logger.Debug("Ambient wizard {Name}: duel {Sigil} closed without a result; it moves on.", Logger.Args(w.Name, sigil));
                    DuelOver(w, won: false);
                }
            }), TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Any wizard in a duel for longer than <see cref="StreetManners.LongestDuel"/> is let go.</summary>
    private void CheckLongDuels(DateTime now) {
        foreach (var wizard in _wizards) {
            if (wizard.Activity != AmbientActivity.Fighting) {
                _fightSince.Remove(wizard);
                continue;
            }

            if (!_fightSince.TryGetValue(wizard, out var since)) {
                _fightSince[wizard] = now;
            }
            else if (now - since > StreetManners.LongestDuel) {
                Logger.Warning("Ambient wizard {Name}: duel {Sigil} ran {Minutes} minutes; it moves on.",
                    Logger.Args(wizard.Name, wizard.DuelSigil, (int) StreetManners.LongestDuel.TotalMinutes));
                DuelOver(wizard, won: false);
            }
        }
    }

    // ---- where to stand ------------------------------------------------------------------------

    /// <summary>
    /// A walk to one of the zone's places (an NPC with <paramref name="roll"/> under 0.65 when one is near), standing
    /// beside other shoppers, off doorways and out of other wizards' spots. False when no place nearby works.
    /// </summary>
    private bool WalkToSpot(AmbientWizard wizard, double roll) {
        var near = _spots.Where(s => Distance(s.At, wizard.Position) < Neighbourhood).ToList();
        var npcs = near.Where(s => s.Npc).ToList();
        var pool = npcs.Count > 0 && roll < 0.65 ? npcs : near;
        if (pool.Count == 0) {
            return false;
        }

        var doorways = Doorways();
        var taken = _wizards.Where(w => w != wizard && w.Present)
            .Select(w => Flat(w.Route.Count > 0 ? w.Route.Last() : w.Target ?? w.Position)).ToList();
        for (var attempt = 0; attempt < 5; attempt++) {
            var spot = pool[_rng.Next(pool.Count)];
            var at = Flat(spot.At);
            if (spot.Npc) {
                var facing = spot.FaceYaw - MathF.PI; // the NPC's own heading
                var shoppers = taken.Count(t => System.Numerics.Vector2.Distance(t, at) < 2 * StreetManners.PersonalSpace);
                at = StreetManners.BesideNpc(at, facing, shoppers);
            }
            else if (StreetManners.InDoorway(at, doorways)) {
                at = StreetManners.StandOff(at, _rng.NextDouble() * Math.PI * 2, StreetManners.StandOffDistance + _rng.Next(120));
            }

            if (StreetManners.Crowded(at, taken) || (!spot.Npc && StreetManners.InDoorway(at, doorways))) {
                continue;
            }

            if (WalkTo(wizard, new Vector3(at.X, at.Y, spot.At.Z), spot.Npc ? AmbientActivity.Shopping : AmbientActivity.Walking)) {
                wizard.ArriveYaw = spot.Npc ? spot.FaceYaw : null; // turn to the NPC on arrival
                return true;
            }
        }

        return false;
    }

    /// <summary>Stay a while, with a small turn or two at uneven times.</summary>
    private void StandAWhile(AmbientWizard wizard, DateTime now) {
        wizard.Activity = AmbientActivity.Idle;
        var stay = 5 + _rng.Next(20);
        wizard.Until = now.AddSeconds(stay);
        Timers.StartSingleTimer($"fidget-{wizard.CharId}", new Later(wizard, Glance),
            TimeSpan.FromSeconds(1.5 + _rng.NextDouble() * (stay - 2)));
    }

    /// <summary>The zone's doorways: its named arrival spots and its start.</summary>
    private List<System.Numerics.Vector2> Doorways() {
        if (_doorways is not null) {
            return _doorways;
        }

        var doorways = new List<System.Numerics.Vector2> { Flat(_start) };
        if (_zoneActor is not null && ZoneDataDirectory.TryGet(_zoneActor, out var data)) {
            doorways.AddRange((data.m_locationList ?? []).Where(l => l is not null).Select(l => Flat(l.m_location)));
            _doorways = doorways;
        }

        return doorways;
    }

    private static System.Numerics.Vector2 Flat(Vector3 v) => new(v.X, v.Y);

}
