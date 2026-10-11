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
 * SPELL CINEMATICS MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Provides centralized loading and retrieval of spell cinematic templates
 * with methods to extract timing information for different spell stages
 * as they are defined in the Root.wad
 * 
 * USAGE EXAMPLE:
 * var summonTime = SpellCinematics.GetSpellSummonTime("Fire Cat");
 * var totalTime = SpellCinematics.GetSpellTotalTime("Thunder Snake");
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Collections.Generic;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Manages the loading, caching, and timing calculations for spell cinematic templates.
/// </summary>
internal class SpellCinematics : RootDirectoryResourceSingleton<SpellCinematics>, IMemoryStreamDisposable {

    private const float HANGING_EFFECT_ADD_TIME = 1.0f;

    protected override string DirectoryName => "Cinematics";

    private static readonly Dictionary<string, CinematicTemplate> s_cinematicTemplates = [];

    protected override void AfterLoad() {
        var serializer = new BindSerializer();
        var counter = 0;

        foreach (var file in base.Files) {
            var fileRecord = file.Key;
            var fileStream = file.Value;

            if (!serializer.Deserialize<CinematicTemplate>(fileStream?.ToArray(), 1, out var cinematicTemplate)) {
                Logger.Error("Could not deserialize {0} as {1}", 
                    Logger.Args(fileRecord.FileName, nameof(CinematicTemplate)));

                continue;
            }

            // SKip if the template is already in the dictionary.
            if (s_cinematicTemplates.ContainsKey(cinematicTemplate.m_name)) {
                continue;
            }

            s_cinematicTemplates.Add(cinematicTemplate.m_name, cinematicTemplate);
            counter++;
        }

        Logger.Information("Loaded {0} cinematic templates.", 
            Logger.Args(counter));
    }

    /// <summary>
    /// Retrieves a cinematic template based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The <see cref="CinematicTemplate"/> of the spell. </returns>
    internal static CinematicTemplate GetCinematicTemplate(string name) {
        // CLASSIC: the profile's players see the 2014 client's cinematic; time the cast from its stages.
        if (Imlight.CoreLib.Classic.ClassicSpellAnimations.TryGet(name, out var older)) {
            return older;
        }

        if (s_cinematicTemplates.TryGetValue(name, out var cinematicTemplate)) {
            return cinematicTemplate;
        }

        return null;
    }

    /// <summary>
    /// Retrieves the summon time of a spell based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The summon time of the spell.</returns>
    internal static float GetSpellSummonTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        // Search the acts of the template to find type `SummonCinematicStageTemplate`.
        foreach (var act in cinematicTemplate.m_stages) {
            if (act is SummonCinematicStageTemplate summonCinematicStageTemplate) {
                return summonCinematicStageTemplate.m_duration;
            }
        }

        return 0.0f;
    }

    /// <summary>
    /// Retrieves the duration of a spell's cinematic act.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The duration of the spell's cinematic act.</returns>
    internal static float GetSpellActTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        // CLASSIC: the longest act. Creature cinematics have several (NA Ghost: Act 3.33 s, Act2 3.67 s, Act3 4.67 s)
        // and the client picks one (it played Act3 for NA Ghost-01); the first one's length cut the rest of the round
        // short. Player spells have one act, so nothing changes for them.
        var longest = 0.0f;
        foreach (var act in cinematicTemplate.m_stages) {
            if (act is ActCinematicStageTemplate || (act.m_name?.ToString().StartsWith("Act") ?? false)) {
                longest = System.Math.Max(longest, act.m_duration);
            }
        }

        return longest;
    }

    /// <summary>
    /// Retrieves the casting time of a spell based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The casting time of the spell.</returns>
    internal static float GetSpellCastingTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        // Search the acts of the template to find type `CastingCinematicStageTemplate`.
        foreach (var act in cinematicTemplate.m_stages) {
            if (act.m_name == "Casting") {
                return act.m_duration;
            }
        }

        return 0.0f;
    }

    /// <summary>
    /// Calculates the total time of a spell's cinematic based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The total time of the spell's cinematic.</returns>
    internal static float GetSpellTotalTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        float totalTime = 0.0f;
        foreach (var act in cinematicTemplate.m_stages) {
            // Add 1 second to the total time if the act is a hanging effect.
            if (act.m_name.ToString().Contains("AddHanging")) {
                totalTime += HANGING_EFFECT_ADD_TIME;
                continue;
            }

            totalTime += act.m_duration;
        }

        return totalTime;
    }

    /// <summary>
    /// CLASSIC: how long the round waits for one cast of <paramref name="name"/>: the caster's cast, then every
    /// stage of the cinematic the client plays, each once.
    /// </summary>
    internal static float GetCastWaitTime(string name, float defaultCastTime)
        => string.IsNullOrEmpty(name) ? defaultCastTime : GetCastWaitTime(GetCinematicTemplate(name), defaultCastTime);

    /// <summary>
    /// CLASSIC: the round's wait for one cast of <paramref name="template"/>.
    ///
    /// The client first plays the caster's cast (the turn cursor, the Cast animation and the school's casting sign:
    /// 3.7 s from the cursor to the summon, 61.07 s to 64.76 s on the cinematic clock, for a player Blood Bat in the
    /// owner's client log of 2026-10-04 03:08), which
    /// is in the cinematic only when it has a Casting stage, else <paramref name="defaultCastTime"/> covers it. Then
    /// the client plays the stages in order. Two layouts:
    ///  * an Act stage with a length (Kraken: Summon 4.3 s, Act 3.3 s; the creature variants Act1/Act2/Act3, of which
    ///    the client plays one, are timed by the longest): summon + cast + longest act, plus any StartAOE/StopAOE
    ///    stage, which the client plays besides the act (r806919 Storm Lord: StopAOEAct1 5.8 s, then Act2 3.3 s);
    ///  * no Act stage with a length (r806919's AoE layout, StartAOEAct 0 s / Act 0 s / StopAOEAct 9.13 s for Ra, and
    ///    the Take/Give spells: Wraith, Vampire, Ghoul, Scarecrow): cast + the sum of every stage. This used to add the
    ///    summon a second time (summon + cast + total, where total already holds the summon): Ra waited 26.9 s for
    ///    16.5 s of stages, Wraith 30.3 s for 20.1 s.
    /// AddHanging stages count <see cref="HANGING_EFFECT_ADD_TIME"/> each, as before.
    /// </summary>
    internal static float GetCastWaitTime(CinematicTemplate template, float defaultCastTime) {
        if (template?.m_stages is null) {
            return defaultCastTime;
        }

        float summon = 0f, casting = 0f, longestAct = 0f, aoe = 0f, total = 0f;
        var summonSeen = false;
        foreach (var stage in template.m_stages) {
            if (stage is null) continue;
            var name = stage.m_name?.ToString() ?? "";
            var duration = stage.m_duration;
            if (!summonSeen && (stage is SummonCinematicStageTemplate || name == "Summon")) {
                summon = duration;
                summonSeen = true;
            }
            if (name == "Casting" && casting <= 0f) {
                casting = duration;
            }
            if (stage is ActCinematicStageTemplate || name.StartsWith("Act")) {
                longestAct = System.Math.Max(longestAct, duration);
            }
            if (name.StartsWith("StartAOE") || name.StartsWith("StopAOE")) {
                aoe += duration;
            }
            total += name.Contains("AddHanging") ? HANGING_EFFECT_ADD_TIME : duration;
        }

        var hasCasting = casting > 0.1f;
        var cast = hasCasting ? casting : defaultCastTime;
        if (longestAct > 0.1f) {
            return summon + cast + longestAct + aoe;
        }

        // Every stage once; the Casting stage is already the cast.
        return cast + total - (hasCasting ? casting : 0f);
    }

    public void DisposeStream() 
        => s_cinematicTemplates.Clear();

}
