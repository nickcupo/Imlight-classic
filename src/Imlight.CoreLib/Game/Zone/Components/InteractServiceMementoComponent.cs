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
 * INTERACT SERVICE MEMENTO
 * ========================================================================
 * 
 * PURPOSE:
 * Manages complex NPC interaction mechanics, tracking player proximity 
 * and service component interactions for zone entities.
 * 
 * USAGE EXAMPLE:
 * Always attached to an entity. Implement a service component to add functionality.
 * 
 * NOTE:
 * Keeps track of any components of type `IServiceComponent` attached to the entity.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Interface for service components that can be attached to a zone entity.
/// </summary>
public interface IServiceComponent {

    IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter);
    void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex);

    string ServiceName { get; }
    string NpcIcon { get; }
    string NpcNameKey { get; }
    string NpcTextKey { get; }
    WizBangs WizBang { get; }
    string StateName { get; }
    string InteractWizBang { get; }
    string DisplayKey { get; }

    float DEFAULT_INTERACTION_RADIUS => 300.0f;

}

/// <summary>
/// CLASSIC: which component owns each option index, kept per wizard. An NPC rebuilds its option list for whichever wizard asked last;
/// the indexes of one wizard's list must not decide where another wizard's click goes (two friends at the same NPC, one just handed in a
/// goal and offered the next quest, the other clicking the hand-in: the click reached the offer service and did nothing).
/// </summary>
/// <summary>
/// CLASSIC: a service component that lists several options (the quests an NPC offers, has underway, or a goal of);
/// the memento tells it which of its own options was clicked.
/// </summary>
internal interface IOrdinalServiceComponent {

    void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex,
                              int ordinal);

}

internal sealed class PlayerOptionIndex {

    private readonly Dictionary<IActorRef, Dictionary<int, IServiceComponent>> _maps = [];

    internal void Set(IActorRef player, IReadOnlyDictionary<int, IServiceComponent> map) => _maps[player] = new Dictionary<int, IServiceComponent>(map);

    internal void Remove(IActorRef player) => _maps.Remove(player);

    /// <summary>The component for <paramref name="index"/> in the list this wizard was sent. CLASSIC: none when it was
    /// sent no list (or has left the NPC's range); the NPC's last-built list was a fallback any wizard could click
    /// through from anywhere (security audit 2026-10-04).</summary>
    /// <summary>CLASSIC: the position of option <paramref name="index"/> among its own component's options in the list
    /// this wizard was sent (an NPC with two quests to offer lists two QuestOfferService options); 0 when unknown.</summary>
    internal int OrdinalOf(IActorRef player, int index)
        => _maps.TryGetValue(player, out var map) ? Ordinal(map, index) : 0;

    internal static int Ordinal(IReadOnlyDictionary<int, IServiceComponent> map, int index)
        => map.TryGetValue(index, out var owner) ? map.Count(entry => entry.Key < index && ReferenceEquals(entry.Value, owner)) : 0;

    // CLASSIC: the named NPC protocol must agree with the option this wizard was sent. The generic
    // MSG_INTERACTOPTION path calls the same handler with "Interact" and carries only an index.
    internal bool TryResolve(IActorRef player, int index, string serviceName, out IServiceComponent component) {
        if (TryResolve(player, index, out var found)
                && (serviceName == "Interact" || string.Equals(found.ServiceName, serviceName, StringComparison.Ordinal))) {
            component = found;
            return true;
        }

        component = null;
        return false;
    }

    internal bool TryResolve(IActorRef player, int index, out IServiceComponent component) {
        if (_maps.TryGetValue(player, out var map) && map.TryGetValue(index, out var found)) {
            component = found;

            return true;
        }

        component = null;

        return false;
    }

}

internal sealed class InteractServiceMementoComponent(ZoneEntity entity) 
    : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {

    private const string DEFAULT_NAME_KEY = "NPCFormats_Name";
    private const string DEFAULT_TEXT_KEY = "GUI_NPCInteractText";
    private const string WIZBANG_UPDATE_TIMER_KEY = "WIZBANG_UPDATE_TIMER";
    private const double WIZBANG_UPDATE_INTERVAL_SECONDS = 1.0;
    private const float DEFAULT_RENDER_DISTANCE = 5000.0f; // Default wizbang render distance.

    private readonly float _interactionRadius = 300.0f;
    // CLASSIC: per wizard, the quests and goals already sent in their chaining class at this NPC (DialogChain); cleared
    // when the wizard leaves this NPC's range or the zone.
    private readonly Dictionary<IActorRef, HashSet<string>> _chained = [];
    private readonly Dictionary<IActorRef, List<(ulong Id, string Name)>> _questSnapshot = []; // CLASSIC: DialogChain.QuestDiff
    private readonly PlayersInRange _playersInInteractionRange = new(); // CLASSIC: a new session actor replaces a stale one.
    private readonly PlayersInRange _playersInRenderRange = new(); // CLASSIC
    private List<IServiceComponent> _serviceComponents = [];
    private Dictionary<int, IServiceComponent> _optionIndexToComponent = [];
    private readonly PlayerOptionIndex _playerOptionIndex = new(); // CLASSIC: each wizard's own option indexes
    private ServiceMementoBase _serviceMemento;
    private MadlibBlock _madlibBlock;
    private float _renderDistance;

    public ITimerScheduler Timers { get; set; }

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => true;

    public override void OnStart() {
        // Set render distance - use zone far clip if available, otherwise default.
        _renderDistance = Entity.Zone?.ZoneData?.m_farClip ?? DEFAULT_RENDER_DISTANCE;
        
        RefreshServiceMomento(null);

        // The component set is fixed once loaded, so an entity without services never has a wizbang.
        if (_serviceComponents.Count <= 0) {
            return;
        }

        var updateInterval = TimeSpan.FromSeconds(WIZBANG_UPDATE_INTERVAL_SECONDS);
        var updateMsg = new ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL();
        Timers.StartPeriodicTimer(WIZBANG_UPDATE_TIMER_KEY, updateMsg, updateInterval);
    }

    // CLASSIC: a wizard who logs in or arrives standing inside an object's range only got its options on the first move, and a
    // wizard who never moves (a login beside the Ravenwood Spiral Door) never got them. Range is checked on join too, and once
    // more a few seconds later in case the client was not yet ready for the first message.
    private static readonly TimeSpan JoinResendDelay = TimeSpan.FromSeconds(4);

    internal sealed record ResendJoinRange(IActorRef PlayerActor) : IServerMessage {
        public byte MessageOrder => 0;
        public byte ServiceID => 102;
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0) {
            return;
        }

        OnPlayerMove(playerObj, playerActor, playerWizard);
        Timers?.StartSingleTimer($"join-range-{playerActor.Path.Name}-{Entity.ActiveGameObject.m_globalID.Full}", new ResendJoinRange(playerActor), JoinResendDelay);
    }

    [MessageHandler(typeof(ResendJoinRange))]
    private void ReceiveResendJoinRange(ResendJoinRange message) {
        if (!_playersInInteractionRange.Contains(message.PlayerActor) || _lastInteraction.ContainsKey(message.PlayerActor)) {
            return;
        }

        RefreshServiceOptions(message.PlayerActor);
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        _sentTeleportOptions.Remove(playerActor); // CLASSIC
        _playerOptionIndex.Remove(playerActor); // CLASSIC
        _lastInteraction.Remove(playerActor); // CLASSIC
        _chained.Remove(playerActor); // CLASSIC
        _questSnapshot.Remove(playerActor); // CLASSIC
        _playersInInteractionRange.Remove(playerActor);
        _playersInRenderRange.Remove(playerActor);

    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0) {
            return;
        }

        var playerId = playerObj.m_globalID.Full;

        // Effective interaction radius = the SMALLEST any attached service asks for (default 300).
        var interactionRadius = _serviceComponents.Count > 0
            ? _serviceComponents.Min(c => c.DEFAULT_INTERACTION_RADIUS)
            : _interactionRadius;

        // Handle interaction range (for service options).
        switch (_playersInInteractionRange.Update(playerId, playerActor, IsInRadius(playerObj, interactionRadius))) {
            case RangeChange.Entered:
                SendActorServiceOptions(playerActor);
                break;
            case RangeChange.Left:
                _chained.Remove(playerActor); // CLASSIC: a new visit chains afresh
                _sentTeleportOptions.Remove(playerActor); // CLASSIC
                _playerOptionIndex.Remove(playerActor); // CLASSIC: its options are gone with it
                SendLeaveServiceRange(playerActor);
                break;
        }

        // Handle render range (for wizbangs).
        _playersInRenderRange.Update(playerId, playerActor, IsInRadius(playerObj, _renderDistance));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEINTERACTION))]
    private void PlayerInteraction(ZONE_102_PROTOCOL.MSG_ZONEINTERACTION message) {
        var playerActor = message.PlayerActor;
        var playerCharacter = message.PlayerCharacter;
        var playerObject = message.PlayerObject;
        var serviceName = message.ServiceName;
        var serviceIndex = message.ServiceIndex;
        var reinteract = message.Reinteract;

        Logger.Debug("Player {0} interacted with NPC {1} using service {2} at index {3} (Reinteract: {4})",
            Logger.Args(playerActor.Path.Name, Entity.ActiveGameObject.m_globalID.Full, serviceName, serviceIndex, reinteract));

        if (_serviceComponents.Count <= 0) {
            Logger.Warning("No service components found for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_debugName));

            return;
        }

        // CLASSIC: the server's own position of the wizard must be near the NPC (security audit 2026-10-04). Generous
        // (InteractionRange.Allowed: radius * 1.5 + 150) so a click sent just before a step out still counts.
        if (playerObject?.m_location is { } playerLocation && Entity.ActiveGameObject is { } npcObject) {
            var radius = _serviceComponents.Max(c => c.DEFAULT_INTERACTION_RADIUS);
            var distanceSquared = (playerLocation - npcObject.m_location).LengthSquared();
            if (!Imlight.Classic.Security.InteractionRange.Within(distanceSquared, radius)) {
                Logger.Warning("Player {0} interacted with NPC {1} from {2:0} units away (allowed {3:0}); refused.",
                    Logger.Args(playerActor.Path.Name, npcObject.m_debugName, Math.Sqrt(distanceSquared),
                        Imlight.Classic.Security.InteractionRange.Allowed(radius)));

                return;
            }
        }

        // CLASSIC: the client asking for this NPC's current options (an empty service name with Reinteract, sent by
        // InteractService after r806919 dropped its cached list): answer with the list, Reinteract echoed. No
        // LEAVESERVICERANGE (the client already dropped its cache; a LEAVE now would cancel the new interaction) and no
        // routing (which would send the list a second time).
        // CLASSIC: a chained option (DialogChain): find it in this wizard's current list; that list becomes the one the
        // click is routed by. Not there (the quest or goal is not at this NPC now): the current list is sent instead.
        if (message.ChainQuestId != 0 || !string.IsNullOrEmpty(message.ChainQuestName)) {
            var owners = new Dictionary<int, IServiceComponent>();
            var current = BuildServiceOptions(_serviceComponents, playerCharacter, owners);
            var found = DialogChain.FindChosen(current, serviceName, message.ChainQuestId, message.ChainGoalId,
                message.ChainQuestName is { Length: > 0 } name ? QuestTemplateCollection.GetQuestByName(name)?.m_questTitle : null);
            if (found < 0) {
                Logger.Debug("Chained option of NPC {0} ({1} {2}/{3}) is not in the wizard's options; re-sent the options.",
                    Logger.Args(Entity.ActiveGameObject.m_debugName, serviceName, message.ChainQuestName ?? message.ChainQuestId.ToString(),
                        message.ChainGoalId));
                SendActorServiceOptions(playerActor, 0);

                return;
            }

            _playerOptionIndex.Set(playerActor, owners);
            serviceIndex = (uint) found;
        }

        if (OnlyAnswersOptions(serviceName, reinteract, ClassicQuestEngine.IsActive)) {
            SendActorServiceOptions(playerActor, reinteract);

            return;
        }

        // If this is a reinteract scenario, send service range messages first.
        if (reinteract == 2) {
            RefreshServiceOptions(playerActor, reinteract);
        }

        // Route to the component that owns this option index.
        // Using FirstOrDefault by ServiceName is unsafe when multiple components share
        // the same service name (e.g. InteractQuestSelectComponent shadowing WoodenChestComponent).
        // CLASSIC: in the list this wizard was sent, not the one the NPC built for whoever asked last.
        var ordinal = 0; // CLASSIC: which of the component's own options (IOrdinalServiceComponent)
        if (_playerOptionIndex.TryResolve(playerActor, (int) serviceIndex, serviceName, out var serviceComponent)) {
            ordinal = _playerOptionIndex.OrdinalOf(playerActor, (int) serviceIndex);
        } else if (TryResolveFromCurrentOptions(playerActor, playerCharacter, ref serviceIndex, serviceName, out serviceComponent)) {
            ordinal = PlayerOptionIndex.Ordinal(_optionIndexToComponent, (int) serviceIndex);
        } else {
            // CLASSIC: the click names an option of a list this wizard no longer has. Send the current list instead of
            // dropping the click silently (the dialog then shows what the NPC offers now).
            Logger.Debug("No component owns service index {0} for NPC {1} with service name {2}; re-sent the options.",
                Logger.Args(serviceIndex, Entity.ActiveGameObject.m_debugName, serviceName));
            RefreshServiceOptions(playerActor, 2);

            return;
        }

        _lastInteraction[playerActor] = DateTime.UtcNow; // CLASSIC

        // Call the service component's interaction method.
        if (serviceComponent is IOrdinalServiceComponent several) {
            several.OnServiceInteraction(playerActor, playerCharacter, playerObject, serviceIndex, ordinal);
        } else {
            serviceComponent.OnServiceInteraction(playerActor, playerCharacter, playerObject, serviceIndex);
        }
    }

    /// <summary>
    /// CLASSIC: the option at <paramref name="index"/> in the list the NPC has for this wizard now, when it is the
    /// service the click names. The wizard's own list was sent on coming into range; a quest step since then (a goal
    /// done elsewhere, a quest turned in) can add an option the client shows from the NPC's wizbang while the list the
    /// server kept for the wizard has no such index (WC-ST01-NPC02's QuestOfferService, rig-final 2026-10-04: every
    /// click dropped as "No component owns service index 0").
    /// </summary>
    private bool TryResolveFromCurrentOptions(IActorRef playerActor, Wizard wizard, ref uint serviceIndex, string serviceName,
                                              out IServiceComponent component) {
        component = null;
        var index = (int) serviceIndex;
        wizard ??= PlayerQuery.ActiveWizard(playerActor, $"Service options of {Entity.ActiveGameObject?.m_debugName}");
        if (wizard is null) {
            return false;
        }

        RefreshServiceMomento(wizard);
        var options = _serviceMemento?.m_serviceOptions;
        if (options is null) {
            return false;
        }

        // The index as clicked when it names that service; else the one option of that service (a server-made click,
        // the seamless quest offer, says index 0 whatever the list).
        bool Named(int i) => string.Equals(options[i]?.m_serviceName?.ToString(), serviceName, StringComparison.Ordinal);
        if (index < 0 || index >= options.Count || !Named(index)) {
            var named = Enumerable.Range(0, options.Count).Where(Named).ToList();
            if (named.Count != 1) {
                return false;
            }

            index = named[0];
        }

        if (!_optionIndexToComponent.TryGetValue(index, out var current)) {
            return false;
        }

        _playerOptionIndex.Set(playerActor, _optionIndexToComponent);
        _sentTeleportOptions[playerActor] = options.Count;
        component = current;
        serviceIndex = (uint) index;

        return true;
    }

    /// <summary>CLASSIC: ResReInteract for one wizard (see ResReInteractHandler).</summary>
    internal sealed record ReInteractPlayer(IActorRef PlayerActor) : IServerMessage {
        public byte MessageOrder => 0;
        public byte ServiceID => 102;
    }

    // CLASSIC: when each wizard last used this object's options; a ResReInteract reopens the dialog only for them.
    private readonly Dictionary<IActorRef, DateTime> _lastInteraction = [];
    private static readonly TimeSpan ReInteractWindow = TimeSpan.FromMinutes(2);

    [MessageHandler(typeof(ReInteractPlayer))]
    private void ReceiveReInteract(ReInteractPlayer message) {
        var playerActor = message.PlayerActor;
        if (playerActor is null || _serviceComponents.Count <= 0 || !_playersInInteractionRange.Contains(playerActor)) {
            return;
        }

        // The object the wizard was just talking to opens its dialog again (Reinteract 2); any other in range only
        // refreshes its list.
        var reopen = _lastInteraction.TryGetValue(playerActor, out var at) && DateTime.UtcNow - at < ReInteractWindow;
        Logger.Debug("ResReInteract: {0} {1} its options to {2}.",
            Logger.Args(Entity.ActiveGameObject?.m_debugName, reopen ? "reopens" : "refreshes", playerActor.Path.Name));
        RefreshServiceOptions(playerActor, reopen ? 2 : 0);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL))]
    private void HandleWizBangUpdateInterval(ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL message) {
        if (_serviceComponents.Count <= 0 || _playersInRenderRange.Count == 0) {
            return;
        }

        // Query every nearby player's wizard concurrently instead of
        // blocking on each one sequentially.
        var playerActors = _playersInRenderRange.Actors;
        var queryTasks = playerActors.Select(async playerActor => {
            // CLASSIC: read the session's pushed wizard; only a session not in the directory is asked.
            if (ActiveWizardDirectory.TryGet(playerActor, out var known, out _)) {
                return known;
            }

            try {
                var msg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
                var rsp = await playerActor.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(msg, PlayerQuery.Timeout); // CLASSIC: timeout
                return rsp.Wizard;
            }
            catch {
                return null;
            }
        });

        Task.WhenAll(queryTasks)
            .ContinueWith(t => {
                var wizards = t.Result.ToArray();
                return new ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT {
                    PlayerActors = playerActors,
                    Wizards = wizards
                };
            })
            .PipeTo(Self);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT))]
    private void HandleWizBangUpdateResult(ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT result) {
        for (int i = 0; i < result.PlayerActors.Length; i++) {
            var wizard = result.Wizards[i];
            if (wizard != null) {
                SendWizBang(result.PlayerActors[i], wizard);
                ResendTeleportOptionsIfChanged(result.PlayerActors[i], wizard); // CLASSIC
            }
        }
    }

    // CLASSIC: a teleport stone is discovered by walking into its volume, which is inside its interaction range, so
    // the options the wizard got on coming into range (none) are stale; send them again once they change.
    private readonly Dictionary<IActorRef, int> _sentTeleportOptions = [];

    private void ResendTeleportOptionsIfChanged(IActorRef playerActor, Wizard wizard) {
        if (!ClassicQuestEngine.IsActive || !_playersInInteractionRange.Contains(playerActor)) {
            return;
        }

        if (!_serviceComponents.OfType<InteractTeleportObjectComponent>().Any()) {
            // CLASSIC: any other object (a sigil, a quest object): a wizard who stands in range when the quest step that
            // opens it is done had an empty list sent on coming into range; send it again once something is on offer.
            if (_sentTeleportOptions.TryGetValue(playerActor, out var before) && before == 0
                    && _serviceComponents.Sum(component => component.GetServiceOptions(wizard).Count()) > 0) {
                RefreshServiceOptions(playerActor);
            }

            return;
        }

        var count = _serviceComponents.Sum(component => component.GetServiceOptions(wizard).Count());
        if (_sentTeleportOptions.TryGetValue(playerActor, out var sent) && sent == count) {
            return;
        }

        if (_sentTeleportOptions.ContainsKey(playerActor)) {
            RefreshServiceOptions(playerActor);
        }

        _sentTeleportOptions[playerActor] = count;
    }

    private void SendActorServiceOptions(IActorRef playerActor, int reinteract = 0) {
        // CLASSIC: with a timeout; a player who left between coming into range and this question used to stop the
        // object's actor for good (no NEWOBJECT to later arrivals, no interactions, no MSG_QUERYZONEENTITY answer).
        var wizard = PlayerQuery.ActiveWizard(playerActor, $"Service options of {Entity.ActiveGameObject?.m_debugName}");
        if (wizard is null) {
            return;
        }

        RefreshServiceMomento(wizard);
        _playerOptionIndex.Set(playerActor, _optionIndexToComponent); // CLASSIC
        _sentTeleportOptions[playerActor] = _serviceMemento?.m_serviceOptions?.Count ?? 0; // CLASSIC

        // If we have no service options, do not send anything.
        if (_serviceMemento.m_serviceOptions.Count <= 0) {
            _questSnapshot[playerActor] = [.. (wizard.QuestBehavior?.CurrentQuestInstances ?? []).Select(q => (q.ID, q.QuestName))]; // CLASSIC
            return;
        }

        // CLASSIC: answering r806919's re-ask after a dialog (Reinteract 2), offers and quest turn-ins go in the classes
        // the client chains on (DialogChain). The memento is rebuilt for every send, so this changes only this answer.
        var quests = wizard.QuestBehavior?.CurrentQuestInstances ?? [];
        var snapshot = quests.Select(q => (q.ID, q.QuestName)).ToList();
        if (reinteract == 2) {
            var happened = DialogChain.QuestDiff.Between(_questSnapshot.GetValueOrDefault(playerActor), snapshot);
            if (!_chained.TryGetValue(playerActor, out var chained)) _chained[playerActor] = chained = [];
            _serviceMemento.m_serviceOptions = DialogChain.Forms(_serviceMemento.m_serviceOptions,
                id => quests.FirstOrDefault(q => q.ID == id), QuestByTitle, happened, chained);
        }
        _questSnapshot[playerActor] = snapshot; // CLASSIC: what the next re-ask compares with

        // Serialize the service memento and send it to the player.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(_serviceMemento, 4, out var data)) {
            Logger.Error("Failed to serialize service memento for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_debugName));

            return;
        }

        var npcOptionsMsg = new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS {
            // Do not let this property name fool you. 
            // The client incorrectly labels this property as "MobileID." It is in fact
            // the global ID of the NPC. It will not work if you set it to the mobile ID.
            MobileID = Entity.ActiveGameObject.m_globalID.Full,
            Options = data,
            Reinteract = reinteract
        };

        playerActor.Tell(npcOptionsMsg);
    }

    /// <summary>CLASSIC: an interaction that only asks for the NPC's current options.</summary>
    internal static bool IsOptionsRequest(string serviceName, int reinteract)
        => string.IsNullOrEmpty(serviceName) && reinteract != 0;

    /// <summary>
    /// CLASSIC: an interaction answered with the NPC's options and nothing else. With the classic engine every re-ask
    /// (Reinteract 1 or 2) is one, whatever service and index it carries: r806919 picks an offer or a turn-in itself
    /// (MSG_INTERACTAVAILABLEQUEST / MSG_INTERACTCOMPLETEGOAL, chained interactions with Reinteract 0); acting on index 0
    /// opened the NPC's first offer unasked (qa-s146).
    /// </summary>
    internal static bool OnlyAnswersOptions(string serviceName, int reinteract, bool classicEngine)
        => IsOptionsRequest(serviceName, reinteract) || classicEngine && reinteract != 0;

    /// <summary>
    /// CLASSIC: sends a wizard who is still in range this NPC's options again (the join-range resend, ResReInteract, a
    /// teleport stone or quest object with new options, a click on a stale list). r806919's NewNPCInteraction
    /// (0x14172ad00) replaces a known NPC's options in place, while MSG_LEAVESERVICERANGE (0x14172b6a0) runs
    /// RemoveInteraction and CancelInteraction, which closes whatever the wizard has open at this NPC (qa-s164: Sergeant
    /// Muldoon's services menu closed 2 s after X when the join-range resend came). So no LEAVE with the classic engine;
    /// only a real exit from range sends one. The stock engine keeps its LEAVE first.
    /// </summary>
    private void RefreshServiceOptions(IActorRef playerActor, int reinteract = 0) {
        if (RefreshLeavesRange(ClassicQuestEngine.IsActive)) {
            SendLeaveServiceRange(playerActor);
        }

        SendActorServiceOptions(playerActor, reinteract);
    }

    /// <summary>CLASSIC: whether a refresh of a wizard still in range sends MSG_LEAVESERVICERANGE first.</summary>
    internal static bool RefreshLeavesRange(bool classicEngine) => !classicEngine;

    private void SendLeaveServiceRange(IActorRef playerActor) {
        var msg = new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE {
            // Do not let this property name fool you. 
            // The client incorrectly labels this property as "MobileID." It is in fact
            // the global ID of the NPC. It will not work if you set it to the mobile ID.
            MobileID = Entity.ActiveGameObject.m_globalID.Full
        };

        playerActor.Tell(msg);
    }

    private void RefreshServiceMomento(Wizard playerCharacter) {
        _serviceComponents = [.. Entity.GetComponentsOfType<IServiceComponent>()];
        if (_serviceComponents.Count <= 0) {
            return;
        }

        var gameObjTemplate = Entity.Template as GameObjectTemplate;

        // Get all service options and track which component owns each flat index.
        var allOptions = BuildServiceOptions(_serviceComponents, playerCharacter, _optionIndexToComponent);

        // Get UI overrides based on priority.
        var sortedComponents = SortComponentsByPriority(_serviceComponents);
        var highestPriority = sortedComponents.FirstOrDefault();

        SetMadLibBlock();

        var npcIconOrDefault = string.IsNullOrEmpty(highestPriority?.NpcIcon)
            ? gameObjTemplate?.m_sIcon
            : highestPriority.NpcIcon;
        var npcNameKeyOrDefault = string.IsNullOrEmpty(highestPriority?.NpcNameKey)
            ? DEFAULT_NAME_KEY
            : highestPriority.NpcNameKey;
        var npcTextKeyOrDefault = string.IsNullOrEmpty(highestPriority?.NpcTextKey)
            ? DEFAULT_TEXT_KEY
            : highestPriority.NpcTextKey;

        _serviceMemento = new ServiceMementoBase {
            m_npcIcon = npcIconOrDefault ?? string.Empty,
            m_npcNameKey = npcNameKeyOrDefault ?? DEFAULT_NAME_KEY,
            m_npcTextKey = npcTextKeyOrDefault ?? DEFAULT_TEXT_KEY,
            m_serviceOptions = allOptions,
            m_personaMadlibs = _madlibBlock
        };
    }

    // CLASSIC: r806919 sends the selected option's m_serviceIndex in MSG_INTERACTNPC, rather than its
    // visual row number. Every option must publish the same index used by the per-wizard owner map.
    // Without this, Equipment on multi-service NPCs such as Zeke and Eloise sends the default index 0.
    // CLASSIC: the quest whose title key an offer shows (an NPC's offers are listed by title key).
    private static QuestTemplate QuestByTitle(string titleKey)
        => string.IsNullOrEmpty(titleKey) ? null
            : QuestTemplateCollection.GetAllQuests().FirstOrDefault(q => q is not null && q.m_questTitle == titleKey);

    internal static List<ServiceOptionBase> BuildServiceOptions(IEnumerable<IServiceComponent> components,
        Wizard wizard, IDictionary<int, IServiceComponent> owners) {
        owners.Clear();
        var options = new List<ServiceOptionBase>();
        foreach (var component in components) {
            foreach (var option in component.GetServiceOptions(wizard)) {
                option.m_serviceIndex = (uint) options.Count;
                owners[options.Count] = component;
                options.Add(option);
            }
        }

        return options;
    }

    private void SetMadLibBlock() {
        // NPCs normally have a madlib of first name, last name, and title.
        // To avoid hardcoding these values, we use the display name of the template.
        // We'll also set the madlib token to just "NAME" so the client displays the name as-is.
        if (Entity.Template is not GameObjectTemplate gameObjTemplate) {
            return;
        }

        var madlibList = new List<MadlibArg> {
            new MadlibArgT_ByteString() {
                m_madlibArgument = gameObjTemplate.m_displayName,
                m_madlibToken = "NAME"
            },
        };

        _madlibBlock = new MadlibBlock() {
            m_blockToken = "NPC",
            m_madlibs = madlibList
        };
    }

    private void SendWizBang(IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0) {
            return;
        }

        // Collect wizbangs from components that have service options for this player.
        var activeWizBangs = new List<WizBangs>();
        foreach (var component in _serviceComponents) {
            var serviceOptions = component.GetServiceOptions(playerWizard);
            if (serviceOptions.Any()) {
                activeWizBangs.Add(component.WizBang);
            }
        }

        // Get highest priority wizbang for this player, or None to clear.
        var wizBang = WizBangPriority.GetHighestPriorityWizBang(activeWizBangs);

        // Re-send every tick rather than once per change: a single send can be lost
        // while the client is still building the scene, and retail re-pushes each update.
        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = (uint) wizBang,
            GameObjectID = Entity.ActiveGameObject.m_globalID.Full
        };
        playerActor.Tell(wizBangMsg);
    }

    private static IOrderedEnumerable<IServiceComponent> SortComponentsByPriority(IEnumerable<IServiceComponent> components) {
        // Sort by WizBang priority
        var wizBangs = components.Select(c => c.WizBang);
        var prioritySortedWizBangs = WizBangPriority.GetPrioritySortedWizBangs([.. wizBangs]);

        // If priority sorted WizBangs are empty, sort by default.
        if (prioritySortedWizBangs is null || prioritySortedWizBangs.Count <= 0) {
            return components.OrderBy(c => c.WizBang);
        }

        return components.OrderBy(c => prioritySortedWizBangs.IndexOf(c.WizBang));
    }

}