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

internal sealed class PetGameService(SessionActor sessionActor) : MessageService(sessionActor) {

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

    private sealed class SendNextRound {

        public static readonly SendNextRound Instance = new();

    }

    private Session _session;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PetGameService(parentActor));

    private static readonly ObjectSerializer s_serializer = new(Behaviors: SerializerFlags.None);
    private static readonly CoreObjectSerializer s_itemSerializer = new(behaviors: SerializerFlags.None);

    private static bool Enabled => ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsLeveling);

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEJOIN))]
    private void ReceiveJoin(PET_9_PROTOCOL.MSG_PETGAMEJOIN message) {
        var game = message.Game.ToString();
        var wizard = GetActiveWizard();
        if (!Enabled || wizard is null || !PetGameConfigs.TryGet(game, out var info)) {
            Logger.Information("Pet game {0}: refused (pets off or not a 2010 game).", Logger.Args(game));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });

            return;
        }

        var pet = EquippedPet(wizard);
        var b = PetProgress.Behavior(pet);
        _ = int.TryParse(message.Track.ToString(), out var track);
        track = Math.Clamp(track, 0, Math.Max(0, (info.m_trackChoices?.Count ?? 1) - 1));
        if (b is null || b.m_level == 0) {
            Logger.Information("Pet game {0}: refused, {1} has no hatched pet equipped.", Logger.Args(game, wizard.CharId));
            InformGameClient("Equip a pet to play the pet games.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });

            return;
        }

        if (PetProgress.EnsureInitialized(pet)) {
            WizardItemCollection.SavePetGrowth(pet);
        }

        var cost = PetRules.EnergyCost(b.m_level);
        if (wizard.PetOwnerBehavior.Energy < cost) {
            Logger.Information("Pet game {0}: refused, energy {1} < {2}.", Logger.Args(game, wizard.PetOwnerBehavior.Energy, cost));
            InformGameClient("Your pet is too tired to play.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });

            return;
        }

        _session = new Session {
            Game = game,
            Track = track,
            PetId = pet.m_globalID,
            Dance = game == "PetGameDance" ? new DanceGame(Random.Shared) : null,
        };
        Logger.Information("Pet game {0} track {1}: {2} joins with pet {3} (level {4}, energy {5}).",
            Logger.Args(game, track, wizard.CharId, pet.m_globalID.Full, b.m_level, wizard.PetOwnerBehavior.Energy));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 1 });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = game, Data = "", MinLevel = 0, Track = (byte) track });
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEREADY))]
    private void ReceiveReady(PET_9_PROTOCOL.MSG_PETGAMEREADY message) {
        if (_session is null || _session.Started) {
            return;
        }

        _session.Started = true;
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = _session.Game, Data = "" });
        if (_session.Dance is not null) {
            Timers.StartSingleTimer("petDanceRound", SendNextRound.Instance, s_roundDelay);
        }
    }

    [MessageHandler(typeof(SendNextRound))]
    private void ReceiveSendNextRound(SendNextRound message) {
        var moves = _session?.Dance?.NextRound();
        if (moves is null) {
            return;
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = moves });
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDANCE))]
    private void ReceiveDance(PET_9_PROTOCOL.MSG_PETGAMEDANCE message) {
        var dance = _session?.Dance;
        if (dance is null || _session.Ended) {
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

        Timers.StartSingleTimer("petDanceRound", SendNextRound.Instance, s_roundDelay);
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDATA))]
    private void ReceiveData(PET_9_PROTOCOL.MSG_PETGAMEDATA message) {
        if (_session is null) {
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
                if (GetActiveAccount()?.AuthLevel < AuthLevel.QualityAssurance) {
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
        if (_session is not null) {
            Logger.Information("Pet game {0}: closed by the client ({1}).",
                Logger.Args(_session.Game, _session.Ended ? "after the end" : "quit early, no energy taken"));
        }

        Timers.Cancel("petDanceRound");
        _session = null;
    }

    private void Finish(int points, int wins) {
        var wizard = GetActiveWizard();
        var pet = wizard is null ? null : FindPet(wizard, _session.PetId);
        var b = PetProgress.Behavior(pet);
        if (b is null || !PetGameConfigs.TryGet(_session.Game, out var info)) {
            return;
        }

        _session.Ended = true;
        var cost = PetRules.EnergyCost(b.m_level);
        wizard.UpdateEnergy(Math.Max(0, wizard.PetOwnerBehavior.Energy - cost));
        SendEnergy(wizard);

        var track = info.m_trackChoices?.ElementAtOrDefault(_session.Track);
        var trackChanges = (track?.m_modifications ?? []).Where(m => m is not null)
            .Select(m => new PetStatChange(m.m_name.ToString(), m.m_change)).ToList();
        var applied = PetProgress.ApplyStats(pet, PetRules.DistributePoints(points, trackChanges));
        var growth = PetProgress.AddXp(pet, PetRules.GameXp(points, b.m_level), Random.Shared);
        WizardItemCollection.SavePetGrowth(pet);

        Logger.Information("Pet game {0} ended: {1} point(s), {2}; +{3} XP (total {4}), level {5} -> {6}; energy -{7} (now {8}).",
            Logger.Args(_session.Game, points, string.Join(", ", applied.Select(a => $"{a.Stat} +{a.Change}")), growth.Xp, b.m_XP,
                growth.OldLevel, growth.NewLevel, cost, wizard.PetOwnerBehavior.Energy));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEEND {
            Game = _session.Game,
            Data = Serialize(EndData(wins, track?.m_name.ToString() ?? "", trackChanges, applied, growth.Xp, wins)),
        });
        AfterGrowth(wizard, pet, growth);
    }

    private void FeedSnack(ulong snackId) {
        var wizard = GetActiveWizard();
        var pet = wizard is null ? null : FindPet(wizard, _session.PetId);
        var snack = wizard?.PetSnackBehavior.Snacks?.FirstOrDefault(s => s.m_globalID == snackId);
        if (pet is null || snack is null || !_session.Ended || _session.Fed
            || CoreObjectFactory.GetCoreTemplate(snack.m_templateID) is not PetSnackItemTemplate template) {
            Logger.Information("Pet snack {0}: refused (pet {1}, snack {2}, game ended {3}, fed {4}; bag: {5}).",
                Logger.Args(snackId, pet is not null, snack is not null, _session.Ended, _session.Fed,
                    string.Join(",", wizard?.PetSnackBehavior.Snacks?.Select(s => $"{s.m_globalID.Full}:{s.m_templateID.Full}x{s.m_quantity}") ?? [])));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());

            return;
        }

        _session.Fed = true;
        wizard.RemoveSnack(snackId, out var updated);
        if (updated is not null && updated.m_quantity > 0) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = wizard.GameObjectID, ItemID = updated.m_globalID, Quantity = updated.m_quantity });
        }
        else {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKREMOVE { GlobalID = wizard.GameObjectID, ItemID = snackId });
        }

        var snackChanges = (template.m_statModifierSet?.m_modifications ?? []).Where(m => m is not null)
            .Select(m => new PetStatChange(m.m_name.ToString(), m.m_change)).ToList();
        var taste = PetRules.Taste(PetProgress.FavouriteSnackKinds((uint) pet.m_templateID), PetProgress.School((uint) pet.m_templateID),
            template.m_adjectiveList?.Select(a => a.ToString()) ?? [], template.m_school.ToString());
        var fed = PetRules.Feed(snackChanges, taste);
        var applied = PetProgress.ApplyStats(pet, fed.Changes);
        var growth = PetProgress.AddXp(pet, fed.Xp, Random.Shared);
        WizardItemCollection.SavePetGrowth(pet);

        Logger.Information("Pet fed snack {0} ({1}): {2}; +{3} XP (total {4}), level {5} -> {6}.",
            Logger.Args(snack.m_templateID.Full, taste, string.Join(", ", applied.Select(a => $"{a.Stat} +{a.Change}")), growth.Xp,
                PetProgress.Behavior(pet).m_XP, growth.OldLevel, growth.NewLevel));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDSUCCESS {
            Data = Serialize(EndData((int) taste, "", fed.Changes, applied, growth.Xp, 0)),
        });
        AfterGrowth(wizard, pet, growth);
    }

    private void AfterGrowth(Wizard wizard, WizClientObjectItem pet, PetGrowth growth) {
        var b = PetProgress.Behavior(pet);
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_GAINPETXP { PetGID = pet.m_globalID, XP = (uint) Math.Max(0, growth.Xp) });
        if (growth.LeveledUp) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETLEVELUP {
                GlobalID = pet.m_globalID,
                OverallRating = (byte) Math.Min(255u, b.m_overallRating),
                ActiveRating = (byte) Math.Min(255u, b.m_activeRating),
                PetLevel = b.m_level,
                NewTalent = growth.NewTalents.LastOrDefault(),
                NewDerbyPower = 0,
                NewJewel = 0,
                Display = 1,
            });
            Logger.Information("Pet {0} grew to {1}; learned {2}.", Logger.Args(pet.m_globalID.Full, PetRules.LevelName(growth.NewLevel),
                string.Join(", ", growth.NewTalents.Select(t => PetProgress.TalentName(t) ?? t.ToString()))));
        }

        RefreshEquippedPet(wizard, pet);
        if (growth.LeveledUp) {
            // The summoned pet carries its level and ratings in its name plate; summon it again with the new ones.
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_RESUMMONPET { PetItemId = pet.m_globalID });
        }
    }

    private static PetGameEndData EndData(int score, string setName, IEnumerable<PetStatChange> asked, IReadOnlyList<PetStatChange> applied,
            int xp, int wins) {
        var actual = applied.ToDictionary(a => a.Stat, a => a.Change, StringComparer.OrdinalIgnoreCase);
        var mods = asked.Where(c => c.Change != 0).Select(c => new PetStatModification {
            m_name = c.Stat,
            m_change = c.Change,
            m_actualChange = (uint) Math.Max(0, actual.GetValueOrDefault(c.Stat)),
        }).ToList();

        return new PetGameEndData {
            m_Score = score,
            m_statMods = new PetStatModificationSet { m_name = setName, m_modifications = mods, m_scene = "", m_gameScoreFactor = [] },
            m_xpGain = (uint) Math.Max(0, xp),
            m_wins = (uint) Math.Max(0, wins),
        };
    }

    private static ByteString Serialize(PropertyClass value) {
        if (!s_serializer.Serialize(value, (PropertyFlags) 5, out var blob)) {
            Logger.Error("Failed to serialize {0} for a pet game message.", Logger.Args(value.GetType().Name));

            return string.Empty;
        }

        return blob;
    }

    private void SendEnergy(Wizard wizard) {
        var max = Shared.Character.MagicLevelsConfig.GetPlayerLevelInfo(wizard.MagicSchoolBehavior.MagicSchool, wizard.MagicSchoolBehavior.Level).m_petEnergy;
        SendToSocket(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
            GlobalID = wizard.GameObjectID,
            Energy = wizard.PetOwnerBehavior.Energy,
            MaxEnergy = max,
            TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch,
        });
    }

    private void RefreshEquippedPet(Wizard wizard, WizClientObjectItem pet) {
        if (CoreObjectFactory.GetCoreTemplate(pet.m_templateID) is not WizItemTemplate template || ItemHelper.GetItemSlot(template) is not { } slot) {
            return;
        }

        if (!s_itemSerializer.Serialize(pet, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
            Logger.Error("Failed to serialize pet {0} for its refresh.", Logger.Args(pet.m_globalID));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM {
            GlobalID = wizard.GameObjectID,
            SlotName = slot.SlotType.ToString(),
            IsValid = 1,
            SerializedItem = data,
        });
    }

    internal static WizClientObjectItem EquippedPet(Wizard wizard)
        => FindPet(wizard, wizard.EquipmentBehavior.GetEquippedPetId());

    private static WizClientObjectItem FindPet(Wizard wizard, ulong id)
        => id == 0 ? null
            : wizard.EquipmentBehavior.EquippedItems?.FirstOrDefault(i => i.m_globalID == id)
              ?? wizard.InventoryBehavior.Items?.FirstOrDefault(i => i.m_globalID == id);

}
