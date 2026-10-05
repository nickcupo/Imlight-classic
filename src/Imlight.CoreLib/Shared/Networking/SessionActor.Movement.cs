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
 * SESSION MOVEMENT GUARD
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: MSG_CLIENTMOVE goes to WizardService (the saved position) and MoveService (the broadcast and NPC ranges)
 * as two actors, so the plausibility check runs here, in packet order, before either sees the move
 * (Imlight.Classic.Security.MovementGuard has the thresholds). An implausible move reaches neither: the client gets a
 * MSG_SERVERTELEPORT back to the last accepted spot. Every MSG_SERVERTELEPORT this session sends for its own wizard
 * (a recall, a teleport stone, a tutorial step, a snap back) re-anchors the guard, whoever sent it.
 *
 * NOTE:
 * [Classic] MovementGuard (on) and MovementMaxSpeed (1500 u/s). Off outside a classic profile and in minigame zones.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic;
using Imlight.Classic.Security;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Networking;

public sealed partial class SessionActor {

    private readonly MovementGuard _movementGuard = new();
    private Wizard _movementWizard;
    private string _movementZone;
    private bool _movementUnguardedLogged;
    // When MSG_LOGINCOMPLETE went out: the entry grace runs from the zone entry, not from the first move (a client that
    // idles first must not get a free teleport).
    private DateTimeOffset? _movementEnteredAt;

    private static bool MovementGuardOn() {
        try {
            return ClassicRuntime.IsActive && ClassicSettings.MovementGuard;
        }
        catch {
            return false;
        }
    }

    private static double MovementMaxSpeed() {
        try {
            return ClassicSettings.MovementMaxSpeed;
        }
        catch {
            return MovementGuard.DefaultMaxSpeed;
        }
    }

    private static bool InMinigameZone(string zone) {
        try {
            return Game.Minigames.MinigameConfig.IsMinigameZone(zone ?? "");
        }
        catch {
            return false;
        }
    }

    /// <summary>True when the move may go on to the services.</summary>
    private bool AdmitClientMove(GAME_5_PROTOCOL.MSG_CLIENTMOVE move) {
        if (!MovementGuardOn() || !ActiveWizardDirectory.TryGet(Self, out var wizard, out var gameObject)
                || wizard is null || gameObject is null) {
            if (!_movementUnguardedLogged) {
                _movementUnguardedLogged = true;
                Logger.Debug("MovementGuard: session {0} moves unchecked (guard {1}).", Logger.Args(SessionID, MovementGuardOn()));
            }

            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (!ReferenceEquals(wizard, _movementWizard) || !string.Equals(wizard.Zone, _movementZone, StringComparison.Ordinal)) {
            _movementWizard = wizard;
            _movementZone = wizard.Zone;
            _movementGuard.Reset(_movementEnteredAt is { } entered && entered <= now ? entered : now);
        }

        if (InMinigameZone(wizard.Zone)) {
            return true;
        }

        var x = unchecked((short) move.LocationX) * 4.0;
        var y = unchecked((short) move.LocationY) * 4.0;
        var z = unchecked((short) move.LocationZ) * 4.0;
        var verdict = _movementGuard.Check(x, y, z, now, MovementMaxSpeed());
        if (verdict != MoveVerdict.Accept) {
            Logger.Debug("MovementGuard: session {0} move to ({1:0}, {2:0}) {3}.", Logger.Args(SessionID, x, y, verdict));
        }
        if (verdict == MoveVerdict.Accept) {
            return true;
        }

        var (ax, ay, az) = _movementGuard.Anchor;
        if (verdict == MoveVerdict.SnapBack) {
            Logger.Warning("MovementGuard: wizard {0} in {1} moved to ({2:0}, {3:0}, {4:0}) from ({5:0}, {6:0}, {7:0}); snapped back.",
                Logger.Args(wizard.CharId, wizard.Zone, x, y, z, ax, ay, az));
            SendToSocket(new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
                LocationX = unchecked((ushort) (short) Math.Round(ax / 4)),
                LocationY = unchecked((ushort) (short) Math.Round(ay / 4)),
                LocationZ = unchecked((ushort) (short) Math.Round(az / 4)),
                Direction = move.Direction,
                MobileID = gameObject.m_nMobileID,
            });
        }

        return false;
    }

    /// <summary>A server teleport of this session's own wizard re-anchors the guard.</summary>
    private void ObserveOutgoing(IMessage message) {
        if (message is GAME_5_PROTOCOL.MSG_LOGINCOMPLETE) {
            _movementEnteredAt = DateTimeOffset.UtcNow;
            _movementWizard = null; // the next move resets the guard from this entry
            return;
        }

        if (message is not GAME_5_PROTOCOL.MSG_SERVERTELEPORT teleport || !MovementGuardOn()
                || !ActiveWizardDirectory.TryGet(Self, out _, out var gameObject) || gameObject is null
                || teleport.MobileID != gameObject.m_nMobileID) {
            return;
        }

        _movementGuard.ServerTeleport(
            unchecked((short) teleport.LocationX) * 4.0,
            unchecked((short) teleport.LocationY) * 4.0,
            unchecked((short) teleport.LocationZ) * 4.0,
            DateTimeOffset.UtcNow, MovementMaxSpeed());
    }

}
