// CLASSIC: arena NPC hand cycling and RNG stay on the duel actor and use the same validated move path as players.
using System;
using System.Collections.Generic;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Pvp;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {
    private ulong _arenaAiSeed;
    private ulong _arenaAiDuelId;
    private readonly Dictionary<int, Random> _arenaAiStreams = [];

    internal Random ArenaAiRng(int slot) {
        var duelId = Duel?.m_duelID.Full ?? 0;
        if (_arenaAiSeed != DuelSeed || _arenaAiDuelId != duelId) {
            _arenaAiStreams.Clear(); _arenaAiSeed = DuelSeed; _arenaAiDuelId = duelId;
        }
        if (!_arenaAiStreams.TryGetValue(slot, out var rng))
            _arenaAiStreams[slot] = rng = StreamFor(CombatRng.AiStream(slot));
        return rng;
    }

    internal void CycleArenaAmbientHand(CombatDuelSubCircle circle) {
        if (!IsArenaPvp) return;
        for (var count = 0; count < 2; count++) {
            if (ArenaPvpBrain.DiscardChoice(ArenaPvpCombat.ViewFor(this, circle)) is not { } index) break;
            var card = circle.GetSpellFromLastHand((byte) index);
            if (card is null || card.m_treasureCard) break;
            ReceiveCombatMove(new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor = circle.ParticipantActor, MoveType = (byte) CombatMoveType.Discard, SpellSelection = (byte) index,
            });
            // Discard does not draw a replacement or submit the round action. The final cast/pass is chosen afterward.
        }
    }
}
