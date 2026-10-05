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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the after-duel protection (the translucent wizard) as KingsIsle's
 * own effect data describes it. GameEffectData/WizardEffects.xml (the 2014
 * Wizard_1_240 client and r806919 carry the same two templates):
 *   PostCombatEffect   30 s, public, AddTranslucentEffect, on remove "PostCombatRemoved"
 *   PostCombatEffect2   6 s, public, AddTranslucentEffect, on remove "ReAggro"
 * So a wizard who stands still after a duel stays translucent and safe for up
 * to 30 s; moving (or the 30 s running out) swaps in the 6 s effect, and only
 * when that one ends do creatures aggro again.
 *
 * USAGE EXAMPLE:
 * grace.Start(now, wizard.Location);
 * if (grace.Moved(newLocation, now)) SwapEffects();
 * if (grace.Due(now)) grace.Advance(now);
 *
 * NOTE:
 * The functors are server code and not in the client. "PostCombatRemoved"
 * adding the 6 s effect is read from the names: the 30 s effect's removal is
 * not itself "ReAggro", and the 6 s effect is.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Numerics;

namespace Imlight.Classic.Quests;

/// <summary>Which of the two after-duel effects is on.</summary>
public enum PostCombatPhase {
    /// <summary>No protection.</summary>
    None,
    /// <summary>PostCombatEffect: the wizard has not moved since the duel (up to 30 s).</summary>
    Still,
    /// <summary>PostCombatEffect2: the wizard moved (or 30 s passed); 6 s more, then creatures aggro again.</summary>
    Moving,
}

/// <summary>One wizard's after-duel protection (see the file header).</summary>
public sealed class PostCombatGrace {

    /// <summary>The effect while the wizard stands still.</summary>
    public const string StillEffectName = "PostCombatEffect";

    /// <summary>The effect after the wizard moves.</summary>
    public const string MovingEffectName = "PostCombatEffect2";

    /// <summary>PostCombatEffect's m_duration.</summary>
    public static readonly TimeSpan DefaultStill = TimeSpan.FromSeconds(30);

    /// <summary>PostCombatEffect2's m_duration.</summary>
    public static readonly TimeSpan DefaultMoving = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How far (world units) from the duel spot counts as moving. Client moves come in steps of 4 units; a turn on the
    /// spot sends the same location.
    /// </summary>
    public const float MoveThreshold = 16f;

    private Vector3 _anchor;

    /// <summary>A grace with the client data's durations.</summary>
    public PostCombatGrace() : this(DefaultStill, DefaultMoving) { }

    /// <summary>A grace with other durations (a zero <paramref name="moving"/> ends it at once on a move).</summary>
    public PostCombatGrace(TimeSpan still, TimeSpan moving) {
        Still = still;
        MovingGrace = moving;
    }

    /// <summary>The longest the first effect lasts.</summary>
    public TimeSpan Still { get; }

    /// <summary>How long the second effect lasts.</summary>
    public TimeSpan MovingGrace { get; }

    /// <summary>The effect that is on.</summary>
    public PostCombatPhase Phase { get; private set; }

    /// <summary>When the current effect runs out.</summary>
    public DateTime EndsUtc { get; private set; }

    /// <summary>True while creatures leave the wizard alone.</summary>
    public bool Protected => Phase != PostCombatPhase.None;

    /// <summary>The name of the client effect for <see cref="Phase"/>, or null.</summary>
    public string? EffectName => Phase switch {
        PostCombatPhase.Still => StillEffectName,
        PostCombatPhase.Moving => MovingEffectName,
        _ => null,
    };

    /// <summary>A duel just ended with the wizard at <paramref name="location"/>.</summary>
    public void Start(DateTime nowUtc, Vector3 location) {
        _anchor = location;
        Phase = PostCombatPhase.Still;
        EndsUtc = nowUtc + Still;
    }

    /// <summary>
    /// The wizard is at <paramref name="location"/>. True when this is its first move off the duel spot, which swaps
    /// the still effect for the moving one.
    /// </summary>
    public bool Moved(Vector3 location, DateTime nowUtc) {
        if (Phase != PostCombatPhase.Still || Vector3.Distance(location, _anchor) <= MoveThreshold) {
            return false;
        }

        Advance(nowUtc);
        return true;
    }

    /// <summary>True when the current effect has run out.</summary>
    public bool Due(DateTime nowUtc) => Phase != PostCombatPhase.None && nowUtc >= EndsUtc;

    /// <summary>The current effect ends: still becomes moving, moving becomes none. Returns the new phase.</summary>
    public PostCombatPhase Advance(DateTime nowUtc) {
        switch (Phase) {
            case PostCombatPhase.Still when MovingGrace > TimeSpan.Zero:
                Phase = PostCombatPhase.Moving;
                EndsUtc = nowUtc + MovingGrace;
                break;
            case PostCombatPhase.Still:
            case PostCombatPhase.Moving:
                Clear();
                break;
        }

        return Phase;
    }

    /// <summary>Ends the protection now (a new duel, a logout).</summary>
    public void Clear() {
        Phase = PostCombatPhase.None;
        EndsUtc = default;
    }

}
