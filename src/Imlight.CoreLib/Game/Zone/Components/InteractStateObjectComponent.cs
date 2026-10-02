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
 * INTERACT STATE OBJECT COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: makes a clickable state object work, such as the symbol obelisks
 * of the Temple of Storms mind puzzle (KT-CRY5-C01-002 "Get Smart";
 * KT_Obelisk_Moon/Sun/Bug/Bird/Tree/Snake, templates 87958..87963 and 87877).
 * A click moves the object to the next state of its state set (Idle_Off and
 * Idle_On swap, through Turn_On / Turn_Off), every client sees the change, and
 * the object posts "<zone tag>.<state>.EnterState" for the zone's triggers,
 * whose ReqState checks read the state from ZoneObjectStates.
 *
 * USAGE EXAMPLE:
 * Attaches to object templates with a state set and the server-side
 * InteractableBehavior; the object offers a click only in a zone whose
 * triggers listen to its state events.
 *
 * NOTE:
 * The template's InteractableBehavior is a server class the client type list
 * no longer has, so it reads as a null behavior; its two options (r806919
 * KT_Obelisk_Moon.xml) are "from Idle_Off to Idle_On" and "from Idle_On to
 * Idle_Off", which is what taking the state's first transition does. 2009
 * guides: clicking a stone turns its symbol on or off. The state is per zone
 * instance, shared by everyone in it. Only under the classic quest rules.
 *
 * TODO:
 * - The option's icon and prompt live in the unreadable InteractableBehavior (Use_KT_Obelisk_Bird.dds); the default
 *   interact prompt is shown.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.States;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractStateObjectComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName => "Interact";
    public string NpcIcon => "";
    public string NpcNameKey => "";
    public string NpcTextKey => "";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private List<StateNode> _states;
    private string _startState;

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        if (!ClassicQuestEngine.IsActive || template is not GameObjectTemplate go || go.m_objectName is null
                || go.m_behaviors is not { Count: > 0 } behaviors) {
            return false;
        }

        var name = go.m_objectName.ToString();
        if (name.Contains("Sigil", StringComparison.Ordinal) || name.Contains("Teleport", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Portal", StringComparison.OrdinalIgnoreCase)) {
            return false; // InteractDungeonSigilComponent's and InteractTeleportObjectComponent's
        }

        // The unreadable InteractableBehavior reads as a null behavior.
        return behaviors.Any(behavior => behavior is null) && BasicStates(go) is { Count: > 0 };
    }

    public override void OnStart() {
        _states = Entity.Template is GameObjectTemplate go ? BasicStates(go) : null;
        var tag = Tag;
        if (_states is not { Count: > 0 } || string.IsNullOrEmpty(tag)) {
            return;
        }

        var placed = Entity.Info?.m_startState?.ToString();
        _startState = _states.Any(state => state.Name == placed) ? placed : StartState(Entity.Template as GameObjectTemplate);
        ZoneObjectStates.For(Entity.ZoneRef)?.Track(tag, _startState);
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (!Clickable) {
            return [];
        }

        return [new InteractableOption { m_serviceName = ServiceName }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var table = ZoneObjectStates.For(Entity.ZoneRef);
        var tag = Tag;
        if (!Clickable || table is null) {
            return;
        }

        var current = table.TryGet(tag, out var known) ? known : _startState;
        if (!StateClick.TryNext(_states, current, out var next, out var shown) || !table.Set(tag, next)) {
            return;
        }

        Entity.SetClassicObjectState(next, shown);
        Logger.Debug("State object {Tag} in {Zone}: {From} -> {To} by {Player}.",
            Logger.Args(tag, Entity.Zone?.ZonePath, current, next, playerActor?.Path.Name));
        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = ObjectStateRules.EnterStateEvent(tag, next),
            PlayerActor = playerActor,
            PlayerGameObject = playerObject,
        });
    }

    private string Tag => Entity.Info?.m_zoneTag?.ToString();

    private bool Clickable => _states is { Count: > 0 } && ZoneObjectStates.IsListened(Entity.ZoneRef, Tag);

    // The states of the category the object starts in (KT_Obelisk_BirdStates' "Basic"), when one of them posts state
    // events and has a transition.
    private static List<StateNode> BasicStates(GameObjectTemplate template) {
        var category = StartCategory(template);
        if (category?.m_states is not { Count: > 0 } states
                || !states.Any(state => state is not null && state.m_postStateEvents && state.m_transitions is { Count: > 0 })) {
            return null;
        }

        return [.. states.Where(state => state?.m_stateName is not null).Select(state => new StateNode(
            state.m_stateName.ToString(),
            (state.m_transitions ?? []).Where(t => t?.m_targetState is not null)
                .Select(t => (t.m_targetState.ToString(), t.m_transitionState?.ToString())).ToList(),
            state.m_autoTransition ? state.m_autoState?.ToString() : null))];
    }

    private static string StartState(GameObjectTemplate template)
        => StartCategory(template)?.m_startState?.ToString();

    private static ObjStateCategory StartCategory(GameObjectTemplate template) {
        var setName = template?.m_behaviors?.OfType<ObjectStateBehaviorTemplate>()
            .Select(behavior => behavior.m_stateSetName?.ToString()).FirstOrDefault(name => !string.IsNullOrEmpty(name));
        if (setName is null) {
            return null;
        }

        return StateFactory.GetStateSet(setName)?.m_categories?.FirstOrDefault(category => category is not null);
    }

}
