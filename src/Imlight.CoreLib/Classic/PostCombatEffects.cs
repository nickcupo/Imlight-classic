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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: puts the after-duel effects (PostCombatEffect, PostCombatEffect2;
 * see Imlight.Classic.Quests.PostCombatGrace) on a wizard and builds the
 * MSG_ADDEFFECT / MSG_REMOVEEFFECT that make everyone in the zone see the
 * wizard go translucent and solid again. Players (CombatService) and ambient
 * wizards (AmbientZone) share it.
 *
 * USAGE EXAMPLE:
 * var add = PostCombatEffects.Put(wizard, gameObjectId, "PostCombatEffect", endUtc);
 *
 * NOTE:
 * The client looks the template up by name hash; the effect is public, so the
 * broadcast must include the wizard itself.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

/// <summary>The after-duel effects on a wizard (see the file header).</summary>
internal static class PostCombatEffects {

    internal static readonly uint StillId = StringHash.Compute(PostCombatGrace.StillEffectName);
    internal static readonly uint MovingId = StringHash.Compute(PostCombatGrace.MovingEffectName);

    /// <summary>
    /// Takes any after-duel effect off <paramref name="wizard"/> and puts <paramref name="effectName"/> on, until
    /// <paramref name="endUtc"/>. Returns the client messages, removals first (the removed effect's
    /// RemoveTranslucentEffect must not undo the new one's fade).
    /// </summary>
    internal static List<IMessage> Put(Wizard wizard, ulong gameObjectId, string effectName, DateTime endUtc) {
        var messages = Take(wizard, gameObjectId);
        var effect = new NamedEffect {
            m_effectNameID = StringHash.Compute(effectName),
            m_internalID = wizard.GameEffects.Count + 1,
            m_endTime = (uint) new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
        };

        wizard.GameEffects.Add(effect);
        wizard.IsInCombatGrace = true;

        // The client reads MSG_ADDEFFECT with Transmit | AuthorityTransmit (GameClient::MSG_AddEffect).
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        if (serializer.Serialize(effect, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
            messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = gameObjectId, EffectData = data });
        }

        return messages;
    }

    /// <summary>Takes every after-duel effect off <paramref name="wizard"/>; the wizard can be aggroed again.</summary>
    internal static List<IMessage> Take(Wizard wizard, ulong gameObjectId) {
        var messages = new List<IMessage>();
        while (wizard.GameEffects.Find(e => e is NamedEffect n && (n.m_effectNameID == StillId || n.m_effectNameID == MovingId))
               is NamedEffect effect) {
            wizard.GameEffects.Remove(effect);
            messages.Add(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = gameObjectId,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }

        wizard.IsInCombatGrace = false;
        return messages;
    }

}
