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
 * AMBIENT WIZARD IDENTITY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: who an ambient wizard is, made once from a seed and then kept
 * in the server's AmbientWizards collection: a name from the game's own
 * first/middle/last name tables, boy or girl, a school, a level that fits
 * the world it lives in, and a look from the 2010-05-25 creation choices
 * (classic-data/appearance/creation-2010-05-25.yaml on client/c35): the
 * first ten hair shapes with their ten colours, the seven classic skin
 * tones, the ten classic faces, and one of the fourteen starter-gear
 * colours with a trim colour. Same seed, same wizard.
 *
 * USAGE EXAMPLE:
 * var id = AmbientIdentity.Generate(seed: 7, homeZone: "WizardCity/WC_Hub", tables, levels: (1, 12));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;

namespace Imlight.Classic.Ambient;

/// <summary>The seven schools a 2009 wizard could pick, by their game names.</summary>
public enum AmbientSchool { Fire, Ice, Storm, Myth, Life, Death, Balance }

/// <summary>Sizes of the game's character name tables (CharacterNames.xml).</summary>
/// <param name="FirstBoy">FirstName_HumanMale.</param>
/// <param name="FirstGirl">FirstName_HumanFemale.</param>
/// <param name="Middle">MiddleName_Human.</param>
/// <param name="Last">LastName_Human.</param>
public sealed record NameTableSizes(int FirstBoy, int FirstGirl, int Middle, int Last);

/// <summary>
/// An ambient wizard's look as WizardCharacterBehavior numbers (the server's race-table indices).
/// </summary>
public sealed record AmbientLook(bool Female, byte HairModel, byte HairColor, byte SkinColor, byte Face,
                                 byte ClothingColor, byte TrimColor);

/// <summary>
/// Who an ambient wizard is. <see cref="NameKeys"/> packs the first, middle and last name indices as m_nameKeys does.
/// </summary>
public sealed record AmbientIdentity(int Seed, uint NameKeys, AmbientSchool School, byte Level, AmbientLook Look,
                                     string HomeZone, AmbientTemper Temper) {

    /// <summary>The first name's table index.</summary>
    public int FirstNameIndex => (int) (NameKeys >> 16) & 0xFF;

    /// <summary>
    /// Makes the wizard for <paramref name="seed"/>. Name indices skip each table's first entry (index 0 means "no part"
    /// for the middle and last names) and stay below 256 (the packed key has one byte per part).
    /// </summary>
    /// <param name="seed">Any number; the same seed gives the same wizard.</param>
    /// <param name="homeZone">The zone it lives in.</param>
    /// <param name="tables">The name table sizes.</param>
    /// <param name="levels">The inclusive level range for its home.</param>
    /// <returns>The identity.</returns>
    public static AmbientIdentity Generate(int seed, string homeZone, NameTableSizes tables, (byte Min, byte Max) levels) {
        ArgumentNullException.ThrowIfNull(tables);
        var rng = new Random(seed);
        var female = rng.Next(2) == 1;
        var first = Pick(rng, female ? tables.FirstGirl : tables.FirstBoy);
        var middle = Pick(rng, tables.Middle);
        var last = Pick(rng, tables.Last);
        var school = (AmbientSchool) rng.Next(7);
        var level = (byte) rng.Next(levels.Min, Math.Max(levels.Min, levels.Max) + 1);
        var hairModel = (byte) rng.Next(10);
        var look = new AmbientLook(
            Female: female,
            HairModel: hairModel,
            HairColor: (byte) (hairModel * 10 + rng.Next(10)), // the first 100 hair textures: ten colours per old shape
            SkinColor: (byte) rng.Next(7),
            Face: (byte) rng.Next(10),
            ClothingColor: SchoolColor(school, rng),
            TrimColor: (byte) rng.Next(14));
        var temper = (AmbientTemper) rng.Next(3);

        return new AmbientIdentity(seed, (uint) (first << 16 | middle << 8 | last), school, level, look, homeZone, temper);
    }

    /// <summary>
    /// Most wizards dress in their school's colours; some pick any of the fourteen starter colours. The colour numbers
    /// are the starter gear's Body_001..014 textures counted from zero.
    /// </summary>
    internal static byte SchoolColor(AmbientSchool school, Random rng) {
        if (rng.Next(4) == 0) {
            return (byte) rng.Next(14);
        }

        byte[] colors = school switch {
            AmbientSchool.Fire => [0, 11],
            AmbientSchool.Ice => [2, 9],
            AmbientSchool.Storm => [4, 13],
            AmbientSchool.Myth => [5, 3],
            AmbientSchool.Life => [6, 7],
            AmbientSchool.Death => [8, 12],
            _ => [10, 1],
        };

        return colors[rng.Next(colors.Length)];
    }

    private static int Pick(Random rng, int tableSize)
        => tableSize <= 1 ? 0 : rng.Next(1, Math.Min(tableSize, 256));

    /// <summary>
    /// The level range of ambient wizards in a zone: Wizard City's streets 1-12, later worlds around their quests' levels,
    /// capped by the profile's level cap.
    /// </summary>
    /// <param name="zone">The zone path, such as Krokotopia/KT_Hub.</param>
    /// <param name="levelCap">The highest level the profile allows.</param>
    /// <returns>The inclusive range.</returns>
    public static (byte Min, byte Max) LevelsFor(string zone, int levelCap) {
        var world = zone.Split('/')[0];
        (int Min, int Max) range = world.ToLowerInvariant() switch {
            "krokotopia" => (10, 22),
            "grizzleheim" or "grizzleheimlite" => (12, 30),
            "marleybone" => (20, 30),
            "mooshu" => (30, 40),
            "dragonspire" => (40, 50),
            _ => zone.Contains("WC_Hub", StringComparison.OrdinalIgnoreCase) ? (1, 20) : (1, 12),
        };
        var cap = Math.Max(1, levelCap);

        return ((byte) Math.Min(range.Min, cap), (byte) Math.Min(range.Max, cap));
    }

}

/// <summary>How chatty an ambient wizard is.</summary>
public enum AmbientTemper { Quiet, Friendly, Chatty }
