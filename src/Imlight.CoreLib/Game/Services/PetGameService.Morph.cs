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
 * CLASSIC (2026-10-04): a player who waits alone may get an ambient
 * wizard of the zone as partner (AmbientHatching, AmbientPets' waits):
 * this service plays the partner's side (its pet, its confirm) and lets
 * it go when the egg is made, the player leaves, or the player has not
 * confirmed in AmbientPets.HoldLimit. A real player joining first wins.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
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
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
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

        /// <summary>CLASSIC (2026-10-04): the ambient wizard playing this side (no service of its own), or null.</summary>
        public AmbientWizard Ambient;

    }

    /// <summary>Two wizards hatching together (in one zone).</summary>
    private sealed class MorphLobby {

        public readonly object Gate = new();
        public readonly MorphSide[] Sides = new MorphSide[2];

    }

    /// <summary>A partner's side changed (sent between the two wizards' pet game services).</summary>
    // CLASSIC: an old partner's queued update belongs to its originating lobby and exact side association.
    private sealed record PartnerChanged(MorphLobby Lobby, MorphSide Side, ulong PetId, bool Ready, bool Left);

    private sealed record MorphEggTimer(ulong EggId);

    // CLASSIC (2026-10-04): the ambient partner's steps, run by the waiting player's service.
    private sealed record AmbientMorphJoin;
    private sealed record AmbientMorphPick;
    private sealed record AmbientMorphConfirm;
    private sealed record AmbientMorphHold;

    private static readonly ConcurrentDictionary<string, MorphLobby> s_lobbies = new(StringComparer.Ordinal);

    private (MorphLobby Lobby, int Side, string Key)? _morph;

    private void JoinMorph(Wizard wizard) {
        if (!Imlight.Classic.ClassicRuntime.Rules.IsFeatureEnabled(Imlight.Classic.ClassicFeatures.PetsHatching) || wizard is null) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MorphGame, Success = 0 });

            return;
        }

        var key = wizard.Zone ?? "";
        // CLASSIC: a repeated JOIN for this still-valid lobby replays admission without losing the offer/readiness.
        if (_morph is { } current && string.Equals(current.Key, key, StringComparison.Ordinal)) {
            lock (current.Lobby.Gate) {
                if (current.Lobby.Sides[current.Side] is { } mine && mine.Service == Self && mine.CharId == wizard.CharId
                    && (current.Lobby.Sides.All(side => side is not null)
                        || ReferenceEquals(s_lobbies.GetValueOrDefault(key), current.Lobby))) {
                    SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MorphGame, Success = 1 });
                    SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = MorphGame, Data = "", MinLevel = PetHatchRules.MinLevel, Track = (byte) current.Side });
                    SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = MorphGame, Data = "" });
                    return;
                }
            }
        }

        (MorphLobby Lobby, int Side, string Key) admitted;
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
                admitted = (lobby, free, key);
                if (lobby.Sides.All(s => s is not null)) {
                    // A full lobby moves on; the next pair starts a new one.
                    s_lobbies.TryRemove(new KeyValuePair<string, MorphLobby>(key, lobby));
                }

                break;
            }
        }

        // CLASSIC: keep the old valid game on refusal; release only its association once a new slot is secured.
        // A stale old handle can name the vacant slot just acquired. Do not release the new association as old.
        if (_morph is { } old && ReferenceEquals(old.Lobby, admitted.Lobby) && old.Side == admitted.Side) {
            _morph = null;
            CancelAmbientTimers();
        }
        else LeaveMorph();
        RetireTraining();
        _morph = admitted;
        Logger.Information("Pet hatching: {0} joins side {1} in {2}.", Logger.Args(wizard.CharId, _morph.Value.Side, key));
        if (AmbientHatching.Enabled) {
            // CLASSIC (2026-10-04): alone on the spots, an ambient wizard of the zone may come over after a moment.
            Timers.StartSingleTimer("ambientMorphJoin", new AmbientMorphJoin(),
                AmbientPets.Wait(Random.Shared, AmbientPets.JoinMinSeconds, AmbientPets.JoinMaxSeconds));
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MorphGame, Success = 1 });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = MorphGame, Data = "", MinLevel = PetHatchRules.MinLevel, Track = (byte) _morph.Value.Side });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = MorphGame, Data = "" });
    }

    private void LeaveMorph() {
        if (_morph is not { } m) {
            return;
        }

        _morph = null;
        CancelAmbientTimers();
        MorphSide partner;
        MorphSide mine;
        ulong mineId;
        lock (m.Lobby.Gate) {
            // CLASSIC: a stale lobby handle cannot remove a slot now owned by somebody else's service.
            if (m.Lobby.Sides[m.Side]?.Service != Self) return;
            mine = m.Lobby.Sides[m.Side];
            mineId = mine.CharId;
            m.Lobby.Sides[m.Side] = null;
            partner = m.Lobby.Sides[1 - m.Side];
            if (partner?.Ambient is not null) {
                m.Lobby.Sides[1 - m.Side] = null; // the ambient partner leaves with the player
            }

            if (partner is null || partner.Ambient is not null) {
                s_lobbies.TryRemove(new KeyValuePair<string, MorphLobby>(m.Key, m.Lobby));
            }
        }

        if (partner?.Ambient is { } ambient) {
            AmbientHatching.Release(ambient, mineId, AmbientHatchEnd.PlayerLeft); // nothing when it already hatched
            return;
        }

        partner?.Service?.Tell(new PartnerChanged(m.Lobby, mine, 0, false, true));
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
                partner?.Service?.Tell(new PartnerChanged(m.Lobby, me, me.PetId, false, false));
                SendAffordability(m);
                if (partner?.Ambient is not null) {
                    AmbientSawPick(m, partner);
                }

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
                partner?.Service?.Tell(new PartnerChanged(m.Lobby, me, me.PetId, me.Ready, false));
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
        if (_morph is not { } m || !ReferenceEquals(m.Lobby, message.Lobby)) {
            return;
        }

        // CLASSIC: a replacement peer in the same lobby must not receive the previous peer's queued update.
        lock (m.Lobby.Gate) {
            var currentPeer = m.Lobby.Sides[1 - m.Side];
            if (message.Side is null || (message.Left ? currentPeer is not null && !ReferenceEquals(currentPeer, message.Side)
                : !ReferenceEquals(currentPeer, message.Side))) return;
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

        MorphSide me, partner;
        HatchParent mine, theirs;
        lock (m.Lobby.Gate) {
            me = m.Lobby.Sides[m.Side];
            partner = m.Lobby.Sides[1 - m.Side];
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
            lock (m.Lobby.Gate) { me.Done = false; me.Ready = false; }
            return;
        }

        // The egg carries what the baby inherited; EnsureInitialized keeps it when the egg hatches.
        b.m_maxStats = [.. baby.MaxStats.Select(kv => new PetStat { m_name = kv.Key, m_statID = PetProgress.StatId(kv.Key), m_value = kv.Value })];
        b.m_allTalents = [.. baby.TalentPool.Select(PetProgress.TalentId)];
        // CLASSIC: fresh ownership/cooldown/capacity and payment are checked with the original egg
        // and saved slot in one commit. No timer or success packet precedes acknowledgement.
        PetHatchReceipt receipt;
        try {
            if (!ClassicPetHatchTransactions.TryCreate(wizard, me.PetId, mine, theirs, egg, now, out receipt)) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                InformGameClient("Your pets cannot hatch: the saved balance, pet or backpack refused the egg.");
                SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD { CanAfford = 0 });
                lock (m.Lobby.Gate) { me.Done = false; me.Ready = false; }
                return;
            }
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }
        egg = receipt.Egg;
        var hatchSeconds = receipt.Seconds;
        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.GameObjectID, SerializedItem = receipt.Data });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETEGGMORPHED { PetTemplateGID = egg.m_globalID, PetName = 0, HatchTime = receipt.Finish });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHINGSLOT { GlobalID = egg.m_globalID, Removed = 0, ExpireTimeCount = receipt.Finish });
        Timers.StartSingleTimer($"eggHatch_{egg.m_globalID.Full}", new MorphEggTimer(egg.m_globalID), TimeSpan.FromSeconds(hatchSeconds + 1));

        Logger.Information("Pet hatching: {0} paid {1} gold; egg {2} of template {3} hatches in {4} s; max stats {5}; pool {6}.",
            Logger.Args(wizard.CharId, cost, egg.m_globalID.Full, baby.TemplateId, hatchSeconds,
                string.Join(" ", baby.MaxStats.Select(kv => $"{kv.Key}={kv.Value}")), string.Join(", ", baby.TalentPool)));

        if (partner.Ambient is { } ambient) {
            // CLASSIC (2026-10-04): the ambient partner hatched too (its egg is off screen); its pet rests 24 hours.
            CancelAmbientTimers();
            lock (m.Lobby.Gate) {
                partner.Done = true;
            }

            AmbientHatching.Release(ambient, wizard.CharId, AmbientHatchEnd.Hatched);
        }
    }

    // ---- CLASSIC (2026-10-04): an ambient wizard as the partner -------------------------------------

    private void CancelAmbientTimers() {
        foreach (var key in new[] { "ambientMorphJoin", "ambientMorphPick", "ambientMorphConfirm", "ambientMorphHold" }) {
            Timers.Cancel(key);
        }
    }

    /// <summary>Still alone on the spots: a free ambient wizard of the zone takes the other one.</summary>
    [MessageHandler(typeof(AmbientMorphJoin))]
    private void ReceiveAmbientMorphJoin(AmbientMorphJoin message) {
        if (_morph is not { } m || GetActiveWizard() is not { } wizard) {
            return;
        }

        lock (m.Lobby.Gate) {
            if (m.Lobby.Sides[1 - m.Side] is not null) {
                return; // a real player came
            }
        }

        var ambient = AmbientHatching.Claim(m.Key, wizard.CharId, DateTime.UtcNow);
        if (ambient is null) {
            Logger.Information("Pet hatching: no ambient wizard free to hatch with {0} in {1}.", Logger.Args(wizard.CharId, m.Key));
            return;
        }

        lock (m.Lobby.Gate) {
            if (m.Lobby.Sides[1 - m.Side] is not null || !ReferenceEquals(_morph?.Lobby, m.Lobby)) {
                ambient = null;
            }
            else {
                m.Lobby.Sides[1 - m.Side] = new MorphSide { CharId = ambient.CharId, Ambient = ambient };
                s_lobbies.TryRemove(new KeyValuePair<string, MorphLobby>(m.Key, m.Lobby)); // full: the next pair starts a new one
            }
        }

        if (ambient is null) {
            return;
        }

        Timers.StartSingleTimer("ambientMorphPick", new AmbientMorphPick(),
            AmbientPets.Wait(Random.Shared, AmbientPets.PickMinSeconds, AmbientPets.PickMaxSeconds));
        Timers.StartSingleTimer("ambientMorphHold", new AmbientMorphHold(), AmbientPets.HoldLimit);
    }

    /// <summary>The ambient partner shows its pet.</summary>
    [MessageHandler(typeof(AmbientMorphPick))]
    private void ReceiveAmbientMorphPick(AmbientMorphPick message) {
        if (_morph is not { } m) {
            return;
        }

        MorphSide partner, me;
        lock (m.Lobby.Gate) {
            partner = m.Lobby.Sides[1 - m.Side];
            me = m.Lobby.Sides[m.Side];
        }

        if (partner?.Ambient is not { } ambient) {
            return;
        }

        var parent = AmbientHatching.ParentOf(ambient);
        var pet = AmbientHatching.PetOf(ambient);
        if (parent is null || pet is null) {
            Logger.Warning("Pet hatching: ambient wizard {0} has no pet to hatch with.", Logger.Args(ambient.Name));
            DropAmbientPartner(m, AmbientHatchEnd.TimedOut);
            return;
        }

        lock (m.Lobby.Gate) {
            partner.PetId = pet.m_globalID;
            partner.Parent = parent;
            partner.Ready = false;
        }

        Logger.Information("Pet hatching: ambient wizard {0} offers {1} (level {2}, pedigree {3}).",
            Logger.Args(ambient.Name, parent.TemplateId, parent.Level, parent.Pedigree));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHSET { PetID = partner.PetId });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = 0 });
        SendAffordability(m);
        if (me?.Parent is not null) {
            AmbientSawPick(m, partner);
        }
    }

    /// <summary>The player picked (or changed) a pet: the ambient partner un-confirms and thinks it over.</summary>
    private void AmbientSawPick((MorphLobby Lobby, int Side, string Key) m, MorphSide partner) {
        if (partner.Parent is null) {
            return; // it has not shown its own pet yet; it looks at the player's when it does
        }

        var wasReady = false;
        lock (m.Lobby.Gate) {
            wasReady = partner.Ready;
            partner.Ready = false;
        }

        if (wasReady) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = 0 });
        }

        Timers.StartSingleTimer("ambientMorphConfirm", new AmbientMorphConfirm(),
            AmbientPets.Wait(Random.Shared, AmbientPets.ConfirmMinSeconds, AmbientPets.ConfirmMaxSeconds));
    }

    /// <summary>The ambient partner confirms; with the player confirmed too, the egg is made.</summary>
    [MessageHandler(typeof(AmbientMorphConfirm))]
    private void ReceiveAmbientMorphConfirm(AmbientMorphConfirm message) {
        if (_morph is not { } m) {
            return;
        }

        MorphSide partner, me;
        bool both;
        lock (m.Lobby.Gate) {
            partner = m.Lobby.Sides[1 - m.Side];
            me = m.Lobby.Sides[m.Side];
            if (partner?.Ambient is null || partner.Parent is null || me?.Parent is null || partner.Done) {
                return;
            }

            partner.Ready = true;
            both = me.Ready;
        }

        Logger.Information("Pet hatching: ambient wizard {0} confirms.", Logger.Args(partner.Ambient.Name));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = 1 });
        if (both) {
            TryMakeEgg();
        }
    }

    /// <summary>The player has not hatched in time: the ambient partner goes, freeing the spot.</summary>
    [MessageHandler(typeof(AmbientMorphHold))]
    private void ReceiveAmbientMorphHold(AmbientMorphHold message) {
        if (_morph is { } m) {
            DropAmbientPartner(m, AmbientHatchEnd.TimedOut);
        }
    }

    private void DropAmbientPartner((MorphLobby Lobby, int Side, string Key) m, AmbientHatchEnd end) {
        MorphSide partner;
        ulong mine;
        lock (m.Lobby.Gate) {
            partner = m.Lobby.Sides[1 - m.Side];
            if (partner?.Ambient is null || partner.Done) {
                return;
            }

            m.Lobby.Sides[1 - m.Side] = null;
            mine = m.Lobby.Sides[m.Side]?.CharId ?? 0;
            s_lobbies.TryAdd(m.Key, m.Lobby); // the player waits again; a real player may now join
        }

        CancelAmbientTimers();
        Logger.Information("Pet hatching: ambient wizard {0} leaves the hatching spot ({1}).", Logger.Args(partner.Ambient.Name, end));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHSET { PetID = 0 });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = 0 });
        AmbientHatching.Release(partner.Ambient, mine, end);
    }

    protected override void OnPreDispose() {
        RetireGamesForClose();
        base.OnPreDispose();
    }

    // CLASSIC: also retire when disposal reaches this service without a completed pre-dispose.
    protected override void OnDispose() {
        RetireGamesForClose();
        base.OnDispose();
    }

    private void RetireGamesForClose() {
        _closing = true;
        _pendingJoin = null;
        Stash?.ClearStash(); // CLASSIC: close bypasses publication staging and discards work that could reopen it.
        RetireTraining();
        LeaveMorph();
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
