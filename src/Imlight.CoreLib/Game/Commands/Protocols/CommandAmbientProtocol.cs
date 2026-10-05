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
 * AMBIENT WIZARD COMMANDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-05): QA tools for ambient wizards (Classic/Ambient):
 * .ambient status (this zone's ambient wizards: level, school, what they
 * are doing and how far away), .ambient call [N] (up to N of this zone's
 * free ambient wizards wander over to 400-800 units from you, as if their
 * walk brought them there; for playbot checks of help offers and dungeon
 * grouping, which need a wizard nearby).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandAmbientProtocol : CommandProtocol {

    internal override string Group { get; set; } = "ambient";

    [Command("status")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void StatusCommand() {
        var here = Context.Character.Location;
        var zone = Context.Character.Zone;
        var wizards = AmbientWizards.All.Where(w => string.Equals(w.Zone, zone, StringComparison.OrdinalIgnoreCase)).ToList();
        InformSenderClient(wizards.Count == 0 ? "No ambient wizard in this zone."
            : string.Join("; ", wizards.Select(w => $"{w.Name} L{w.Wizard.MagicSchoolBehavior.Level} {w.Identity.School} {w.Activity} "
                + $"{(int) MathF.Sqrt((w.Position.X - here.X) * (w.Position.X - here.X) + (w.Position.Y - here.Y) * (w.Position.Y - here.Y))}")));
    }

    [Command("call")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CallCommand(string howMany) {
        var count = int.TryParse(howMany, out var n) ? n : 2;
        if (!AmbientWizards.TryGetGroup(Context.ZoneActor, out var group)) {
            InformSenderClient("No ambient wizard in this zone.");
            return;
        }

        group.Tell(new AmbientCall(Context.Character.Location, Math.Clamp(count, 1, 12)));
        InformSenderClient($"Up to {count} ambient wizard(s) wander over.");
    }

}
