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
 * CLASSIC PET HATCHING (PET MORPH)
 * ========================================================================
 *
 * PURPOSE:
 * Two wizards hatch their pets together at the Pet Hatchery: each picks a
 * pet, both confirm, each pays the gold and gets an egg that hatches into a
 * baby mixing both parents (PetHatchRules: Adult or older, once every 24
 * hours a pet, 20,000 to 50,000 gold).
 *
 * USAGE EXAMPLE:
 * Client: MSG_PETGAMEJOIN(Game "PetGameMorph"); MSG_PETGAMEDATA(Game "PetGameMorph",
 * Data "set:side=N;id=PETGID") and Data "ready:confirmed=1". Server: MSG_PETMORPHSET to the
 * partner, MSG_PETMORPHCANAFFORD, MSG_PETMORPHREADY, then MSG_PETEGGMORPHED and
 * MSG_PETMORPHINGSLOT with the egg (a level-0 pet item in the backpack).
 *
 * NOTE:
 * The text commands are the official client's PetGameMorph GUI's (r806919:
 * "set:side=%d;id=%s", "ready:confirmed=%d"). How the client enters the morph
 * game from the Hatchery's two-player sigils (PetMorphSigil01/02, the
 * PetGameMorph phantom zone) is not wired: wizards are paired here by
 * joining PetGameMorph in the same zone, two at a time.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed partial class PetGameService {

    private const string MorphGame = "PetGameMorph";

    /// <summary>One side of a hatching: who, which pet, and whether they confirmed.</summary>
    private sealed class MorphSide {

        public IActorRef Service;
        public ulong CharId;
        public ulong PetId;
        public HatchParent Parent;
        public bool Ready;
        public bool Done;

    }

    /// <summary>Two wizards hatching together (in one zone).</summary>
    private sealed class MorphLobby {

        public readonly object Gate = new();
        public readonly MorphSide[] Sides = new MorphSide[2];

    }

    /// <summary>A partner's side changed (sent between the two wizards' pet game services).</summary>
    private sealed record PartnerChanged(ulong PetId, bool Ready, bool Left);

    private sealed record MorphEggTimer(ulong EggId);

    private static readonly ConcurrentDictionary<string, MorphLobby> s_lobbies = new(StringComparer.Ordinal);

    private (MorphLobby Lobby, int Side, string Key)? _morph;

    private void JoinMorph(Wizard wizard) {
        if (!Imlight.Classic.ClassicRuntime.Rules.IsFeatureEnabled(Imlight.Classic.ClassicFeatures.PetsHatching) || wizard is null) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MorphGame, Success = 0 });

            return;
        }

        LeaveMorph();
        var key = wizard.Zone ?? "";
        while (true) {
            var lobby = s_lobbies.GetOrAdd(key, _ => new MorphLobby());
            lock (lobby.Gate) {
                if (!ReferenceEquals(s_lobbies.GetValueOrDefault(key), lobby)) {
                    continue;
                }

                var free = Array.FindIndex(lobby.Sides, s => s is null);
                if (free < 0) {
                    InformGameClient("Both hatching spots are taken.");
                    SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MorphGame, Success = 0 });

                    return;
                }

                lobby.Sides[free] = new MorphSide { Service = Self, CharId = wizard.CharId };
                _morph = (lobby, free, key);
                if (lobby.Sides.All(s => s is not null)) {
                    // A full lobby moves on; the next pair starts a new one.
                    s_lobbies.TryRemove(new KeyValuePair<string, MorphLobby>(key, lobby));
                }

                break;
            }
        }

        Logger.Information("Pet hatching: {0} joins side {1} in {2}.", Logger.Args(wizard.CharId, _morph.Value.Side, key));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MorphGame, Success = 1 });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = MorphGame, Data = "", MinLevel = PetHatchRules.MinLevel, Track = (byte) _morph.Value.Side });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = MorphGame, Data = "" });
    }

    private void LeaveMorph() {
        if (_morph is not { } m) {
            return;
        }

        _morph = null;
        MorphSide partner;
        lock (m.Lobby.Gate) {
            m.Lobby.Sides[m.Side] = null;
            partner = m.Lobby.Sides[1 - m.Side];
            if (partner is null) {
                s_lobbies.TryRemove(new KeyValuePair<string, MorphLobby>(m.Key, m.Lobby));
            }
        }

        partner?.Service.Tell(new PartnerChanged(0, false, true));
    }

    private void ReceiveMorphCommand(string text) {
        if (_morph is not { } m) {
            return;
        }

        var (verb, args) = ParseCommand(text);
        var wizard = GetActiveWizard();
        MorphSide me, partner;
        switch (verb) {
            case "set": {
                var pet = FindPet(wizard, ulong.TryParse(args.GetValueOrDefault("id"), out var id) ? id : 0);
                var parent = ParentOf(wizard, pet);
                lock (m.Lobby.Gate) {
                    me = m.Lobby.Sides[m.Side];
                    partner = m.Lobby.Sides[1 - m.Side];
                    me.PetId = pet?.m_globalID ?? 0;
                    me.Parent = parent;
                    me.Ready = false;
                }

                Logger.Information("Pet hatching: {0} offers pet {1} (level {2}).", Logger.Args(wizard.CharId, me.PetId, parent?.Level ?? 0));
                partner?.Service.Tell(new PartnerChanged(me.PetId, false, false));
                SendAffordability(m);
                break;
            }

            case "ready": {
                var confirmed = args.GetValueOrDefault("confirmed") == "1";
                bool both;
                lock (m.Lobby.Gate) {
                    me = m.Lobby.Sides[m.Side];
                    partner = m.Lobby.Sides[1 - m.Side];
                    me.Ready = confirmed && me.Parent is not null;
                    both = me.Ready && partner is { Ready: true };
                }

                // The partner's service makes the partner's egg when it sees both sides confirmed.
                partner?.Service.Tell(new PartnerChanged(me.PetId, me.Ready, false));
                if (both) {
                    TryMakeEgg();
                }

                break;
            }

            default:
                Logger.Debug("Pet hatching: command '{0}' not handled.", Logger.Args(text));
                break;
        }
    }

    [MessageHandler(typeof(PartnerChanged))]
    private void ReceivePartnerChanged(PartnerChanged message) {
        if (_morph is not { } m) {
            return;
        }

        if (message.Left) {
            Logger.Information("Pet hatching: the partner left.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHSET { PetID = 0 });
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = 0 });

            return;
        }

        MorphSide partner;
        bool both;
        lock (m.Lobby.Gate) {
            partner = m.Lobby.Sides[1 - m.Side];
            both = m.Lobby.Sides.All(s => s is { Ready: true });
        }

        if (partner is not null && message.PetId == partner.PetId) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHSET { PetID = message.PetId });
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = (sbyte) (message.Ready ? 1 : 0) });
            SendAffordability(m);
        }

        if (both) {
            TryMakeEgg();
        }
    }

    private void SendAffordability(( MorphLobby Lobby, int Side, string Key) m) {
        HatchParent mine, theirs;
        lock (m.Lobby.Gate) {
            mine = m.Lobby.Sides[m.Side]?.Parent;
            theirs = m.Lobby.Sides[1 - m.Side]?.Parent;
        }

        if (mine is null || theirs is null) {
            return;
        }

        var cost = PetHatchRules.GoldCost(mine.Pedigree + theirs.Pedigree);
        var wizard = GetActiveWizard();
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD { CanAfford = (sbyte) (wizard.GameStats.m_currentGold >= cost ? 1 : 0) });
    }

    /// <summary>When both sides confirmed: this wizard pays and gets an egg (once).</summary>
    private void TryMakeEgg() {
        if (_morph is not { } m) {
            return;
        }

        MorphSide me;
        HatchParent mine, theirs;
        lock (m.Lobby.Gate) {
            me = m.Lobby.Sides[m.Side];
            var partner = m.Lobby.Sides[1 - m.Side];
            if (me is null || partner is null || !me.Ready || !partner.Ready || me.Done) {
                return;
            }

            me.Done = true;
            (mine, theirs) = (me.Parent, partner.Parent);
        }

        var wizard = GetActiveWizard();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var refusal = PetHatchRules.Check(mine, theirs, now);
        var cost = PetHatchRules.GoldCost(mine.Pedigree + theirs.Pedigree);
        if (refusal is null && wizard.GameStats.m_currentGold < cost) {
            refusal = $"hatching costs {cost} gold";
        }

        if (refusal is not null) {
            Logger.Information("Pet hatching: {0} refused: {1}.", Logger.Args(wizard.CharId, refusal));
            InformGameClient($"Your pets cannot hatch: {refusal}.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD { CanAfford = 0 });
            lock (m.Lobby.Gate) {
                me.Done = false;
                me.Ready = false;
            }

            return;
        }

        var baby = PetHatchRules.Breed(mine, theirs, Random.Shared);
        var egg = PetFactory.CreatePet(wizard.CharId, (uint) baby.TemplateId, preHatch: false);
        var b = PetProgress.Behavior(egg);
        if (egg is null || b is null) {
            Logger.Error("Pet hatching: no egg for template {0}.", Logger.Args(baby.TemplateId));

            return;
        }

        // The egg carries what the baby inherited; EnsureInitialized keeps it when the egg hatches.
        b.m_maxStats = [.. baby.MaxStats.Select(kv => new PetStat { m_name = kv.Key, m_statID = PetProgress.StatId(kv.Key), m_value = kv.Value })];
        b.m_allTalents = [.. baby.TalentPool.Select(PetProgress.TalentId)];
        // CLASSIC: checked and spent in one save; a balance spent elsewhere since the check above refuses the hatch.
        if (!wizard.RemoveGold(cost)) {
            InformGameClient($"Your pets cannot hatch: hatching costs {cost} gold.");
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD { CanAfford = 0 });
            lock (m.Lobby.Gate) {
                me.Done = false;
                me.Ready = false;
            }

            return;
        }

        wizard.PetOwnerBehavior.PetHatchTimes ??= [];
        wizard.PetOwnerBehavior.PetHatchTimes[me.PetId] = now;
        wizard.AddPetToInventory(egg);
        var hatchSeconds = (uint) Math.Max(0, (long) b.m_hatchedTimeSecs - now);
        var slot = wizard.PetOwnerBehavior.CreatePetEgg(baby.TemplateId, hatchSeconds, egg.m_globalID);
        WizardData.Collections.WizardCollection.UpdateCharacterPetOwnerBehavior(wizard);

        if (s_itemSerializer.Serialize(egg, (PropertyFlags) 24, out var eggData)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.GameObjectID, SerializedItem = eggData });
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETEGGMORPHED { PetTemplateGID = egg.m_globalID, PetName = 0, HatchTime = (uint) slot.m_timeFinished });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHINGSLOT { GlobalID = egg.m_globalID, Removed = 0, ExpireTimeCount = (uint) slot.m_timeFinished });
        Timers.StartSingleTimer($"eggHatch_{egg.m_globalID.Full}", new MorphEggTimer(egg.m_globalID), TimeSpan.FromSeconds(hatchSeconds + 1));

        Logger.Information("Pet hatching: {0} paid {1} gold; egg {2} of template {3} hatches in {4} s; max stats {5}; pool {6}.",
            Logger.Args(wizard.CharId, cost, egg.m_globalID.Full, baby.TemplateId, hatchSeconds,
                string.Join(" ", baby.MaxStats.Select(kv => $"{kv.Key}={kv.Value}")), string.Join(", ", baby.TalentPool)));
    }

    protected override void OnPreDispose() {
        LeaveMorph();
        base.OnPreDispose();
    }

    [MessageHandler(typeof(MorphEggTimer))]
    private void ReceiveMorphEggTimer(MorphEggTimer message)
        => TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_DOEGGHATCH { EggGlobalId = message.EggId });

    private static HatchParent ParentOf(Wizard wizard, WizClientObjectItem pet) {
        var b = PetProgress.Behavior(pet);
        if (b is null || b.m_level == 0) {
            return null;
        }

        PetProgress.EnsureInitialized(pet);
        var last = wizard.PetOwnerBehavior.PetHatchTimes?.GetValueOrDefault(pet.m_globalID) ?? 0;
        return new HatchParent(pet.m_templateID.Full, b.m_level, PetProgress.Stats(b.m_maxStats),
            [.. (b.m_allTalents ?? []).Select(PetProgress.TalentName).Where(n => n is not null)],
            [.. (b.m_expressedTalents ?? []).Select(PetProgress.TalentName).Where(n => n is not null)],
            (int) b.m_overallRating, last);
    }

    /// <summary>"verb:key=value;key=value" (the client's text pet game commands).</summary>
    private static (string Verb, Dictionary<string, string> Args) ParseCommand(string text) {
        var colon = text.IndexOf(':');
        var verb = colon < 0 ? text : text[..colon];
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (colon >= 0) {
            foreach (var pair in text[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                var eq = pair.IndexOf('=');
                if (eq > 0) {
                    args[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
                }
            }
        }

        return (verb.Trim(), args);
    }

}
