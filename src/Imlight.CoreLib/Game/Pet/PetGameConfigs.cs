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
 * CLASSIC PET GAME CONFIGS
 * ========================================================================
 *
 * PURPOSE:
 * The client's PetGames.xml (Root.wad): each pet game's name, energy costs
 * and track choices (the stat changes of a full score), which the kiosk
 * window shows and the server pays out.
 *
 * USAGE EXAMPLE:
 * if (PetGameConfigs.TryGet("PetGameDance", out var info)) { ... info.m_trackChoices[track] ... }
 *
 * NOTE:
 * CLASSIC: only the four 2010 games are offered (Pet Mini Games, oldid
 * 122046): PetGameDance, PetGameDrop (Gobbler Drop), PetGameCannon (Mortar
 * Mayhem) and PetGameMaze. Siege, the obstacle course, Grub Guardian and the
 * derby came later.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

internal sealed class PetGameConfigs : RootSingleResourceSingleton<PetGameConfigs>, IMemoryStreamDisposable {

    /// <summary>CLASSIC: the pet games of the May 2010 Pavilion, by kiosk object name.</summary>
    public static readonly IReadOnlyDictionary<string, string> KioskGames = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["PetGameDanceKiosk"] = "PetGameDance",
        ["PetGameDropKiosk"] = "PetGameDrop",
        ["PetGameCannonKiosk"] = "PetGameCannon",
        ["PetGameMazeKiosk"] = "PetGameMaze",
    };

    protected override string ResourceName => "PetGames.xml";

    private static Dictionary<string, PetGameInfo> s_games = new(StringComparer.Ordinal);

    protected override void AfterLoad() {
        var serializer = new BindSerializer();
        if (!serializer.Deserialize<PetGameConfig>(Stream.ToArray(), out var config) || config?.m_games is null) {
            Logger.Error("Failed to deserialize PetGames.xml; the pet game kiosks will not open.");

            return;
        }

        s_games = config.m_games.Where(g => g is not null)
            .GroupBy(g => g.m_name.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Logger.Information("Loaded {0} pet games ({1}).", Logger.Args(s_games.Count, string.Join(", ", s_games.Keys)));
    }

    public static bool TryGet(string game, out PetGameInfo info) {
        _ = Instance;
        info = null;

        return game is not null && KioskGames.Values.Contains(game, StringComparer.Ordinal) && s_games.TryGetValue(game, out info);
    }

    public void DisposeStream() => Stream?.Dispose();

}
