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
internal sealed class PlayerOptionIndex {

    private readonly Dictionary<IActorRef, Dictionary<int, IServiceComponent>> _maps = [];

    internal void Set(IActorRef player, IReadOnlyDictionary<int, IServiceComponent> map) => _maps[player] = new Dictionary<int, IServiceComponent>(map);

    internal void Remove(IActorRef player) => _maps.Remove(player);

    /// <summary>The component for <paramref name="index"/> in the list this wizard was sent; <paramref name="fallback"/> when it was sent none.</summary>
    internal bool TryResolve(IActorRef player, int index, IReadOnlyDictionary<int, IServiceComponent> fallback, out IServiceComponent component) {
        var map = _maps.TryGetValue(player, out var own) ? own : fallback;
        if (map.TryGetValue(index, out var found)) {
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

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        _sentTeleportOptions.Remove(playerActor); // CLASSIC
        _playerOptionIndex.Remove(playerActor); // CLASSIC
        _lastInteraction.Remove(playerActor); // CLASSIC
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
                _sentTeleportOptions.Remove(playerActor); // CLASSIC
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

        // If this is a reinteract scenario, send service range messages first.
        if (reinteract == 2) {
            SendLeaveServiceRange(playerActor);
            SendActorServiceOptions(playerActor, reinteract);
        }

        // Route to the component that owns this option index.
        // Using FirstOrDefault by ServiceName is unsafe when multiple components share
        // the same service name (e.g. InteractQuestSelectComponent shadowing WoodenChestComponent).
        // CLASSIC: in the list this wizard was sent, not the one the NPC built for whoever asked last.
        if (!_playerOptionIndex.TryResolve(playerActor, (int) serviceIndex, _optionIndexToComponent, out var serviceComponent)
                && !TryResolveFromCurrentOptions(playerActor, playerCharacter, ref serviceIndex, serviceName, out serviceComponent)) {
            // CLASSIC: the click names an option of a list this wizard no longer has. Send the current list instead of
            // dropping the click silently (the dialog then shows what the NPC offers now).
            Logger.Debug("No component owns service index {0} for NPC {1} with service name {2}; re-sent the options.",
                Logger.Args(serviceIndex, Entity.ActiveGameObject.m_debugName, serviceName));
            SendLeaveServiceRange(playerActor);
            SendActorServiceOptions(playerActor, 2);

            return;
        }

        _lastInteraction[playerActor] = DateTime.UtcNow; // CLASSIC

        // Call the service component's interaction method.
        serviceComponent.OnServiceInteraction(playerActor, playerCharacter, playerObject, serviceIndex);
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
        SendLeaveServiceRange(playerActor);
        SendActorServiceOptions(playerActor, reopen ? 2 : 0);
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
        if (!ClassicQuestEngine.IsActive || !_playersInInteractionRange.Contains(playerActor)
                || !_serviceComponents.OfType<InteractTeleportObjectComponent>().Any()) {
            return;
        }

        var count = _serviceComponents.Sum(component => component.GetServiceOptions(wizard).Count());
        if (_sentTeleportOptions.TryGetValue(playerActor, out var sent) && sent == count) {
            return;
        }

        if (_sentTeleportOptions.ContainsKey(playerActor)) {
            SendLeaveServiceRange(playerActor);
            SendActorServiceOptions(playerActor);
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
            return;
        }

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
        _optionIndexToComponent.Clear();
        var allOptions = new List<ServiceOptionBase>();
        foreach (var component in _serviceComponents) {
            var componentOptions = component.GetServiceOptions(playerCharacter).ToList();
            foreach (var option in componentOptions) {
                _optionIndexToComponent[allOptions.Count] = component;
                allOptions.Add(option);
            }
        }

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