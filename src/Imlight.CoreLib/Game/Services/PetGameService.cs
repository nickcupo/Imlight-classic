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
 * CLASSIC PET GAME SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Pet training at the Pet Pavilion's game kiosks: joining a pet game, the
 * Dance Game's rounds, the end-of-game rewards (stat points, experience,
 * pet energy), feeding one snack after the game, and pet level ups.
 *
 * USAGE EXAMPLE:
 * Client: MSG_PETGAMEJOIN(Game, Track) -> server MSG_PETGAMEJOINRSP, MSG_PETGAMEINIT;
 * client MSG_PETGAMEREADY -> MSG_PETGAMESTART; Dance: MSG_PETGAMEDANCE both ways per round;
 * server MSG_PETGAMEEND(PetGameEndData); client MSG_PETGAMEDATA([4][snack gid]) ->
 * MSG_PETGAMESNACKFEEDSUCCESS(PetGameEndData) (+ MSG_PETLEVELUP); client MSG_PETGAMEENDING closes.
 *
 * NOTE:
 * The message order and payloads were read from the official client r806919
 * (PetGameClientLogicBase, PetGameBase, PetGameDance); no live capture of a
 * KingsIsle server exists. MSG_PETGAMEDATA's first byte is the client's
 * command: 4 = feed this snack (u64 gid follows); 1 and 2 come from the
 * GUI's debug Win/Lose buttons and are honoured for QA accounts only. The
 * Drop, Cannon and Maze games need server-side game data the server does not
 * produce yet; they open and can be ended by a QA win or lose.
 * Energy is taken when a game ends with a score (2010: quitting costs none).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.CoreObject;
using Imlight.Classic;
using Imlight.Classic.Pets;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed partial class PetGameService(SessionActor sessionActor) : MessageService(sessionActor), IWithUnboundedStash {

    private const byte CommandDebugWin = 1;
    private const byte CommandDebugLose = 2;
    private const byte CommandFeedSnack = 4;
    private static readonly TimeSpan s_roundDelay = TimeSpan.FromSeconds(2);

    private sealed class Session {

        public string Game;
        public int Track;
        public ulong PetId;
        public DanceGame Dance;
        public bool Started;
        public bool Ended;
        public bool Fed;

    }

    // CLASSIC: a queued round belongs to one admitted game, never whichever game replaced it.
    private sealed record SendNextRound(Session Session, int Round);

    private Session _session;
    private bool _closing; // CLASSIC: queued joins cannot recreate a game after graceful shutdown begins.
    private sealed record PendingJoin(object Token, Session Candidate, PetGameAttachContext Context);
    private PendingJoin _pendingJoin;
    public IStash Stash { get; set; }

    protected override void ConfigureReceivers() {
        Receive<object>(ShouldStashForPublication, _ => Stash.Stash());
        base.ConfigureReceivers();
    }

    // CLASSIC: preserve queued training/Morph work while the parent completes one local publication.
    // Identity and lifecycle messages must still run, so closing cannot wait on the publication result.
    internal bool ShouldStashForPublication(object message)
        => _pendingJoin is not null && message is not PetGamePublicationResult
            and not SERVICE_101_PROTOCOL.MSG_QUERYMESSAGESERVICEIDENTITY
            and not SERVICE_101_PROTOCOL.MSG_PREDISPOSE and not SERVICE_101_PROTOCOL.MSG_DISPOSE
            and not Exception and not Status.Failure and not Terminated;

    private new void SendToSocket(IMessage message) => SessionActor.ActorRef.Tell(message, Self);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PetGameService(parentActor));

    private static bool Enabled => ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsLeveling);

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEJOIN))]
    private void ReceiveJoin(PET_9_PROTOCOL.MSG_PETGAMEJOIN message) {
        if (_closing) return;
        var game = message.Game.ToString();
        var wizard = GetActiveWizard();
        if (game == MorphGame) {
            JoinMorph(wizard);

            return;
        }

        if (!Enabled || wizard is null || !PetGameConfigs.TryGet(game, out var info)) {
            Logger.Information("Pet game {0}: refused (pets off or not a 2010 game).", Logger.Args(game));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });

            return;
        }

        // CLASSIC: prepare the native metadata before any saved pet or session change.
        if (!PetGameInitializationCodec.TryPrepare(info, out var initData)) {
            Logger.Warning("Pet game {0}: refused, initialization metadata could not be prepared.", Logger.Args(game));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });
            return;
        }

        PetGameAttachContext attach = null;
        PreparedPetGameObject gameObject = null;
        if (game == PetGameObjectCodec.Dance
            && (!SessionActor.TryCapturePetGameAttach(wizard, out attach)
                || !PetGameObjectCodec.TryPrepareDance(attach.World, out gameObject)
                || !SessionActor.MatchesPetGameAttach(attach))) {
            Logger.Warning("Pet game {0}: refused, completed attachment or native Dance object could not be prepared.", Logger.Args(game));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });
            return;
        }

        var selectedPet = EquippedPet(wizard);
        WizClientObjectItem pet;
        int energy;
        // CLASSIC: initialize the fresh owned pet, and publish only an acknowledged change.
        try {
            if (!ClassicPetProgressTransactions.TryInitializeForGame(wizard, selectedPet?.m_globalID.Full ?? 0, out pet, out energy,
                attach is null ? null : () => SessionActor.MatchesPetGameAttach(attach))) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                InformGameClient("Equip a pet to play the pet games.");
                SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });
                return;
            }
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }
        var b = PetProgress.Behavior(pet);
        _ = int.TryParse(message.Track.ToString(), out var track);
        track = Math.Clamp(track, 0, Math.Max(0, (info.m_trackChoices?.Count ?? 1) - 1));
        if (b is null || b.m_level == 0) {
            Logger.Information("Pet game {0}: refused, {1} has no hatched pet equipped.", Logger.Args(game, wizard.CharId));
            InformGameClient("Equip a pet to play the pet games.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });

            return;
        }

        var cost = PetRules.EnergyCost(b.m_level);
        if (energy < cost) {
            Logger.Information("Pet game {0}: refused, energy {1} < {2}.", Logger.Args(game, energy, cost));
            InformGameClient("Your pet is too tired to play.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });

            return;
        }

        var candidate = new Session {
            Game = game,
            Track = track,
            PetId = pet.m_globalID,
            Dance = game == "PetGameDance" ? new DanceGame(Random.Shared) : null,
        };
        Logger.Information("Pet game {0} track {1}: {2} joins with pet {3} (level {4}, energy {5}).",
            Logger.Args(game, track, wizard.CharId, pet.m_globalID.Full, b.m_level, energy));
        var init = new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = game, Data = initData, MinLevel = 0, Track = (byte) track };
        if (attach is not null) {
            var token = new object();
            _pendingJoin = new(token, candidate, attach);
            SessionActor.ActorRef.Tell(new PetGamePublication(token, attach, gameObject, init), Self);
            return;
        }
        // CLASSIC: other games retain their existing admission; Dance commits after the guarded parent result.
        LeaveMorph();
        RetireTraining();
        _session = candidate;
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 1 });
        SendToSocket(init);
    }

    [MessageHandler(typeof(PetGamePublicationResult))]
    private void ReceivePublicationResult(PetGamePublicationResult message) {
        if (Sender != SessionActor.ActorRef || _pendingJoin is not { } pending
            || !ReferenceEquals(pending.Token, message.Token)) return;
        _pendingJoin = null;
        if (_closing) { Stash.ClearStash(); return; }
        if (message.Accepted) {
            LeaveMorph();
            RetireTraining();
            _session = pending.Candidate;
        }
        else if (!message.ContextValid) {
            // A changed/disposed scene also invalidates the old binding; normal shutdown retires it.
            RetireGamesForClose();
            CloseSession();
            return;
        }
        else SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = pending.Candidate.Game, Success = 0 });
        Stash.UnstashAll();
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEREADY))]
    private void ReceiveReady(PET_9_PROTOCOL.MSG_PETGAMEREADY message) {
        if (_session is null || _session.Started || _session.Ended) {
            return;
        }

        _session.Started = true;
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = _session.Game, Data = "" });
        if (_session.Dance is not null) {
            Timers.StartSingleTimer("petDanceRound", new SendNextRound(_session, _session.Dance.Round), s_roundDelay);
        }
    }

    [MessageHandler(typeof(SendNextRound))]
    private void ReceiveSendNextRound(SendNextRound message) {
        // CLASSIC: neither an old mailbox callback nor a duplicate callback may replace an outstanding round.
        if (!ReferenceEquals(_session, message.Session) || _session is not { Started: true, Ended: false }
            || _session.Dance is not { Current: null } dance || dance.Round != message.Round) return;
        var moves = dance.NextRound();
        if (moves is null) {
            return;
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = moves });
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDANCE))]
    private void ReceiveDance(PET_9_PROTOCOL.MSG_PETGAMEDANCE message) {
        var dance = _session?.Dance;
        // CLASSIC: READY must precede play, and each issued round accepts exactly one answer.
        if (dance is null || !_session.Started || _session.Ended || dance.Current is null) {
            return;
        }

        var answer = message.Moves.ToString();
        var ok = dance.Answer(answer);
        Logger.Information("Pet dance round {0}: {1} ({2} right, {3} wrong).",
            Logger.Args(dance.Round, ok ? "right" : "wrong", dance.Successes, dance.Failures));
        if (dance.IsOver) {
            Finish(dance.Points, dance.Successes);

            return;
        }

        Timers.StartSingleTimer("petDanceRound", new SendNextRound(_session, dance.Round), s_roundDelay);
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDATA))]
    private void ReceiveData(PET_9_PROTOCOL.MSG_PETGAMEDATA message) {
        var game = message.Game.ToString();
        if (_morph is not null && string.Equals(game, MorphGame, StringComparison.Ordinal)) {
            ReceiveMorphCommand(message.Data.ToString() ?? "");

            return;
        }

        // CLASSIC: the native shared reader checks the complete Game string; data cannot cross games.
        if (_session is null || !string.Equals(game, _session.Game, StringComparison.Ordinal)) {
            return;
        }

        byte[] data = message.Data;
        data ??= [];
        if (data.Length == 0) {
            return;
        }

        switch (data[0]) {
            case CommandFeedSnack when data.Length >= 9:
                FeedSnack(BitConverter.ToUInt64(data, 1));
                break;
            case CommandDebugWin or CommandDebugLose when !_session.Ended:
                var account = GetActiveAccount();
                if (account is null || account.AuthLevel < AuthLevel.QualityAssurance) {
                    Logger.Information("Pet game {0}: debug end ignored (not a QA account).", Logger.Args(_session.Game));
                    break;
                }

                Finish(data[0] == CommandDebugWin ? PetRules.FullGamePoints : 0, data[0] == CommandDebugWin ? 1 : 0);
                break;
            default:
                Logger.Debug("Pet game {0}: command {1} ({2} bytes) not handled.", Logger.Args(_session.Game, data[0], data.Length));
                break;
        }
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEENDING))]
    private void ReceiveEnding(PET_9_PROTOCOL.MSG_PETGAMEENDING message) {
        var game = message.Game.ToString();
        // CLASSIC: GlobalID semantics are not proven here; bind only the native Game identity.
        if (_morph is not null && string.Equals(game, MorphGame, StringComparison.Ordinal)) {
            LeaveMorph();
            return;
        }
        if (_session is null || !string.Equals(game, _session.Game, StringComparison.Ordinal)) return;
        Logger.Information("Pet game {0}: closed by the client ({1}).",
            Logger.Args(_session.Game, _session.Ended ? "after the end" : "quit early, no energy taken"));
        RetireTraining();
    }

    // CLASSIC: use the same retirement for client close and successful replacement, with no reward or charge.
    private void RetireTraining() {
        Timers.Cancel("petDanceRound");
        _session = null;
    }

    private void Finish(int points, int wins) {
        if (_session is null || _session.Ended) return;
        var wizard = GetActiveWizard();
        if (wizard is null || !PetGameConfigs.TryGet(_session.Game, out var info)) return;
        var track = info.m_trackChoices?.ElementAtOrDefault(_session.Track);
        var changes = (track?.m_modifications ?? []).Where(m => m is not null)
            .Select(m => new PetStatChange(m.m_name.ToString(), m.m_change)).ToList();
        try {
            // CLASSIC: the fresh pet and energy cost become visible together after one save acknowledgement.
            if (!ClassicPetProgressTransactions.TryFinish(wizard, _session.PetId, _session.Game,
                track?.m_name.ToString() ?? "", changes, points, wins, out var receipt)) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
                return;
            }
            _session.Ended = true;
            Timers.Cancel("petDanceRound");
            Logger.Information("Pet game {0} ended: {1} point(s), {2}; +{3} XP (total {4}), level {5} -> {6}; energy -{7} (now {8}).",
                Logger.Args(_session.Game, points, string.Join(", ", receipt.Applied.Select(a => $"{a.Stat} +{a.Change}")),
                    receipt.Growth.Xp, PetProgress.Behavior(receipt.Pet).m_XP, receipt.Growth.OldLevel,
                    receipt.Growth.NewLevel, receipt.Cost, wizard.PetOwnerBehavior.Energy));
            PublishProgress(receipt);
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }
    }

    private void FeedSnack(ulong snackId) {
        var wizard = GetActiveWizard();
        if (_session is null || wizard is null || !_session.Ended || _session.Fed) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());
            return;
        }
        try {
            // CLASSIC: consume the fresh saved stack and grow the fresh owned pet in the same transaction.
            if (!ClassicPetProgressTransactions.TryFeed(wizard, _session.PetId, snackId, out var receipt)) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                Logger.Information("Pet snack {0}: refused; no snack or progress was committed.", Logger.Args(snackId));
                SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());
                return;
            }
            _session.Fed = true;
            Logger.Information("Pet fed snack {0} ({1}): {2}; +{3} XP (total {4}), level {5} -> {6}.",
                Logger.Args(snackId, receipt.Taste, string.Join(", ", receipt.Applied.Select(a => $"{a.Stat} +{a.Change}")),
                    receipt.Growth.Xp, PetProgress.Behavior(receipt.Pet).m_XP, receipt.Growth.OldLevel, receipt.Growth.NewLevel));
            PublishProgress(receipt);
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }
    }

    private void PublishProgress(PetProgressReceipt receipt) {
        // CLASSIC: every native payload was prepared before the acknowledged write; retain its original order.
        foreach (var message in receipt.Messages) SendToSocket(message);
        if (receipt.Growth.LeveledUp) {
            Logger.Information("Pet {0} grew to {1}; learned {2}.", Logger.Args(receipt.Pet.m_globalID.Full,
                PetRules.LevelName(receipt.Growth.NewLevel),
                string.Join(", ", receipt.Growth.NewTalents.Select(t => PetProgress.TalentName(t) ?? t.ToString()))));
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_RESUMMONPET { PetItemId = receipt.Pet.m_globalID });
        }
    }

    internal static WizClientObjectItem EquippedPet(Wizard wizard)
        => FindPet(wizard, wizard.EquipmentBehavior.GetEquippedPetId());

    private static WizClientObjectItem FindPet(Wizard wizard, ulong id)
        => id == 0 ? null
            : wizard.EquipmentBehavior.EquippedItems?.FirstOrDefault(i => i.m_globalID == id)
              ?? wizard.InventoryBehavior.Items?.FirstOrDefault(i => i.m_globalID == id);

}
