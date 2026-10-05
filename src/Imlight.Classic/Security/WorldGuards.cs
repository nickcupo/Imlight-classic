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
 * WORLD GUARDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the small server-side checks on what a client asks for in the world (security audit 2026-10-04). Each
 * is pure so the services that call them stay thin and the rules are unit-tested:
 *   MinigameRun          one reward per minigame run, a minimum run time, a per-wizard cooldown, a score cap.
 *   InteractionRange     an NPC interaction is only honoured near the NPC (generous, for lag).
 *   CombatTargets        a raw combat target names an occupied circle, or the caster.
 *   EggHatch             an egg hatches only once its timer is done.
 *   VoluntaryTeleport    no Go Home / world door / hop / recall / friend port while in a duel; Mark not inside an
 *                        instance; Recall needs a mark.
 *   CooldownTracker      per-key cooldowns (cantrips).
 *   BoundedOffers        a capped, de-duplicated cache (quest offers).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Security;

public enum MinigameRewardDecision {
    Reward,
    NotStarted,
    AlreadyRewarded,
    TooQuick,
    Cooldown,
}

/// <summary>One minigame run (connect to result). The cooldown store is shared by all runs.</summary>
public sealed class MinigameRun {

    /// <summary>No 2009 minigame round is finished in under this.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(20);

    /// <summary>Time between two paid results of one wizard (any game).</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>Scores above this many times the game's top threshold are cut there (the leaderboard).</summary>
    public const int ScoreCapFactor = 10;

    public const int AbsoluteScoreCap = 1_000_000;

    private DateTimeOffset? _startedAt;
    private bool _rewarded;

    public bool Started => _startedAt is not null;

    /// <summary>The client's connect message: a new round starts (play again included).</summary>
    public void Start(DateTimeOffset now) {
        _startedAt = now;
        _rewarded = false;
    }

    /// <summary>Decides a result. When it is <see cref="MinigameRewardDecision.Reward"/> the run and cooldown are spent.</summary>
    public MinigameRewardDecision Decide(DateTimeOffset now, ulong charId, IDictionary<ulong, DateTimeOffset> lastPaid) {
        if (_startedAt is not { } started) return MinigameRewardDecision.NotStarted;
        if (_rewarded) return MinigameRewardDecision.AlreadyRewarded;
        if (now - started < MinimumDuration) return MinigameRewardDecision.TooQuick;
        lock (lastPaid) {
            if (lastPaid.TryGetValue(charId, out var last) && now - last < Cooldown) return MinigameRewardDecision.Cooldown;
            lastPaid[charId] = now;
        }

        _rewarded = true;

        return MinigameRewardDecision.Reward;
    }

    /// <summary>The score kept for rewards and the leaderboard: 0 or more, at most the cap.</summary>
    public static int ClampScore(int score, IReadOnlyList<int>? thresholds) {
        var top = 0;
        if (thresholds is not null) {
            foreach (var threshold in thresholds) top = Math.Max(top, threshold);
        }

        var cap = top > 0 ? (int) Math.Min(AbsoluteScoreCap, (long) top * ScoreCapFactor) : AbsoluteScoreCap;

        return Math.Clamp(score, 0, cap);
    }

}

public static class InteractionRange {

    /// <summary>Lag allowance over the NPC's own radius: radius * 1.5 + 150 (300 -> 600).</summary>
    public static double Allowed(double radius) => (Math.Max(0, radius) * 1.5) + 150;

    public static bool Within(double distanceSquared, double radius) {
        var allowed = Allowed(radius);

        return double.IsFinite(distanceSquared) && distanceSquared <= allowed * allowed;
    }

}

public static class CombatTargets {

    /// <summary>The circle a raw target names, or -1 for the caster: out of range or an empty circle.</summary>
    public static int Resolve(uint rawTarget, int circleCount, Func<int, bool> occupied) {
        if (rawTarget >= (uint) Math.Max(0, circleCount)) return -1;
        var slot = (int) rawTarget;

        return occupied(slot) ? slot : -1;
    }

}

public static class EggHatch {

    /// <summary>An egg hatches only once its finish time (unix seconds) has come; "hatch now" has no free skip.</summary>
    public static bool Ready(long finishedUnixSeconds, long nowUnixSeconds) => nowUnixSeconds >= finishedUnixSeconds;

}

public enum TeleportRefusal {
    None,
    InDuel,
    NoMark,
    MarkInInstance,
}

public static class VoluntaryTeleport {

    /// <summary>Go Home, Go to Dorm, a world door, a zone hop, Recall, a friend port or a teleport cantrip.</summary>
    public static TeleportRefusal Check(bool inDuel) => inDuel ? TeleportRefusal.InDuel : TeleportRefusal.None;

    public static TeleportRefusal CheckRecall(bool inDuel, string? markedZone)
        => inDuel ? TeleportRefusal.InDuel : string.IsNullOrEmpty(markedZone) ? TeleportRefusal.NoMark : TeleportRefusal.None;

    /// <summary>A mark inside a private instance (a dungeon run, a dorm, a minigame) would later recall into a copy
    /// the wizard doesn't own; such zones are refused.</summary>
    public static TeleportRefusal CheckMark(bool inDuel, bool inInstance)
        => inDuel ? TeleportRefusal.InDuel : inInstance ? TeleportRefusal.MarkInInstance : TeleportRefusal.None;

    public static string Message(TeleportRefusal refusal) => refusal switch {
        TeleportRefusal.InDuel => "You can't do that during a duel.",
        TeleportRefusal.NoMark => "You haven't marked a location.",
        TeleportRefusal.MarkInInstance => "You can't mark your location here.",
        _ => "",
    };

}

/// <summary>Per-key cooldowns. Not thread-safe: one session actor owns it.</summary>
public sealed class CooldownTracker {

    private readonly Dictionary<uint, DateTimeOffset> _readyAt = [];

    public bool IsReady(uint key, DateTimeOffset now) => !_readyAt.TryGetValue(key, out var ready) || now >= ready;

    public void Start(uint key, DateTimeOffset now, TimeSpan cooldown) {
        if (cooldown > TimeSpan.Zero) _readyAt[key] = now + cooldown;
    }

}

/// <summary>A small cache of offers keyed by name: a new offer replaces the old one, the oldest goes past the cap.</summary>
public sealed class BoundedOffers<T> where T : class {

    public const int DefaultCapacity = 64;

    private readonly int _capacity;
    private readonly List<(string Key, T Value)> _items = [];

    public BoundedOffers(int capacity = DefaultCapacity) {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _items.Count;

    public void Add(string key, T value) {
        _items.RemoveAll(item => item.Key == key);
        _items.Add((key, value));
        if (_items.Count > _capacity) _items.RemoveAt(0);
    }

    public T? Find(string key) => _items.Find(item => item.Key == key).Value;

    public void Remove(string key) => _items.RemoveAll(item => item.Key == key);

}
