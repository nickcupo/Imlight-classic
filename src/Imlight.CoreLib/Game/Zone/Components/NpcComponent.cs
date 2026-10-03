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
 * NPC COMPONENT 
 * ========================================================================
 * 
 * PURPOSE:
 * Manages core NPC behavior and interaction mechanics, including 
 * combat, proximity detection, and statistical attributes.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Supports monster and non-monster NPC configurations.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class NpcComponent : ZoneEntityComponent, IComponentFactory, IClientBehaviorProvider<NPCBehavior> {

    public bool NoTransfer { get; set; } = false;
    public bool IsMonster { get; private set; }
    public bool IsBossMonster { get; private set; }
    public float IntelligenceFactor { get; private set; }
    public float SelfishnessFactor { get; private set; }
    public float AggressiveFactor { get; private set; }
    public int StartingHealth { get; private set; }
    public int CurrentHealth { get; private set; }
    public MagicSchool MagicSchool { get; private set; }
    public int Level { get; private set; }
    public float Proximity { get; private set; }
    public string NameOverride { get; private set; }

    // CLASSIC: keyed by the object instance. CoreObject is a record whose hash follows its location, so the default
    // comparer missed the entry after any move: enter events re-fired on every move and exit never fired.
    private readonly Dictionary<CoreObject, IActorRef> _playersInRange = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CoreObject, DateTime> _lastAggroTry = new(ReferenceEqualityComparer.Instance); // CLASSIC
    private readonly NPCBehaviorTemplate _npcBehaviorTemplate;
    private readonly DuelistBehaviorTemplate _duelistBehaviorTemplate;

    private StatsComponent _statsComponent;
    private CombatCreatureDeckComponent _deckComponent;

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate);

    public NpcComponent(ZoneEntity entity) : base(entity) {
        _npcBehaviorTemplate = entity.Template.m_behaviors
            .OfType<NPCBehaviorTemplate>()
            .First();
        _duelistBehaviorTemplate = entity.Template.m_behaviors
            .OfType<DuelistBehaviorTemplate>()
            .FirstOrDefault();

        this.IsBossMonster = _npcBehaviorTemplate.m_bossMob;
        this.IntelligenceFactor = _npcBehaviorTemplate.m_fIntelligence;
        this.SelfishnessFactor = _npcBehaviorTemplate.m_fSelfishFactor;
        this.AggressiveFactor = _npcBehaviorTemplate.m_nAggressiveFactor;
        // CLASSIC: the creature's health at the profile's cutoff, where a dated value exists.
        this.StartingHealth = (entity.Template is GameObjectTemplate gameTemplate ? ClassicProgression.MobStats?.HealthOf(gameTemplate.m_templateID) : null) ?? _npcBehaviorTemplate.m_nStartingHealth;

        this.IsMonster = _duelistBehaviorTemplate is not null;
        this.Proximity = _duelistBehaviorTemplate?.m_npcProximity ?? 0;
        
        // Try to parse the npcBehaviorTemplate.m_schoolOfFocus to a MagicSchool.
        var parsedSchool = MagicSchool.Balance;
        if (    _npcBehaviorTemplate.m_schoolOfFocus != "" 
            && !Enum.TryParse(_npcBehaviorTemplate.m_schoolOfFocus, out parsedSchool)) {
            Logger.Error("Failed to parse magic school {0} for creature {1}.",
                Logger.Args(_npcBehaviorTemplate.m_schoolOfFocus, Entity.ActiveGameObject.m_globalID));

            return;
        }

        this.MagicSchool = parsedSchool;
        this.Level = _npcBehaviorTemplate.m_nLevel;
    }

    public override void OnStart() {
        // All NPCs have game stats.
        _statsComponent = Entity.GetComponentOfType<StatsComponent>();
        if (_statsComponent is null) {
            Logger.Error("NPC {0} does not have a StatsComponent.", Logger.Args(Entity.ActiveGameObject.m_globalID));

            return;
        }

        _statsComponent.Stats.m_currentHitpoints = StartingHealth;
        _statsComponent.Stats.m_baseHitpoints = StartingHealth;

        // Boss mobs will sometimes have base effects in their NPC template.
        if (_npcBehaviorTemplate.m_baseEffects.Count > 0) {
            foreach (var effect in _npcBehaviorTemplate.m_baseEffects) {
                CharacterEffectHelper.AddGameEffectToStats(_statsComponent.Stats, effect);
            }
        }

        // If the NPC is a monster, it will have a deck.
        if (IsMonster) {
            _deckComponent = Entity.GetComponentOfType<CombatCreatureDeckComponent>();
            if (_deckComponent is null) {
                Logger.Error("NPC {0} does not have a CombatDeckComponent.", 
                    Logger.Args(Entity.ActiveGameObject.m_debugName));

                return;
            }
        }

        Classic.CreatureStatsDirectory.Set(Entity.SelfRef, this); // CLASSIC: duels read the stats without an Ask.
    }

    // CLASSIC: forget a player who leaves the zone, so the same object coming back in range counts as an enter again.
    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        foreach (var key in _playersInRange.Where(x => x.Value.Equals(playerActor)).Select(x => x.Key).ToList()) {
            _playersInRange.Remove(key);
            _lastAggroTry.Remove(key); // CLASSIC
        }
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // Check if the player is now in range of the object.
        if (IsInRadius(playerObj, Proximity) && !_playersInRange.ContainsKey(playerObj)) {
            // If the player is in range, trigger the enter events.
            OnProximityEnter(playerObj, playerActor, playerWizard);
            // CLASSIC: a wizard still in a duel or its after-duel grace is not remembered as inside the radius, so the
            // first move after the grace aggroes. A creature that appears beside the wizard as a duel ends (the Plague
            // Oni rising from Ideyoshi in MS_Plague2_PalaceInterior) otherwise never fights while the wizard stays near.
            if (!ClassicQuestEngine.IsActive || !Imlight.Classic.Quests.ProximityAggro.Deferred(
                    IsMonster, playerWizard?.IsInCombatGrace == true, playerWizard?.IsInDuel == true)) {
                _playersInRange.Add(playerObj, playerActor);
            }
        }
        // CLASSIC: a wizard still inside the radius and free again is tried again (ProximityAggro.Retry).
        else if (ClassicQuestEngine.IsActive && playerWizard is not null && IsInRadius(playerObj, Proximity)
                && _playersInRange.ContainsKey(playerObj)
                && Imlight.Classic.Quests.ProximityAggro.Retry(IsMonster, playerWizard.IsInCombatGrace, playerWizard.IsInDuel,
                    _lastAggroTry.GetValueOrDefault(playerObj), DateTime.UtcNow)) {
            OnProximityEnter(playerObj, playerActor, playerWizard);
        } 
        else if (!IsInRadius(playerObj, Proximity) && _playersInRange.ContainsKey(playerObj)) {
            _playersInRange.Remove(playerObj);
            _lastAggroTry.Remove(playerObj); // CLASSIC
        }
    }

    public NPCBehavior GetClientBehaviorInstance() => new() {
        m_isMonster = IsMonster,
        m_wsNameOverride = NameOverride,
    };

    private void OnProximityEnter(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // Bug fix: There is an off chance the player spawns in the middle of our proximity radius,
        // before the wizard even has time to load themselves into the world.
        if (playerWizard is null) {
            return;
        }

        if (!IsMonster || playerWizard.IsInCombatGrace || playerWizard.IsInDuel) {
            if (IsMonster && ClassicQuestEngine.IsActive) { // CLASSIC: a trace of aggro the grace or a duel held back.
                Logger.Debug("{Creature} does not aggro on {Player}: grace {Grace}, in duel {Duel}.",
                    Logger.Args(Entity.ActiveGameObject?.m_debugName, playerActor?.Path.Name, playerWizard.IsInCombatGrace, playerWizard.IsInDuel));
            }

            return;
        }

        // CLASSIC: an ambient wizard walking into a duel circle does not pull this creature into a new fight.
        if (!Classic.Ambient.AmbientWizards.MayEngage(playerActor)) {
            return;
        }

        if (Classic.Ambient.AmbientWizards.IsAmbient(playerActor)) {
            Logger.Debug("{Creature} aggroes on ambient wizard {Name}.",
                Logger.Args(Entity.ActiveGameObject?.m_debugName, playerWizard.PlayerNameBehavior?.GetWizardName()));
        }

        _lastAggroTry[playerObj] = DateTime.UtcNow; // CLASSIC: ProximityAggro.Retry

        // Hey! I'm a dueling creature and a player just entered my proximity.
        // I really don't like that.
        var interactionMsg = new ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL {
            StartingParticipants = new Dictionary<IActorRef, CoreObject> {
                { playerActor, playerObj },
                { Entity.SelfRef, Entity.ActiveGameObject },
            },
        };
        Entity.ZoneRef.Tell(interactionMsg);

        // We do nothing further here. This message will be sent to the ZoneSigilSupervisor
        // to locate the closest sigil to the player and the creature.
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_QUERYCREATURESTATS))]
    private void ReceiveQueryGameStats(COMBAT_106_PROTOCOL.MSG_QUERYCREATURESTATS message)
        => Sender.Tell(BuildCreatureStats());

    /// <summary>
    /// CLASSIC: the MSG_QUERYCREATURESTATS answer, also read by duels through CreatureStatsDirectory without an Ask (the
    /// same object references the answer carried: the stats component's stats and the deck's spell list).
    /// </summary>
    internal COMBAT_106_PROTOCOL.MSG_CREATURESTATS BuildCreatureStats() {
        return new COMBAT_106_PROTOCOL.MSG_CREATURESTATS {
            GameStats = _statsComponent.Stats,
            CombatIntelligence = IntelligenceFactor,
            CombatSelfishFactor = SelfishnessFactor,
            CombatAggressionFactor = AggressiveFactor,
            CombatLevel = Level,
            MagicSchool = MagicSchool,
            SpellList = _deckComponent?.Spells ?? [],
        };
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET))]
    private void ReceiveQueryNearestDuelTarget(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET message) {
        // Only dueling creatures whose aggro radius covers the player answer this query.
        if (!IsMonster || message.PlayerGameObject is null || !IsInRadius(message.PlayerGameObject, Proximity)) {
            return;
        }

        Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGETRSP {
            CreatureActor = Entity.SelfRef,
            CreatureObject = Entity.ActiveGameObject,
        });
    }

}