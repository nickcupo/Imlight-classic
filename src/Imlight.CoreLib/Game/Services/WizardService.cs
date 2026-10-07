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
 * WIZARD SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages wizard character state, including location tracking, 
 * level management, and game state synchronization.
 * 
 * USAGE EXAMPLE:
 * Internal service handling wizard-specific interactions within 
 * the game server session.
 * 
 * NOTE:
 * - Updates wizard location and orientation
 * - Manages level-up mechanics and stat updates
 * - Provides internal wizard state management
 * 
 * TODO:
 * - Enhance level-up stat calculation
 * - Review location and orientation compression logic
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Akka.Actor;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Character;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Math;
using Microsoft.Extensions.ObjectPool;
using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Services;

internal class WizardService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const float ORIENTATION_TOLERANCE = 1.035f;

    private Wizard _activeWizard;
    private CoreObject _activeWizardGameObject;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new WizardService(parentActor));

    protected override void OnPreDispose() {
        try {
            _activeWizard?.SaveLocation();
        }
        catch (Exception ex) {
            Logger.Error("Failed to save wizard location during disconnect: {0}",
                Logger.Args(ex));
        }
        finally {
            base.OnPreDispose();
        }
    }

    // CLASSIC: the directory answers for this session only while it lives.
    protected override void PostStop() {
        ActiveWizardDirectory.Remove(SessionActor?.ActorRef);
        base.PostStop();
    }

    #region Internal Handlers

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveZoneAddPlayerResponse(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP message) {
        _activeWizardGameObject = message.WizardGameObject;
        ActiveWizardDirectory.SetGameObject(SessionActor.ActorRef, _activeWizardGameObject); // CLASSIC: no Ask needed.
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD))]
    private void ReceiveSetActiveWizard(CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD message) {
        _activeWizard = message.Wizard;
        ActiveWizardDirectory.SetWizard(SessionActor.ActorRef, _activeWizard); // CLASSIC: no Ask needed.
        _activeWizard.UpdateLastLoginTime((uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD))]
    private void ReceiveQueryActiveWIzard(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD message)
        => Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER() {
            Wizard = _activeWizard,
            WizardGameObject = _activeWizardGameObject
        });

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_LEVELUP))]
    private void ReceiveSetLevel(CHARACTER_103_PROTOCOL.MSG_LEVELUP message)
        => ApplyLevel(_activeWizard, message.NewLevel, SendToSocket, packet => ZoneBroadcast(packet, false), CloseSession);

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_GAINXP))]
    private void ReceiveGainXP(CHARACTER_103_PROTOCOL.MSG_GAINXP message)
        => ApplyExperience(_activeWizard, message.XP, SendToSocket, packet => ZoneBroadcast(packet, false), CloseSession);

    // CLASSIC: the production wrapper emits only an acknowledged receipt, in the existing native order.
    internal static bool ApplyLevel(Wizard live, byte level, Action<Imcodec.MessageLayer.IMessage> send,
        Action<Imcodec.MessageLayer.IMessage> broadcast, System.Action close) {
        try {
            if (!WizardProgressionTransactions.TrySetLevel(live, level, resetMismatchedXp: true, out var receipt)) return false;
            SendLevelReceipt(receipt, send, broadcast);
            return true;
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(live)) close();
            throw;
        }
    }

    internal static bool ApplyExperience(Wizard live, int xp, Action<Imcodec.MessageLayer.IMessage> send,
        Action<Imcodec.MessageLayer.IMessage> broadcast, System.Action close) {
        try {
            if (!WizardProgressionTransactions.TryGainExperience(live, xp, out var receipt)) return false;
            SendLevelReceipt(receipt, send, broadcast);
            if (receipt.OldLevel != receipt.Level && receipt.Level == MagicLevelsConfig.MaxLevel
                && MagicLevelsConfig.MaxLevelXp is not null) ClassicGate.LevelCapReached(live.CharId, receipt.Level);
            if (receipt.XpMessage is not null) send(receipt.XpMessage);
            return true;
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(live)) close();
            throw;
        }
    }

    private static void SendLevelReceipt(ProgressionReceipt receipt, Action<Imcodec.MessageLayer.IMessage> send,
        Action<Imcodec.MessageLayer.IMessage> broadcast) {
        foreach (var packet in receipt.LevelMessages) {
            if (packet is WIZARD_12_PROTOCOL.MSG_LEVELUP) broadcast(packet);
            else send(packet);
        }
    }

    #endregion

    #region Game Handlers

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENTMOVE))]
    private void ReceiveClientMove(GAME_5_PROTOCOL.MSG_CLIENTMOVE message) {
        // CLASSIC: a client may move before its MSG_ATTACH has set the wizard.
        if (_activeWizard is null) {
            Logger.Debug("SessionActor {SessionId} ignored a move sent before the attach.",
                Logger.Args(SessionActor.SessionID));

            return;
        }

        // Save the player's location and direction on interval.
        // Restore actual location information, as it is compressed by a factor of 4 and unsigned.
        // Yaw is represented in radians in the client, but transmitted to the server as degrees.
        var position = new Vector3(
            unchecked((short) message.LocationX * 4),
            unchecked((short) message.LocationY * 4),
            unchecked((short) message.LocationZ * 4));
        _activeWizard.Location = position;

        // Direction is a byte and it's packed. Unpack it and convert it to radians.
        var initDir = message.Direction;
        var degrees = initDir * (360f / byte.MaxValue) * ORIENTATION_TOLERANCE;
        var radians = degrees * (System.MathF.PI / 180f);
        _activeWizard.Orientation = new Vector3(0, 0, radians);
    }

    #endregion
    
}
