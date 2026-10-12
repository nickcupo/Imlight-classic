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
 * AMBIENT WIZARD PETS AND HATCHING
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): the pets ambient wizards own and how they hatch
 * with real players at the Pet Pavilion (May 2010, owner extra). Pure:
 * which pet an ambient wizard has (from its seed), how grown it is, when
 * it may hatch, and the human-feeling waits around a hatch.
 *
 * Rules (dated sources in PetHatchRules and the report):
 *  - The pets are the eight a 2009 wizard bought from Tennant Wastelander
 *    (the server's classic pet shop stock): Bloodbat, Cyclops, Fire Elf,
 *    Firecat, Imp, Piggle, Dragon, Unicorn.
 *  - "Pets that you wish to combine must be at least in the Adult stage"
 *    (Pet Pavilion, oldid 69921, 2010-06-01); "You may only breed a pet
 *    once every 24 hours" (The Pet Hatchery, oldid 74051, 2010-06-28).
 *    Ambient pets keep the same rules: a Teen cannot hatch, and each
 *    ambient pet hatches once a day.
 *  - The Pavilion opened on 2010-05-26, so its first weeks' pets were
 *    young: most ambient pets are Adult, some Teen (they cannot hatch
 *    yet), a few Ancient, rarely Epic.
 *  - Waits (our own, to feel like a person and never block a player): an
 *    ambient wizard takes a free hatching spot 4-10 s after a player waits
 *    alone on the other, picks its pet 2-4 s later, confirms 3-8 s after
 *    the player picked theirs, and leaves after 120 s without a confirm
 *    (or at once when the player leaves).
 *
 * USAGE EXAMPLE:
 * var pet = AmbientPets.For(identity.Seed);
 * if (AmbientPets.MayHatch(pet.Level, lastHatchUnix, nowUnix)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Immutable;
using Imlight.Classic.Pets;

namespace Imlight.Classic.Ambient;

/// <summary>An ambient wizard's pet: which template, how grown, and the seed its talents are learned with.</summary>
/// <param name="TemplateId">The pet item template.</param>
/// <param name="Level">PetRules level (Teen 2 .. Epic 5).</param>
/// <param name="GrowthSeed">Seeds the talents it learned and its trained stats.</param>
/// <param name="TrainedShare">How far its stats were trained from start towards their maximums (0..1).</param>
public sealed record AmbientPet(ulong TemplateId, int Level, int GrowthSeed, double TrainedShare);

/// <summary>Ambient wizards' pets and hatching waits (see the file header).</summary>
public static class AmbientPets {

    /// <summary>
    /// Tennant Wastelander's pets (SpiralDB NpcInventory 175491, the server's 2009 pet shop): Imp, Bloodbat, Firecat,
    /// Piggle, Unicorn, Dragon (Puff), Fire Elf, Cyclops.
    /// </summary>
    public static readonly ImmutableArray<ulong> ClassicPetTemplates = [100453, 100447, 100450, 69082, 100333, 87115, 100408, 100407];

    /// <summary>The pets' names as the client shows them (pets.json display names, r806919).</summary>
    public static string NameOf(ulong templateId) => templateId switch {
        100453 => "Imp", 100447 => "Bloodbat", 100450 => "Firecat", 69082 => "Piggle", 100333 => "Unicorn",
        87115 => "Dragon", 100408 => "Fire Elf", 100407 => "Cyclops", _ => "pet",
    };

    /// <summary>A player who waits alone on a hatching spot gets an ambient partner after this many seconds.</summary>
    public const int JoinMinSeconds = 4, JoinMaxSeconds = 10;

    /// <summary>The ambient partner shows its pet this long after taking the spot.</summary>
    public const int PickMinSeconds = 2, PickMaxSeconds = 4;

    /// <summary>The ambient partner confirms this long after the player picked (or changed) a pet.</summary>
    public const int ConfirmMinSeconds = 3, ConfirmMaxSeconds = 8;

    /// <summary>The ambient partner leaves a hatch the player has not confirmed in this long.</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(120);

    /// <summary>A player who asked in chat keeps a promised partner this long.</summary>
    public static readonly TimeSpan PromiseLimit = TimeSpan.FromSeconds(90);

    /// <summary>The pet of the ambient wizard with <paramref name="seed"/> (the same seed, the same pet).</summary>
    public static AmbientPet For(int seed) {
        var rng = new Random(unchecked(seed * 7919 + 17));
        var template = ClassicPetTemplates[rng.Next(ClassicPetTemplates.Length)];
        var roll = rng.NextDouble();
        var level = roll switch {
            < 0.15 => PetRules.Teen,
            < 0.75 => PetRules.Adult,
            < 0.95 => PetRules.Ancient,
            _ => PetRules.Epic,
        };
        var trained = Math.Round(Math.Clamp(0.15 * level + (rng.NextDouble() - 0.5) * 0.2, 0.05, 0.95), 2);

        return new AmbientPet(template, level, rng.Next(), trained);
    }

    /// <summary>True when a pet of <paramref name="level"/> that last hatched at <paramref name="lastHatchUnix"/> may hatch now.</summary>
    public static bool MayHatch(int level, long lastHatchUnix, long nowUnix)
        => level >= PetHatchRules.MinLevel
           && (lastHatchUnix <= 0 || nowUnix - lastHatchUnix >= (long) PetHatchRules.Cooldown.TotalSeconds);

    /// <summary>A wait in whole seconds between <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static TimeSpan Wait(Random random, int min, int max) => TimeSpan.FromSeconds(random.Next(min, max + 1));

    /// <summary>The stat a trained pet stands at: <paramref name="share"/> of the way from start to max.</summary>
    public static int TrainedStat(int start, int max, double share)
        => Math.Clamp((int) Math.Round(start + (max - start) * Math.Clamp(share, 0, 1), MidpointRounding.AwayFromZero), Math.Min(start, max), Math.Max(start, max));

    // ---- what they say -------------------------------------------------------------------------

    // CLASSIC (2026-10-10): shorter and plainer, dictionary words only (no "lf"; "can't" keeps its apostrophe).
    private static readonly string[] s_offers = [
        "anyone wanna hatch? my {pet} is {stage}", "hatch with me? {stage} {pet}", "who wants to hatch with a {pet}",
        "{stage} {pet}, anyone wanna hatch", "anyone hatch? i have a {pet}",
    ];

    private static readonly string[] s_answers = ["sure", "ok come to the hatch spots", "me", "sure what pet", "ok lets hatch"];

    private static readonly string[] s_joined = ["ok im here", "hi", "my {pet} is ready", "ok go"];

    private static readonly string[] s_done = ["ty!", "ty for the hatch", "can't wait to see the egg", "yay ty"];

    private static readonly string[] s_gone = ["sorry gtg", "nvm maybe later", "brb... actually gtg"];

    private static readonly string[] s_busy = ["sorry my pet already hatched today", "my pet is too young lol sorry"];

    /// <summary>Kinds of hatch talk.</summary>
    public enum Talk { Offer, Answer, Joined, Done, Gone, Busy }

    /// <summary>A line of hatch talk, picked by <paramref name="turn"/>.</summary>
    public static string Line(Talk kind, int turn, string petName, int level) {
        var lines = kind switch {
            Talk.Offer => s_offers, Talk.Answer => s_answers, Talk.Joined => s_joined, Talk.Done => s_done,
            Talk.Gone => s_gone, _ => s_busy,
        };
        var line = lines[(int) ((uint) turn % (uint) lines.Length)];
        return line.Replace("{pet}", string.IsNullOrWhiteSpace(petName) ? "pet" : petName.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{stage}", PetRules.LevelName(level).ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="text"/> asks around for a hatch ("anyone hatch?", "lf hatch", "hatch with me").</summary>
    public static bool AsksForHatch(string text)
        => !string.IsNullOrWhiteSpace(text)
           && System.Text.RegularExpressions.Regex.IsMatch(text, @"\bhatch(ing)?\b|\bbreed\b",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

}
