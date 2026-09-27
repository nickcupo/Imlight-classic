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
 * CLASSIC RULES MESSAGES
 * ========================================================================
 * 
 * PURPOSE:
 * The default texts a player sees when the classic rules refuse something.
 * A zone-map rule's own message overrides the zone defaults.
 * 
 * USAGE EXAMPLE:
 * inform(ClassicMessages.FeatureUnavailable(ClassicFeatures.Bazaar), true);
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

namespace Imlight.Classic;

/// <summary>
/// Player-facing refusal texts.
/// </summary>
public static class ClassicMessages {

    public const string AreaClosed = "That area isn't open yet.";
    public const string Unmapped = "That part of the Spiral isn't open yet.";
    public const string DormUnavailable = "Your dorm room isn't available yet.";
    public const string BazaarClosed = "The Bazaar isn't open yet.";
    public const string SeamstressClosed = "The Seamstress isn't open yet.";
    public const string JewelsUnavailable = "Jewels aren't available yet.";
    public const string Unavailable = "That isn't available yet.";

    /// <summary>
    /// The text for a closed world.
    /// </summary>
    /// <param name="worldName">The world's display name.</param>
    /// <returns>For example "Celestia isn't open yet."</returns>
    public static string WorldClosed(string worldName)
        => $"{worldName} isn't open yet.";

    /// <summary>
    /// The text for a feature that is switched off.
    /// </summary>
    /// <param name="featurePath">A <see cref="ClassicFeatures"/> path.</param>
    /// <returns>The feature's text, or <see cref="Unavailable"/>.</returns>
    public static string FeatureUnavailable(string featurePath)
        => featurePath switch {
            ClassicFeatures.Housing => DormUnavailable,
            ClassicFeatures.Bazaar => BazaarClosed,
            ClassicFeatures.Seamstress => SeamstressClosed,
            ClassicFeatures.Jewels => JewelsUnavailable,
            _ => Unavailable,
        };

}
