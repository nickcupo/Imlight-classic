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
 * SHARED WIZARD EFFECTS
 * ========================================================================
 *
 * PURPOSE:
 * Protects the runtime effect list shared by a wizard's service actors.
 *
 * USAGE EXAMPLE:
 * Equipment attachment sends a detached Snapshot while other services add effects.
 *
 * NOTE:
 * Effect fields must be initialized before publication.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Models.Player;

public sealed class WizardEffectCollection {
    private readonly object _sync = new();
    private readonly List<GameEffectBase> _effects = [];

    public int Count {
        get {
            lock (_sync) {
                return _effects.Count;
            }
        }
    }

    public void Add(GameEffectBase effect) {
        ArgumentNullException.ThrowIfNull(effect);
        lock (_sync) {
            _effects.Add(effect);
        }
    }

    public bool Remove(GameEffectBase effect) {
        lock (_sync) {
            return _effects.Remove(effect);
        }
    }

    public GameEffectBase Find(Predicate<GameEffectBase> predicate) {
        lock (_sync) {
            return _effects.Find(predicate);
        }
    }

    public void Clear() {
        lock (_sync) {
            _effects.Clear();
        }
    }

    public List<GameEffectBase> Snapshot() {
        lock (_sync) {
            return new List<GameEffectBase>(_effects);
        }
    }
}
