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
 * CLASSIC PET MINIGAME RULES (CANNON, GOBBLER DROP, MAZE)
 * ========================================================================
 *
 * PURPOSE:
 * The numbers the server uses to run and score the three Pavilion games that
 * play in their own phantom zone. Each value carries its source and how sure
 * it is; classic-data/rules/pet-minigames-2010.yaml holds the same values with
 * full provenance, and PetMinigameRulesTests keeps the two identical.
 *
 * NOTE:
 * Confidence words match classic-data: "verified" (a dated source states it),
 * "derived" (fits every dated observation, not stated), "client" (the owned
 * r806919 client data or native constant, undated), "unverified" (a
 * placeholder the owner has to rule on). Unverified values are marked
 * UNVERIFIED below. Gobbler Drop reads its own numbers at run time from the
 * client's ThePhantomZoneWorld/PetGameDrop/GameSettings.xml, so the server and
 * the client agree; those values are "client" confidence.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Pets;

/// <summary>The rules of Cannon, Gobbler Drop and Maze, with their sources.</summary>
public static class PetMinigameRules {

    // ------------------------------------------------------------------ Cannon

    /// <summary>
    /// Shots in one Cannon game: a practice shot, then three that count. Client r806919 PetGameCannon.gui has
    /// PracticeRound, Round1..Round3; May 22 2010 Test Realm account (practice shot added), May 28 2010 live account
    /// (three shots). Confidence: client + verified (dated accounts).
    /// </summary>
    public const int CannonShots = 4;

    /// <summary>The scored shots (the first shot is practice).</summary>
    public const int CannonScoredShots = CannonShots - 1;

    /// <summary>
    /// Ring radii of the target, centre outwards, from the client's collision mesh
    /// GameObjects/PET_Cannon_Target_01.col.xml (rings at 100, 225, 375 and 575 units around a centre 565.78 units
    /// above the target's foot). Confidence: client.
    /// </summary>
    public static readonly float[] CannonRingRadii = [100f, 225f, 375f, 575f];

    /// <summary>The target's centre above its placed foot (the collision mesh's ring centre). Confidence: client.</summary>
    public const float CannonTargetCentreHeight = 565.78f;

    /// <summary>
    /// The value of a shot by its result index (0 = no score, 1 = Ok, 2 = Good, 3 = Perfect; the client's
    /// Art_Cannon_ShotNone/Ok/Good/Perfect icons). Derived from the June 30 2010 test of 15 hit combinations
    /// (thefriendlynecromancer.blogspot.com "How does cannon game keep score"): with black 0, blue 1, red 2 and
    /// yellow 4 every one of the 15 observed totals maps to its reported points by <see cref="CannonPoints"/>.
    /// Which ring is which colour (yellow centre, then red, blue, black: the archery order) is UNVERIFIED.
    /// </summary>
    public static readonly int[] CannonResultValues = [0, 1, 2, 4];

    /// <summary>Totals at or above which three scored shots earn 1, 2, 3 and 4 points. Confidence: derived (see above).</summary>
    public static readonly int[] CannonPointThresholds = [1, 5, 8, 10];

    /// <summary>
    /// Gravity of the shot, units per second squared, downwards. UNVERIFIED: the client's own constant was not read
    /// (the native code was not inspected further); calibrate from the logged shots and the owner's view in game.
    /// </summary>
    public const float CannonGravity = 980f;

    /// <summary>Strongest wind the server picks (the client shows the number). UNVERIFIED placeholder.</summary>
    public const int CannonMaxWind = 5;

    /// <summary>Sideways acceleration per point of wind, units per second squared. UNVERIFIED placeholder.</summary>
    public const float CannonWindAcceleration = 25f;

    /// <summary>How far past the farthest target a shot may fly before it counts as gone. UNVERIFIED placeholder.</summary>
    public const float CannonDistanceMargin = 3000f;

    /// <summary>Stat points for the three scored shots' result indexes (practice excluded).</summary>
    public static int CannonPoints(IEnumerable<int> scoredResults) {
        var total = scoredResults.Sum(r => r >= 0 && r < CannonResultValues.Length ? CannonResultValues[r] : 0);
        var points = 0;
        for (var i = 0; i < CannonPointThresholds.Length; i++) {
            if (total >= CannonPointThresholds[i]) {
                points = i + 1;
            }
        }

        return points;
    }

    /// <summary>The result index of a shot that crossed the target plane <paramref name="distance"/> from its centre.</summary>
    public static int CannonResult(float distance) {
        if (float.IsNaN(distance) || distance < 0) {
            return 0;
        }

        // Inside the centre ring is Perfect (3), the next Good (2), the next Ok (1); the outer ring and misses score 0.
        for (var ring = 0; ring < CannonRingRadii.Length - 1; ring++) {
            if (distance <= CannonRingRadii[ring]) {
                return CannonRingRadii.Length - 1 - ring;
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ Gobbler Drop

    /// <summary>
    /// How long a jump lets the pet catch food higher up, seconds. UNVERIFIED placeholder; the client settings give the
    /// jump height (m_fJumpHeight) but not its duration.
    /// </summary>
    public const double DropJumpSeconds = 0.75;

    /// <summary>A short grace after the clock runs out before the result is sent, so the client shows Time's Up.</summary>
    public const double TimesUpGraceSeconds = 2.0;

    // ------------------------------------------------------------------ Maze

    /// <summary>Starting clock, seconds: the client's command 0 sets its clock to 60,000 ms. Confidence: client.</summary>
    public const double MazeStartSeconds = 60;

    /// <summary>A clock adds ten seconds: client command 10; May 22 2010 Test Realm account. Confidence: client + verified.</summary>
    public const double MazeClockSeconds = 10;

    /// <summary>Clocks in one maze: May 22 2010 Test Realm account ("three clocks"). Confidence: verified (Test Realm).</summary>
    public const int MazeClocks = 3;

    /// <summary>Speed boosts in one maze. UNVERIFIED placeholder.</summary>
    public const int MazeSpeedBoosts = 2;

    /// <summary>Ghost-eating stars in one maze. UNVERIFIED placeholder.</summary>
    public const int MazeStars = 2;

    /// <summary>A star lets the pet eat ghosts for ten seconds: client command 11. Confidence: client.</summary>
    public const double MazeImmunitySeconds = 10;

    /// <summary>A ghost freezes the pet for three seconds: client command 7. Confidence: client.</summary>
    public const double MazeFreezeSeconds = 3;

    /// <summary>A ghost cannot catch the pet again this soon after a freeze ends. UNVERIFIED placeholder.</summary>
    public const double MazeGhostGraceSeconds = 2;

    /// <summary>An eaten ghost stays gone this long (the maze spawners' respawn rate, spawnData.xml). Confidence: client.</summary>
    public const double MazeGhostRespawnSeconds = 30;

    /// <summary>
    /// Snacks for a full result (4 points): "70 for a perfect game", May 2 2010 Test Realm comment
    /// (thefriendlynecromancer.blogspot.com "Et tu, Brute?"). Confidence: verified (Test Realm), UNVERIFIED for October.
    /// </summary>
    public const int MazeFullScore = 70;

    /// <summary>Score for an eaten ghost. UNVERIFIED placeholder (0: only snacks count).</summary>
    public const int MazeGhostScore = 0;

    /// <summary>Pickup radius of snacks and bonuses (PetSnackCollectorBehavior m_radius 200). Confidence: client.</summary>
    public const float MazePickupRadius = 200f;

    /// <summary>Catch radius of a ghost (MazeGhost01 PetSnackCollectorBehavior m_radius 150). Confidence: client.</summary>
    public const float MazeGhostRadius = 150f;

    /// <summary>
    /// Stat points for a Gobbler Drop fullness or a Maze score: the share of the full score, rounded, 0..4 (the rule the
    /// Dance Game already uses). The partial thresholds are UNVERIFIED for October 2010.
    /// </summary>
    public static int ScorePoints(int score, int fullScore) => PetRules.GamePoints(score, fullScore);

}
