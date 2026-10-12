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
 * AMBIENT ZONE: DUNGEONS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): the street side of grouping for a dungeon. A real
 * player steps on a dungeon sigil (AmbientSigilNotice from the sigil):
 *   1. Who may come (DungeonManners): ambient wizards here that can see
 *      the sigil (1200 units, a clear line), of a fitting level, not
 *      busy, that can get there before the countdown ends; each comes
 *      with the configured chance, up to the open helper slots (what the
 *      instance holds after its real players; none for a one-wizard tower).
 *   2. A second or two later it walks over, steps on (SigilGroup.Join as
 *      ambient: a real player arriving later takes its place), slides to
 *      its face like a player does and says it is coming ("mind if i
 *      come?"). A "no" from the player steps every helper off and leaves
 *      that player alone for half an hour.
 *   3. When the countdown is over and the player is inside, the wizard
 *      leaves the street (as a player teleporting) and its dungeon party
 *      (AmbientDungeonParty, one per run) takes it in. Bumped, or the
 *      player never went in: it steps off and goes back to its day.
 *   4. Back from the dungeon, it comes back into the street at the sigil.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A helper is back from its dungeon run: it comes into its home zone at <paramref name="At"/> after <paramref name="After"/>.</summary>
internal sealed record AmbientHelperBack(AmbientWizard Wizard, Vector3 At, TimeSpan After);

/// <summary>A dungeon party has no helper left.</summary>
internal sealed record AmbientPartyOver(ulong RunId);

internal sealed partial class AmbientZone {

    private sealed record HardLimitKnown(AmbientSigilNotice Notice, int HardLimit);
    private sealed record SigilSeat(AmbientSigilNotice Notice, int HardLimit);

    private readonly Dictionary<AmbientWizard, SigilSeat> _toSigil = [];
    private readonly Dictionary<AmbientWizard, SigilSeat> _onSigil = [];
    private readonly Dictionary<ulong, IActorRef> _parties = [];
    private readonly Dictionary<AmbientWizard, Vector3> _returnAt = [];

    private void ReceiveDungeons() {
        Receive<AmbientSigilNotice>(OnSigilNotice);
        Receive<HardLimitKnown>(Recruit);
        Receive<AmbientHelperBack>(OnHelperBack);
        Receive<AmbientPartyOver>(over => _parties.Remove(over.RunId));
    }

    private void OnSigilNotice(AmbientSigilNotice notice) {
        if (!AmbientDungeons.Settings.Enabled || AmbientDungeons.WantsNoHelpers(notice.PlayerCharId, DateTime.UtcNow)) {
            return;
        }

        var self = Self;
        AmbientDungeons.HardLimitOf(notice.DestinationZone).ContinueWith(task
            => new HardLimitKnown(notice, task.IsCompletedSuccessfully ? task.Result : 1)).PipeTo(self);
    }

    /// <summary>Picks who walks over to the sigil (see the file header, 1).</summary>
    private void Recruit(HardLimitKnown known) {
        var notice = known.Notice;
        var group = notice.Group;
        var now = DateTime.UtcNow;
        var settings = AmbientDungeons.Settings;
        var coming = _toSigil.Values.Count(s => s.Notice.Group == group);
        var open = DungeonManners.OpenHelperSlots(group.RealCount, group.AmbientCount + coming, known.HardLimit, settings.MaxHelpers);
        if (open <= 0 || !group.IsOpen(now.AddSeconds(3))) {
            Logger.Debug("Ambient wizards in {Zone}: no helper for sigil run {Run} ({Open} open, hard limit {Limit}).",
                Logger.Args(_zone, group.RunId, open, known.HardLimit));
            return;
        }

        var secondsLeft = (group.EndsUtc - now).TotalSeconds;
        var candidates = _wizards
            .Where(w => w.Present && w.Activity is AmbientActivity.Idle or AmbientActivity.Walking or AmbientActivity.Shopping
                                                   or AmbientActivity.Following
                        && !_askAbout.ContainsKey(w) && !_toSigil.ContainsKey(w) && !_onSigil.ContainsKey(w)
                        && DungeonManners.Fits(w.Wizard.MagicSchoolBehavior.Level, notice.PlayerLevel))
            .Select(w => (Wizard: w, Distance: Distance(w.Position, notice.Pad)))
            .Where(c => c.Distance <= DungeonManners.RecruitDistance && CanSee(c.Wizard.Position, notice.Pad)
                        && 2.5 + c.Distance / AmbientNav.RunSpeed < secondsLeft - 1)
            .OrderBy(c => c.Distance)
            .ToList();
        if (candidates.Count == 0) {
            Logger.Debug("Ambient wizards in {Zone}: none can see and reach the sigil of run {Run}: {Who}", Logger.Args(_zone, group.RunId,
                string.Join("; ", _wizards.Select(w => $"{w.Name} L{w.Wizard.MagicSchoolBehavior.Level} {w.Activity} {(int) Distance(w.Position, notice.Pad)}"
                    + $"{(CanSee(w.Position, notice.Pad) ? "" : " unseen")}{(DungeonManners.Fits(w.Wizard.MagicSchoolBehavior.Level, notice.PlayerLevel) ? "" : " unfit")}"))
                + $" (player level {notice.PlayerLevel}, {secondsLeft:0.0} s left)"));
        }
        foreach (var (wizard, _) in candidates) {
            if (open <= 0) {
                break;
            }

            if (_rng.NextDouble() >= settings.JoinChance) {
                continue;
            }

            open--;
            var seat = new SigilSeat(notice, known.HardLimit);
            _toSigil[wizard] = seat;
            Logger.Information("Ambient wizard {Name} (level {Level}) comes to the sigil for {Dungeon} (run {Run}, player level {Player}).",
                Logger.Args(wizard.Name, wizard.Wizard.MagicSchoolBehavior.Level, notice.DestinationZone, group.RunId, notice.PlayerLevel));
            // A moment to notice and decide, as a person would.
            Timers.StartSingleTimer($"sigil-{wizard.CharId}", new Later(wizard, w => GoToSigil(w, seat)),
                TimeSpan.FromMilliseconds(900 + _rng.Next(1300)));
        }
    }

    private void GoToSigil(AmbientWizard wizard, SigilSeat seat) {
        if (!_toSigil.TryGetValue(wizard, out var mine) || mine != seat || !wizard.Present
            || wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Away or AmbientActivity.Helping) {
            _toSigil.Remove(wizard);
            return;
        }

        if (wizard.DuelSigil == ulong.MaxValue) {
            wizard.DuelSigil = 0; // it was hunting; the dungeon comes first
            AmbientWizards.RevokeJoin(wizard.Endpoint);
        }

        // To the sigil's rim on the wizard's side; the step on is a slide to its face, as for a player.
        var pad = seat.Notice.Pad;
        var away = Distance(wizard.Position, pad);
        var rim = away < 1 ? pad
            : new Vector3(pad.X + (wizard.Position.X - pad.X) / away * 230, pad.Y + (wizard.Position.Y - pad.Y) / away * 230, pad.Z);
        if (away <= 260 || !WalkTo(wizard, rim, AmbientActivity.Walking)) {
            Halt(wizard);
            ArrivedAtSigil(wizard, DateTime.UtcNow);
        }
    }

    /// <summary>The wizard reached the sigil it was coming to: it steps on (see the file header, 2). False: not coming to one.</summary>
    private bool ArrivedAtSigil(AmbientWizard wizard, DateTime now) {
        if (!_toSigil.Remove(wizard, out var seat)) {
            return false;
        }

        var notice = seat.Notice;
        var group = notice.Group;
        wizard.Activity = AmbientActivity.Idle;
        wizard.Until = now.AddSeconds(2 + _rng.Next(3));
        if (AmbientDungeons.WantsNoHelpers(notice.PlayerCharId, now) || !group.IsMember(notice.PlayerCharId)
            || group.Join(wizard.CharId, now, ambient: true) is not { } ticket) {
            Logger.Debug("Ambient wizard {Name}: too late for sigil run {Run}.", Logger.Args(wizard.Name, group.RunId));
            return true;
        }

        var entry = new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
            SigilLoc = Util.GetCompactStringFromVector(new Vector4(notice.Pad.X, notice.Pad.Y, notice.Pad.Z, notice.PadHeading)),
            SigilType = notice.SigilType, Slot = ticket.Slot,
        };
        if (ZoneService.TryGetSigilFaceSlot(entry, out var face, out var faceYaw)) {
            // The slide onto its face, as the sigil gives a player (ZoneService.SnapPlayerToSigilFace).
            wizard.Position = face;
            wizard.Yaw = AmbientWizards.Heading(faceYaw);
            wizard.Wizard.Location = face;
            wizard.Wizard.Orientation = new Vector3(0, 0, faceYaw);
            Send([new WIZARD_12_PROTOCOL.MSG_AGGRO {
                GlobalID = wizard.Wizard.GameObjectID, LocX = face.X, LocY = face.Y, LocZ = face.Z, Yaw = faceYaw,
            }]);
        }
        else {
            Face(wizard, notice.Pad);
        }

        wizard.Activity = AmbientActivity.Helping; // busy: nothing else picks it while it waits on the sigil
        wizard.Until = group.EndsUtc.AddSeconds(30);
        _onSigil[wizard] = seat;
        Logger.Information("Ambient wizard {Name} is wizard {Slot} on the sigil (run {Run}, {Left:0.0} s left).",
            Logger.Args(wizard.Name, ticket.Slot + 1, group.RunId, ticket.SecondsLeft));
        if (wizard.Limiter.TryTake(now)) {
            var line = DungeonLines.Pick(DungeonLines.Join, wizard.Identity.Seed + wizard.Turn++);
            Timers.StartSingleTimer($"sigil-say-{wizard.CharId}", new Later(wizard, w => AmbientChat.Say(w, line)),
                TimeSpan.FromMilliseconds(500 + _rng.Next(900)));
        }

        Timers.StartSingleTimer($"sigil-go-{wizard.CharId}", new Later(wizard, SigilGo),
            group.EndsUtc - now + TimeSpan.FromSeconds(1));
        return true;
    }

    /// <summary>The countdown is over: in with the player, or off the sigil (see the file header, 3).</summary>
    private void SigilGo(AmbientWizard wizard) {
        if (!_onSigil.TryGetValue(wizard, out var seat)) {
            return;
        }

        var notice = seat.Notice;
        var group = notice.Group;
        var now = DateTime.UtcNow;
        if (!group.IsMember(wizard.CharId)) {
            Logger.Information("Ambient wizard {Name} gave its place on the sigil to a player.", Logger.Args(wizard.Name));
            StepOff(wizard, DungeonLines.Pick(DungeonLines.MakeRoom, wizard.Identity.Seed + wizard.Turn++));
            return;
        }

        var inside = group.Members.Where(m => !AmbientWizards.IsAmbientChar(m))
            .Any(m => OnlinePlayerCollection.GetOnlinePlayer(m) is { } p && p.InstanceOwnerId == group.RunId);
        if (inside) {
            Handoff(wizard, seat);
            return;
        }

        if (now < group.EndsUtc.AddSeconds(15)) {
            Timers.StartSingleTimer($"sigil-go-{wizard.CharId}", new Later(wizard, SigilGo), TimeSpan.FromSeconds(1));
            return;
        }

        Logger.Information("Ambient wizard {Name}: nobody went into run {Run}; it steps off.", Logger.Args(wizard.Name, group.RunId));
        StepOff(wizard, null);
    }

    /// <summary>The wizard steps off the sigil and goes back to its day, saying <paramref name="line"/> if any.</summary>
    private void StepOff(AmbientWizard wizard, string line) {
        if (!_onSigil.Remove(wizard, out var seat)) {
            return;
        }

        seat.Notice.Group.Leave(wizard.CharId);
        Timers.Cancel($"sigil-say-{wizard.CharId}"); // no "can i come?" after it was told no
        Timers.Cancel($"sigil-go-{wizard.CharId}");
        wizard.Activity = AmbientActivity.Idle;
        wizard.Until = DateTime.UtcNow.AddSeconds(2 + _rng.Next(3));
        _afterDuel.Add(wizard); // it walks off rather than standing on the sigil
        Send([new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = wizard.Wizard.GameObjectID, State = (uint) NPCStates.Idle }]);
        if (line is not null && AmbientWizards.Settings.Chat) {
            AmbientChat.Say(wizard, line);
        }
    }

    /// <summary>
    /// A no from a player of the group to a wizard on the sigil: every helper of that group steps off, and the player
    /// is left alone for a while. True when the line was such an answer.
    /// </summary>
    private bool SigilAnswer(AmbientWizard wizard, ulong speaker, string text, DateTime now) {
        if (!_onSigil.TryGetValue(wizard, out var seat) || !seat.Notice.Group.IsMember(speaker) || !SaysNoToHelpers(text)) {
            return false;
        }

        AmbientDungeons.SaidNo(speaker, now);
        var group = seat.Notice.Group;
        foreach (var helper in _onSigil.Where(kv => kv.Value.Notice.Group == group).Select(kv => kv.Key).ToList()) {
            StepOff(helper, helper == wizard ? DungeonLines.Pick(DungeonLines.Declined, helper.Identity.Seed + helper.Turn++) : null);
        }

        foreach (var coming in _toSigil.Where(kv => kv.Value.Notice.Group == group).Select(kv => kv.Key).ToList()) {
            _toSigil.Remove(coming);
        }

        return true;
    }

    /// <summary>A no to a helper: the usual no words, or "solo", "no helpers", "leave".</summary>
    internal static bool SaysNoToHelpers(string text) {
        if (HelpOffers.Classify(text) == HelpAnswerKind.No) {
            return true;
        }

        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("solo") || lower.Contains("no help") || lower.Contains("leave") || lower.Contains("go away")
               || lower.Contains("by myself") || lower.Contains("alone");
    }

    /// <summary>The wizard leaves the street for the dungeon; the run's party takes it (see the file header, 3).</summary>
    private void Handoff(AmbientWizard wizard, SigilSeat seat) {
        _onSigil.Remove(wizard);
        var notice = seat.Notice;
        if (!_parties.TryGetValue(notice.Group.RunId, out var party)) {
            party = Context.ActorOf(AmbientDungeonParty.Props(notice, seat.HardLimit, _server), $"party-{notice.Group.RunId:x}");
            _parties[notice.Group.RunId] = party;
        }

        Logger.Information("Ambient wizard {Name} goes into {Dungeon} with run {Run}.",
            Logger.Args(wizard.Name, notice.DestinationZone, notice.Group.RunId));
        Leave(wizard);
        _wizards.Remove(wizard);
        _afterDuel.Remove(wizard);
        _askAbout.Remove(wizard);
        wizard.Activity = AmbientActivity.Dungeon;
        wizard.Driver = party;
        party.Tell(new AmbientDungeonParty.Take(wizard));
    }

    /// <summary>A helper is back from its run: it comes into the street again at the sigil (see the file header, 4).</summary>
    private void OnHelperBack(AmbientHelperBack back) {
        var wizard = back.Wizard;
        wizard.Driver = null;
        wizard.Zone = _zone; // CLASSIC (2026-10-09): it was the dungeon's; its Say and status go by its zone again
        wizard.Activity = AmbientActivity.Arriving;
        wizard.DuelSigil = 0;
        wizard.Wizard.IsInDuel = false;
        _returnAt[wizard] = back.At;
        if (!_wizards.Contains(wizard)) {
            _wizards.Add(wizard);
        }

        Timers.StartSingleTimer($"enter-{wizard.CharId}", new Enter(wizard.CharId), back.After);
    }

}
