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
 * AMBIENT WIZARDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: server-side wizards that make the world feel lived in (owner,
 * 2026-10-01). They have no account and no client connection. Each one is
 * a small endpoint actor (AmbientEndpoint) that zones treat as a player,
 * driven by one AmbientZone actor per zone (batched movement ticks, chat,
 * friends, help offers, fights). This class is the shared registry the
 * rest of the server asks: is this actor or character id an ambient
 * wizard, what is its Wizard, may it join this duel.
 *
 * Off ([Classic] AmbientWizards = off) means nothing is registered and
 * every check here answers "no" at the cost of an empty-dictionary test.
 *
 * Rules the hooks enforce (owner): an ambient wizard never takes a duel
 * slot a real player needs, never joins PvP unless a player seats it
 * through the sparring hook, joins a real player's duel only after that
 * player said yes, and does not count as a real wizard for the
 * "four wizards go first" rule.
 *
 * USAGE EXAMPLE:
 * if (AmbientWizards.IsAmbient(playerActor) && !AmbientWizards.MayJoin(playerActor, sigilId)) return;
 * if (AmbientWizards.TryGetWizard(charId, out var wizard)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.Bit;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>What an ambient wizard is doing.</summary>
// CLASSIC (2026-10-04): Hatching = on a Pet Pavilion hatching spot with a player (AmbientHatching).
internal enum AmbientActivity { Arriving, Idle, Walking, Shopping, Following, Helping, Fighting, Sparring, Away, Hatching }

/// <summary>
/// One ambient wizard: its stored record, its Wizard (never saved), its endpoint actor and its live state. Only its
/// AmbientZone actor changes the state; other threads read the Wizard and the ids.
/// </summary>
internal sealed class AmbientWizard {

    public AmbientWizard(AmbientWizardRecord record, Wizard wizard) {
        Record = record;
        Identity = record.ToIdentity();
        Wizard = wizard;
    }

    public AmbientWizardRecord Record { get; }
    public AmbientIdentity Identity { get; }
    public Wizard Wizard { get; }
    public ulong CharId => Record.CharId;
    public string Name => Wizard.PlayerNameBehavior.GetWizardName();
    public IActorRef Endpoint { get; set; }

    /// <summary>CLASSIC (2026-10-04): the AmbientZone actor that drives this wizard (hatch news goes there).</summary>
    public IActorRef Group { get; set; }

    // Live state, owned by the AmbientZone actor.
    public string Zone { get; set; }
    public string ZoneDisplayName { get; set; } = "";
    public IActorRef ZoneActor { get; set; }
    public bool Present { get; set; }
    public AmbientActivity Activity { get; set; } = AmbientActivity.Arriving;
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float? ArriveYaw { get; set; }
    public Vector3? Target { get; set; }

    /// <summary>CLASSIC (2026-10-03): the turns still ahead on the current walk (NavGrid), Target being the next.</summary>
    public Queue<Vector3> Route { get; } = new();

    /// <summary>
    /// CLASSIC (2026-10-04): the straight run under way (to Target), or null between runs. The client was sent its far
    /// end once; <see cref="LegId"/> tells the leg's end timer from a later leg's.
    /// </summary>
    public WalkLeg? Leg { get; set; }
    public int LegId { get; set; }

    /// <summary>A short stop at a corner of the walk, until then.</summary>
    public DateTime PauseUntil { get; set; }
    public DateTime Until { get; set; }
    public bool Moving { get; set; }
    public ulong DuelSigil { get; set; }
    public ulong FollowCharId { get; set; }
    public int Turn { get; set; }
    public HelpOffers Offers { get; } = new();
    public AmbientChatLimiter Limiter { get; } = new();
    public DateTime NextIdleLine { get; set; }
    public DateTime NextLook { get; set; }

    /// <summary>CLASSIC (2026-10-04): the after-duel protection, as a player's (translucent until it walks off, then 6 s).</summary>
    public Imlight.Classic.Quests.PostCombatGrace Grace { get; } = new();

    public FriendMemory FriendOf(ulong charId)
        => Record.Friends.FirstOrDefault(f => f.CharId == charId)?.ToMemory();

}

/// <summary>
/// The registry of live ambient wizards (see the file header).
/// </summary>
internal static class AmbientWizards {

    private static readonly ConcurrentDictionary<ulong, AmbientWizard> s_byChar = new();
    private static readonly ConcurrentDictionary<IActorRef, AmbientWizard> s_byActor = new();
    private static readonly ConcurrentDictionary<IActorRef, ulong> s_joinPermits = new();
    private static readonly ConcurrentDictionary<IActorRef, ulong> s_sparring = new();

    private static readonly ConcurrentDictionary<IActorRef, IActorRef> s_groups = new();

    /// <summary>The AmbientZone actor driving the ambient wizards of a zone actor.</summary>
    internal static void SetGroup(IActorRef zone, IActorRef group) => s_groups[zone] = group;

    /// <summary>
    /// A duel in <paramref name="zone"/> started, changed or ended: its zone's ambient wizards may offer help. Free when
    /// the zone has none.
    /// </summary>
    internal static void NotifyDuel(IActorRef zone, AmbientDuelNotice notice) {
        if (zone is not null && !s_groups.IsEmpty && s_groups.TryGetValue(zone, out var group)) {
            group.Tell(notice);
        }
    }

    /// <summary>The settings in force (Off until the director starts).</summary>
    internal static AmbientSettings Settings { get; set; } = AmbientSettings.Off;

    internal static int Count => s_byChar.Count;

    internal static IEnumerable<AmbientWizard> All => s_byChar.Values;

    internal static void Register(AmbientWizard wizard) {
        s_byChar[wizard.CharId] = wizard;
        if (wizard.Endpoint is not null) {
            s_byActor[wizard.Endpoint] = wizard;
        }
    }

    internal static void Unregister(AmbientWizard wizard) {
        s_byChar.TryRemove(wizard.CharId, out _);
        if (wizard.Endpoint is not null) {
            s_byActor.TryRemove(wizard.Endpoint, out _);
            s_joinPermits.TryRemove(wizard.Endpoint, out _);
            s_sparring.TryRemove(wizard.Endpoint, out _);
        }
    }

    /// <summary>True when <paramref name="actor"/> is an ambient wizard's endpoint.</summary>
    internal static bool IsAmbient(IActorRef actor) => actor is not null && !s_byActor.IsEmpty && s_byActor.ContainsKey(actor);

    /// <summary>True when <paramref name="charId"/> is an ambient wizard's character id.</summary>
    internal static bool IsAmbientChar(ulong charId) => !s_byChar.IsEmpty && s_byChar.ContainsKey(charId);

    internal static bool TryGet(IActorRef actor, out AmbientWizard wizard) {
        wizard = null;
        return actor is not null && !s_byActor.IsEmpty && s_byActor.TryGetValue(actor, out wizard);
    }

    internal static bool TryGet(ulong charId, out AmbientWizard wizard) {
        wizard = null;
        return !s_byChar.IsEmpty && s_byChar.TryGetValue(charId, out wizard);
    }

    /// <summary>The ambient wizard's Wizard, for code that looks characters up by id (friends, buddy stats).</summary>
    internal static bool TryGetWizard(ulong charId, out Wizard wizard) {
        wizard = TryGet(charId, out var ambient) ? ambient.Wizard : null;
        return wizard is not null;
    }

    /// <summary>The player said yes: the ambient wizard may take a free slot in this duel.</summary>
    internal static void PermitJoin(IActorRef actor, ulong sigilId) => s_joinPermits[actor] = sigilId;

    internal static void RevokeJoin(IActorRef actor) => s_joinPermits.TryRemove(actor, out _);

    /// <summary>
    /// May this player-actor join the duel at <paramref name="sigilId"/> by walking into it? Real players always may;
    /// an ambient wizard only with a permit for that duel (a yes, or its own street fight).
    /// </summary>
    internal static bool MayJoin(IActorRef actor, ulong sigilId)
        => !IsAmbient(actor) || (s_joinPermits.TryGetValue(actor, out var permitted) && permitted == sigilId);

    /// <summary>
    /// May this ambient wizard start a fight with a creature (its own street fight)? Real players are never asked.
    /// </summary>
    internal static bool MayEngage(IActorRef actor)
        => !IsAmbient(actor) || (s_joinPermits.TryGetValue(actor, out var permitted) && permitted == 0);

    /// <summary>Marks the ambient wizard as seated for a PvP practice match (the sparring hook).</summary>
    internal static void MarkSparring(IActorRef actor, ulong sigilId) => s_sparring[actor] = sigilId;

    internal static void EndSparring(IActorRef actor) => s_sparring.TryRemove(actor, out _);

    /// <summary>True when the ambient wizard was seated in this PvP duel through the sparring hook.</summary>
    internal static bool IsSparringIn(IActorRef actor, ulong sigilId)
        => s_sparring.TryGetValue(actor, out var seated) && seated == sigilId;

    /// <summary>
    /// The WizardCharacterBehavior for an identity: the classic creation look, starter gear in one colour with a trim.
    /// </summary>
    internal static WizardCharacterBehavior AvatarFor(AmbientIdentity identity) {
        var look = identity.Look;
        return new WizardCharacterBehavior {
            m_eGender = look.Female ? eGender.Female : eGender.Male,
            m_eRace = eRace.Human,
            m_nHeadHandsModel = (Bui2) 0,
            m_nHairModel = (Bui4) look.HairModel,
            m_nHatModel = (Bui2) 0,
            m_nTorsoModel = (Bui2) 0,
            m_nFeetModel = (Bui2) 0,
            m_nWandModel = (Bui2) 0,
            m_nSkinColor = (Bui4) look.SkinColor,
            m_nSkinDecal = (Bui4) look.Face,
            m_nHairColor = (Bui7) look.HairColor,
            m_nHatColor = (Bui5) look.ClothingColor,
            m_nHatDecal = (Bui5) look.TrimColor,
            m_nTorsoColor = (Bui5) look.ClothingColor,
            m_nTorsoDecal = (Bui5) look.TrimColor,
            m_nTorsoDecal2 = (Bui5) 0,
            m_nFeetColor = (Bui5) look.ClothingColor,
            m_nFeetDecal = (Bui5) look.TrimColor,
        };
    }

    /// <summary>The server's MagicSchool for an ambient school.</summary>
    internal static MagicSchool SchoolOf(AmbientSchool school) => school switch {
        AmbientSchool.Fire => MagicSchool.Fire, AmbientSchool.Ice => MagicSchool.Ice, AmbientSchool.Storm => MagicSchool.Storm,
        AmbientSchool.Myth => MagicSchool.Myth, AmbientSchool.Life => MagicSchool.Life, AmbientSchool.Death => MagicSchool.Death,
        _ => MagicSchool.Balance,
    };

    /// <summary>
    /// Builds the in-memory Wizard for a record (never saved: WizardCollection ignores ambient character ids).
    /// </summary>
    internal static Wizard BuildWizard(AmbientWizardRecord record) {
        var identity = record.ToIdentity();
        var wizard = Wizard.CreateAmbient(record.CharId, SchoolOf(identity.School), AvatarFor(identity), identity.NameKeys,
            identity.Level, record.HomeZone);
        wizard.AccountId = record.CharId; // its own fake account id; never an account row
        wizard.Account = new Account { AuthLevel = AuthLevel.None, ChatMode = ChatMode.Open }; // in memory only
        wizard.Account.Characters.Add(wizard);

        // The experience points of the level's start, as a real wizard of that level has.
        wizard.MagicSchoolBehavior.ExperiencePoints = Shared.Character.MagicLevelsConfig.GetExperiencePointsAtLevel(identity.Level);

        // CLASSIC (2026-10-02): wear what a classic new wizard wears, the school's starter wand and the starter deck.
        // Without a wand the official client builds the player as "NA Player - Unarmed", whose cast cinematics have no
        // wand node to put the spell effect on ("Effect Relative to Actor, but no name was provided"); the client froze
        // in the first ambient wizard's cast on live (owner, Unicorn Way, 2026-10-02).
        foreach (var templateId in ClassicStart.StarterItemTemplateIds(wizard.MagicSchoolBehavior.MagicSchool)) {
            EquipInMemory(wizard, templateId);
        }

        Shared.Character.CharacterHelper.RecalculateGameStats(wizard);
        wizard.GameStats.m_currentHitpoints = wizard.GameStats.m_baseHitpoints;
        wizard.GameStats.m_currentMana = wizard.GameStats.m_baseMana;

        return wizard;
    }

    /// <summary>Puts an item made from <paramref name="templateId"/> in its slot, in memory only.</summary>
    internal static bool EquipInMemory(Wizard wizard, ulong templateId) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not WizItemTemplate template
            || Shared.Items.ItemHelper.GetItemSlot(template) is not { } slot) {
            return false;
        }

        var item = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(templateId);
        CoreObjectFactory.InitializeCoreObjectBehaviors(item, templateId);
        item.m_characterId = (Imcodec.Types.GID) wizard.CharId;

        return wizard.EquipmentBehavior.EquipItem(item, slot.SlotType);
    }

    /// <summary>
    /// The client's yaw for a heading <paramref name="theta"/> (radians from +X towards +Y). The client turns clockwise
    /// with a quarter-turn offset; CombatDuelComponent seats duelists with the same conversion (YAW_ERROR_COMPENSATION).
    /// </summary>
    internal static float ClientYaw(float theta) => Wrap(2 * MathF.PI - theta - YawOffset);

    /// <summary>The heading for a client yaw (the inverse of <see cref="ClientYaw"/>).</summary>
    internal static float Heading(float clientYaw) => Wrap(2 * MathF.PI - YawOffset - clientYaw);

    private const float YawOffset = 1.58f;

    private static float Wrap(float angle) {
        angle %= 2 * MathF.PI;
        return angle < 0 ? angle + 2 * MathF.PI : angle;
    }

}
