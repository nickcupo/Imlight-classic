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
 * CLASSIC ZONE MAP
 * ========================================================================
 * 
 * PURPOSE:
 * The answer to "may a player enter this zone under the active profile",
 * with the audit reason and the polite text the player sees.
 * 
 * USAGE EXAMPLE:
 * var decision = ClassicRuntime.Rules.IsZoneAllowed("Celestia/CL_Hub");
 * if (!decision.Allowed) { inform(decision.PlayerMessage, true); }
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

namespace Imlight.Classic.Zones;

/// <summary>
/// A zone access decision.
/// </summary>
/// <param name="Allowed">True when the zone is open.</param>
/// <param name="Zone">The zone asked about.</param>
/// <param name="WorldId">The effective world, if any.</param>
/// <param name="HomeWorldId">The world of the zone's prefix, if its home is a world.</param>
/// <param name="AreaId">The area of the zone's prefix, if its home is an area.</param>
/// <param name="Reason">Audit text, including the rule source and confidence.</param>
/// <param name="PlayerMessage">Polite text for the player; empty when allowed.</param>
/// <param name="RuleSource">Where the deciding rule is declared.</param>
/// <param name="Confidence">The deciding rule's confidence, if it has one.</param>
public sealed record ZoneDecision(
    bool Allowed,
    string Zone,
    string? WorldId,
    string? HomeWorldId,
    string? AreaId,
    string Reason,
    string PlayerMessage,
    string RuleSource,
    string? Confidence);
