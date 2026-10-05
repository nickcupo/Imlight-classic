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
 * CLIENT TRUST RULES
 * ========================================================================
 *
 * PURPOSE:
 * The checks the server makes on values a client sends with an economy request (training, shops, treasure cards,
 * dyes, character creation, service range) instead of trusting them. Each rule here is pure; the CoreLib service
 * that receives the message calls it with what it looked up and answers with the failure reply the stock
 * r806919 client already understands.
 *
 * USAGE EXAMPLE:
 * var refusal = TrainRules.Check(known: false, level: 3, requiredLevel: 5, ...);  // TrainRefusal.LevelTooLow
 * var near = ServiceRange.IsWithin(0, 0, 0, 300, 400, 0);                          // true (500 units)
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Rules;

/// <summary>Why a training request was refused (<see cref="None"/> when it may go ahead).</summary>
public enum TrainRefusal : byte {
    None,
    AlreadyKnown,
    LevelTooLow,
    MissingRequiredSpell,
    NotEnoughTrainingPoints,
}

/// <summary>
/// The trainer's rules, the same ones InteractTrainerComponent uses to show an option as trainable: a spell of the
/// wizard's own school is free, any other costs one training point; the wizard must reach the entry's level, and
/// know its required spell; a known spell is not offered.
/// </summary>
public static class TrainRules {

    /// <summary>Training points a spell costs: 0 in the wizard's own school, else 1.</summary>
    public static int Cost(string? wizardSchool, string? spellSchool)
        => string.Equals(wizardSchool, spellSchool, StringComparison.Ordinal) ? 0 : 1;

    /// <summary>Whether a wizard may train a trainer entry.</summary>
    /// <param name="known">The wizard already has the spell.</param>
    /// <param name="level">The wizard's level.</param>
    /// <param name="requiredLevel">The entry's level.</param>
    /// <param name="requiredSpellId">The entry's required spell (0 for none).</param>
    /// <param name="hasSpell">True when the wizard knows a spell template.</param>
    /// <param name="trainingPoints">The wizard's training points.</param>
    /// <param name="cost">The spell's cost (<see cref="Cost"/>).</param>
    public static TrainRefusal Check(bool known, int level, int requiredLevel, ulong requiredSpellId,
                                     Func<ulong, bool> hasSpell, int trainingPoints, int cost) {
        if (known) {
            return TrainRefusal.AlreadyKnown;
        }

        if (trainingPoints < cost) {
            return TrainRefusal.NotEnoughTrainingPoints;
        }

        if (level < requiredLevel) {
            return TrainRefusal.LevelTooLow;
        }

        if (requiredSpellId != 0 && !hasSpell(requiredSpellId)) {
            return TrainRefusal.MissingRequiredSpell;
        }

        return TrainRefusal.None;
    }

}

/// <summary>
/// How far from a service NPC or object (trainer, shop, bank chest, Bazaar, potion vendor) a request may come. The
/// client only offers a service when the wizard walks up to it; the radius is generous so a position the client
/// has not reported yet (it sends moves on an interval) never refuses a wizard standing at the counter.
/// </summary>
public static class ServiceRange {

    /// <summary>Game units: about two seconds of running at the player run speed (600).</summary>
    public const float Radius = 1200f;

    /// <summary>True when the two points are no more than <paramref name="radius"/> apart.</summary>
    public static bool IsWithin(float ax, float ay, float az, float bx, float by, float bz, float radius = Radius) {
        if (!float.IsFinite(ax) || !float.IsFinite(ay) || !float.IsFinite(az)
            || !float.IsFinite(bx) || !float.IsFinite(by) || !float.IsFinite(bz)) {
            return false;
        }

        double dx = ax - bx, dy = ay - by, dz = az - bz;

        return (dx * dx) + (dy * dy) + (dz * dz) <= (double) radius * radius;
    }

}

/// <summary>
/// Dye layers a client may ask for. Public equipment carries each layer in 5 bits, so a layer is 0..31; a pet's
/// layers are its template's own texture choices.
/// </summary>
public static class DyeRules {

    public const int MaxDye = 31;

    public static bool IsInRange(int dye) => dye is >= 0 and <= MaxDye;

    /// <summary>A pet layer: unchanged, or one of the template's choices when it has more than one.</summary>
    public static bool IsPetLayerDye(int dye, int currentDye, int templateColorCount)
        => dye == currentDye || (templateColorCount > 1 && dye >= 0 && dye < templateColorCount);

    /// <summary>
    /// Whether a shop offers colors for an item: the same test the shop price uses to add the dyed-item markup, so a
    /// chosen color is always one the wizard paid for.
    /// </summary>
    public static bool IsDyeable(int numPrimaryColors, int numSecondaryColors)
        => numPrimaryColors != 1 && numSecondaryColors != 0;

    /// <summary>
    /// The layer a bought item gets: the requested one when the shop offers colors for it and the request is valid,
    /// else the template's default.
    /// </summary>
    /// <param name="requested">The client's texture or decal.</param>
    /// <param name="templateDefault">The layer the finalized item already has.</param>
    /// <param name="dyeable">The shop offers colors (<see cref="IsDyeable"/>).</param>
    /// <param name="isPet">The item is a pet.</param>
    /// <param name="petColorCount">For a pet, the template's choices for this layer.</param>
    public static int BuyLayer(int requested, int templateDefault, bool dyeable, bool isPet, int petColorCount) {
        if (requested == templateDefault) {
            return templateDefault;
        }

        if (isPet) {
            return IsPetLayerDye(requested, templateDefault, petColorCount) ? requested : templateDefault;
        }

        return dyeable && IsInRange(requested) ? requested : templateDefault;
    }

}

/// <summary>Treasure card purchases: how many copies one request may buy, and how many the book holds.</summary>
public static class TreasureShopRules {

    /// <summary>The most copies one purchase may ask for (the client's quantity box).</summary>
    public const int MaxQuantity = 99;

    /// <summary>
    /// The most treasure cards a wizard's book holds through the shop. Not a 2009 figure: a server cap so that a
    /// request cannot grow the character document without bound.
    /// </summary>
    public const int BookCapacity = 999;

    /// <summary>True when a purchase may go ahead.</summary>
    /// <param name="quantity">The client's quantity.</param>
    /// <param name="unitPrice">The vendor's price for one card.</param>
    /// <param name="held">Treasure cards the wizard already has.</param>
    public static bool CanBuy(int quantity, int unitPrice, int held)
        => quantity is >= 1 and <= MaxQuantity
        && unitPrice > 0
        && held >= 0
        && (long) held + quantity <= BookCapacity;

}

/// <summary>Character creation: the school and name a new wizard may take.</summary>
public static class CharacterCreationRules {

    /// <summary>The seven player schools (MagicSchool string hashes): Ice, Life, Fire, Myth, Death, Storm, Balance.</summary>
    public static readonly IReadOnlySet<uint> PlayerSchools = new HashSet<uint> {
        72777, 2330892, 2343174, 2448141, 78318724, 83375795, 1027491821,
    };

    public static bool IsPlayerSchool(uint school) => PlayerSchools.Contains(school);

    /// <summary>
    /// True when packed name keys point into the creation name tables: a first name (always present) and optional
    /// middle and last parts, where 0 means none.
    /// </summary>
    /// <param name="nameKeys">The packed keys (first in bits 16-23, middle 8-15, last 0-7).</param>
    /// <param name="firstNames">Entries in the first-name table for the wizard's gender.</param>
    /// <param name="middleNames">Entries in the middle-name table.</param>
    /// <param name="lastNames">Entries in the last-name table.</param>
    public static bool IsValidName(uint nameKeys, int firstNames, int middleNames, int lastNames) {
        var first = (int) (nameKeys >> 16) & 0xFF;
        var middle = (int) (nameKeys >> 8) & 0xFF;
        var last = (int) nameKeys & 0xFF;

        return first < firstNames
            && (middle == 0 || middle < middleNames)
            && (last == 0 || last < lastNames);
    }

}

/// <summary>Who may read an account's infraction history with <c>.account infractions</c>.</summary>
public static class InfractionAccess {

    /// <summary>A moderator may read any account's; anyone else only their own.</summary>
    public static bool CanView(string? callerUsername, bool callerIsModerator, string? target)
        => callerIsModerator
        || (!string.IsNullOrEmpty(callerUsername) && string.Equals(callerUsername, target, StringComparison.OrdinalIgnoreCase));

}
