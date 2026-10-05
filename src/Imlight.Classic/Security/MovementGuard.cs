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
 * MOVEMENT GUARD
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: is a client's MSG_CLIENTMOVE plausible? The client is the movement authority, so the server only refuses
 * a move that covers more ground than any wizard could, and answers it with a server teleport back to the last
 * accepted spot (a snap back, never a kick).
 *
 * The check is a distance budget: it fills at MaxSpeed units per second up to MaxSpeed * BurstSeconds + Slack, and
 * each accepted move spends its distance from the last accepted spot. So a lag spike that delivers seconds of
 * buffered moves at once passes (the budget filled while nothing arrived), while a stream of small hops cannot add up
 * to more than MaxSpeed. Default thresholds (tune with [Classic] MovementMaxSpeed):
 *   MaxSpeed     1500 u/s  (2.5x the 600 u/s run speed: speed gear, mounts, falls)
 *   BurstSeconds 3 s       (largest single catch-up after a stall: 1500*3+400 = 4900 units)
 *   Slack        400 units (quantisation, client prediction, circle placement)
 *   After a server teleport: 2 s in which moves more than Slack from the destination are dropped silently (moves
 *   already in flight from the old spot), not snapped.
 *   After a snap back: 1 s in which further bad moves are dropped silently, so one cheat costs one teleport.
 *   After a session start or zone change: 5 s in which every move is accepted (the spawn point is the client's).
 *
 * USAGE EXAMPLE:
 * var verdict = guard.Check(x, y, z, now, MovementGuard.DefaultMaxSpeed);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;

namespace Imlight.Classic.Security;

public enum MoveVerdict {
    /// <summary>Plausible: apply and broadcast it.</summary>
    Accept,
    /// <summary>Implausible: drop it and teleport the client back to <see cref="MovementGuard.Anchor"/>.</summary>
    SnapBack,
    /// <summary>Implausible but expected (in flight before a server teleport or snap): drop it quietly.</summary>
    Drop,
}

/// <summary>Per-session movement plausibility. Not thread-safe: the session actor owns it.</summary>
public sealed class MovementGuard {

    public const double DefaultMaxSpeed = 1500;
    public const double BurstSeconds = 3;
    public const double Slack = 400;
    public static readonly TimeSpan EntryGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TeleportSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SnapQuiet = TimeSpan.FromSeconds(1);

    private bool _hasAnchor;
    private double _x, _y, _z;
    private double _budget;
    private DateTimeOffset _budgetAt;
    private DateTimeOffset _graceUntil;
    private DateTimeOffset _settleUntil;
    private DateTimeOffset _quietUntil;

    /// <summary>The last accepted (or server-set) position.</summary>
    public (double X, double Y, double Z) Anchor => (_x, _y, _z);

    public bool HasAnchor => _hasAnchor;

    /// <summary>A new session, wizard or zone: trust the client's position for <see cref="EntryGrace"/>.</summary>
    public void Reset(DateTimeOffset now) {
        _hasAnchor = false;
        _graceUntil = now + EntryGrace;
        _settleUntil = default;
        _quietUntil = default;
    }

    /// <summary>The server placed the wizard at (x, y, z) (MSG_SERVERTELEPORT, a recall, a snap back).</summary>
    public void ServerTeleport(double x, double y, double z, DateTimeOffset now, double maxSpeed = DefaultMaxSpeed) {
        (_x, _y, _z) = (x, y, z);
        _hasAnchor = true;
        _budget = Capacity(maxSpeed);
        _budgetAt = now;
        _settleUntil = now + TeleportSettle;
    }

    public MoveVerdict Check(double x, double y, double z, DateTimeOffset now, double maxSpeed = DefaultMaxSpeed) {
        if (!double.IsFinite(maxSpeed) || maxSpeed <= 0) {
            maxSpeed = DefaultMaxSpeed;
        }

        if (!_hasAnchor || now < _graceUntil) {
            (_x, _y, _z) = (x, y, z);
            _hasAnchor = true;
            _budget = Capacity(maxSpeed);
            _budgetAt = now;

            return MoveVerdict.Accept;
        }

        if (now > _budgetAt) {
            _budget = Math.Min(Capacity(maxSpeed), _budget + (now - _budgetAt).TotalSeconds * maxSpeed);
            _budgetAt = now;
        }

        var dx = x - _x;
        var dy = y - _y;
        var dz = z - _z;
        var distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

        // Moves sent before the client saw a server teleport are near the old spot; drop them, don't snap.
        if (now < _settleUntil && distance > Slack) {
            return MoveVerdict.Drop;
        }

        if (distance <= _budget) {
            (_x, _y, _z) = (x, y, z);
            _budget = Math.Max(0, _budget - distance);

            return MoveVerdict.Accept;
        }

        if (now < _quietUntil) {
            return MoveVerdict.Drop;
        }

        _quietUntil = now + SnapQuiet;

        return MoveVerdict.SnapBack;
    }

    private static double Capacity(double maxSpeed) => (maxSpeed * BurstSeconds) + Slack;

}
