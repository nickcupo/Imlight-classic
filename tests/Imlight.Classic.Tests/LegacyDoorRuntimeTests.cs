using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Reflection;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;
using Imlight.Common;
using Imcodec.ObjectProperty;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imcodec.MessageLayer;
using Imlight.Classic.Quests;
using Imlight.Classic;
using Imlight.Classic.Zones;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LegacyDoorRuntimeTests {
    private static string PrivateFixture(string name) {
        var path = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(path)) {
            if (Environment.GetEnvironmentVariable("W101C_REQUIRE_CLASSIC_DATA") == "1") Assert.Fail($"Required private fixture {name} is unset.");
            Assert.Skip($"Requires owned private fixture {name}; payloads are not committed.");
        }
        return path!;
    }
    private sealed class QuietActor : ReceiveActor { public QuietActor() { Receive<object>(_ => { }); } }
    [Fact] public async Task AttachGenerationRejectsReorderedReadySnapshotAndLeftBeforeFirstSnapshot() {
        using var system = ActorSystem.Create("door-generation", "akka.actor.provider = local");
        try {
            var actor = system.ActorOf(Props.Create(() => new QuietActor()));
            var oldActor = system.ActorOf(Props.Create(() => new QuietActor()));
            var binding = LegacyDoorBindings.ForZone("WizardCity/WC_Streets/WC_Unicorn")[0];
            var feed = new LegacyDoorFeed();
            LegacyDoorSnapshot Snapshot(IActorRef source, long generation) => new() {
                Zone = binding.Zone, ZoneActor = source, AttachGeneration = generation, States = [new(binding, "On")],
            };
            feed.ObserveCurrent(new(binding.Zone, actor, 2));
            Assert.True(feed.Ready(binding.Zone, actor, 2));
            Assert.False(feed.Ready(binding.Zone, oldActor, 1));
            Assert.Empty(feed.Apply(Snapshot(oldActor, 1), binding.Zone, 100, true));
            // Leaving before any state was emitted must still revoke readiness.
            Assert.Empty(feed.Leave(binding.Zone, actor, 2));
            Assert.Empty(feed.Apply(Snapshot(actor, 2), binding.Zone, 100, true));
            feed.ObserveCurrent(new(binding.Zone, actor, 3));
            Assert.True(feed.Ready(binding.Zone, actor, 3));
            Assert.False(feed.Ready(binding.Zone, actor, 2));
            Assert.Empty(feed.Leave(binding.Zone, actor, 2));
            Assert.Empty(feed.Apply(Snapshot(actor, 2), binding.Zone, 100, true));
            Assert.Equal(7, feed.Apply(Snapshot(actor, 3), binding.Zone, 100, true).Count);
            feed.ObserveCurrent(null);
            Assert.False(feed.Ready(binding.Zone, actor, 3));
            Assert.Empty(feed.Apply(Snapshot(actor, 3), binding.Zone, 100, true));
        } finally { await system.Terminate(); }
    }
    [Fact] public async Task LifecycleWaitsForPlayerReplayDeduplicatesAndRejectsStaleInstances() {
        using var system = ActorSystem.Create("legacy-door-lifecycle", "akka.actor.provider = local");
        try {
            var first = system.ActorOf(Props.Create(() => new QuietActor()));
            var next = system.ActorOf(Props.Create(() => new QuietActor()));
            var binding = LegacyDoorBindings.ForZone("WizardCity/WC_Streets/WC_Unicorn")[0];
            var feed = new LegacyDoorFeed(); var peer = new LegacyDoorFeed();
            LegacyDoorSnapshot Snapshot(string state, IActorRef actor, bool replay = false) => new() {
                Zone = binding.Zone, ZoneActor = actor, AttachGeneration = 1, Replay = replay, States = [new(binding, state)],
            };
            Assert.Empty(feed.Apply(Snapshot("Off", first), binding.Zone, 100, true));
            feed.ObserveCurrent(new(binding.Zone, first, 1));
            feed.Ready(binding.Zone, first, 1);
            Assert.Empty(feed.Apply(Snapshot("Off", first), binding.Zone, 100, false));
            Assert.Empty(feed.Apply(Snapshot("Off", first), "Another/Zone", 100, true));
            var baseline = feed.Apply(Snapshot("Off", first), binding.Zone, 100, true);
            Assert.Equal(7, baseline.Count);
            Assert.All(baseline, p => Assert.Equal(100UL, p.GlobalID));
            for (int i = 0; i < 6; i += 2) { Assert.Equal(1, baseline[i].Add); Assert.Equal(1, baseline[i+1].Remove); Assert.Equal(baseline[i].Index, baseline[i+1].Index); }
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "Off"), baseline[^1].Index);
            Assert.Empty(feed.Apply(Snapshot("Off", first), binding.Zone, 100, true));
            var blue = feed.Apply(Snapshot("Quest", first), binding.Zone, 100, true);
            Assert.Equal(2, blue.Count); Assert.Equal(1, blue[0].Remove);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "Off"), blue[0].Index);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "Quest"), blue[1].Index);
            var yellow = feed.Apply(Snapshot("On", first), binding.Zone, 100, true);
            Assert.Equal(2, yellow.Count); Assert.Equal(QuestService.LegacyDoorIndex(binding, "Quest"), yellow[0].Index);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "On"), yellow[1].Index);
            peer.ObserveCurrent(new(binding.Zone, first, 1));
            peer.Ready(binding.Zone, first, 1);
            var other = peer.Apply(Snapshot("Quest", first), binding.Zone, 200, true);
            Assert.Equal(7, other.Count); Assert.All(other, p => Assert.Equal(200UL, p.GlobalID));
            Assert.Empty(feed.Apply(Snapshot("On", first), binding.Zone, 100, true));
            feed.ObserveCurrent(new(binding.Zone, next, 1));
            feed.Ready(binding.Zone, next, 1);
            Assert.Empty(feed.Apply(Snapshot("Off", first), binding.Zone, 100, true));
            var replay = feed.Apply(Snapshot("Quest", next), binding.Zone, 100, true);
            Assert.Equal(8, replay.Count); Assert.Equal(1, replay[0].Remove);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "On"), replay[0].Index);
            Assert.Empty(feed.Leave(binding.Zone, first, 1)); // old instance cannot clear the new scene
            var leave = Assert.Single(feed.Leave(binding.Zone, next, 1));
            Assert.Equal(1, leave.Remove); Assert.Equal(QuestService.LegacyDoorIndex(binding, "Quest"), leave.Index);
            Assert.Empty(feed.Apply(Snapshot("Quest", next), binding.Zone, 100, true));
            feed.ObserveCurrent(new(binding.Zone, next, 1));
            feed.Ready(binding.Zone, next, 1);
            Assert.Equal(7, feed.Apply(Snapshot("On", next), binding.Zone, 100, true).Count);
            Assert.Equal(8, feed.Apply(Snapshot("On", next, true), binding.Zone, 100, true).Count);
            var switched = feed.Apply(Snapshot("Off", next), binding.Zone, 300, true);
            Assert.Equal(7, switched.Count); Assert.All(switched, p => Assert.Equal(300UL, p.GlobalID));
        } finally { await system.Terminate(); }
    }
    [Fact] public void LegacyPacketRoundTripsStockMaskAndWizardOwnership() {
        var binding = new LegacyDoorBindings.Binding("Test/Zone", "Enter_Test", "TestDoor", "Test/House", 0x51000001);
        var packet = QuestService.LegacyDoorPacket(12345, binding);
        var decoded = Assert.IsType<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(packet))!));
        Assert.Equal(12345UL, decoded.GlobalID); Assert.Equal(1, decoded.Add); Assert.Equal(0, decoded.Remove); Assert.Equal(0, decoded.UpdateAll);
        var serializer = new ObjectSerializer(false, SerializerFlags.None);
        Assert.True(serializer.Deserialize<DynaMod>((byte[])decoded.NewMod, 24, out var mod));
        Assert.Equal(1646584070U, mod.GetHash()); Assert.Equal("TestDoor_y", mod.m_clientTag);
        Assert.Equal(binding.Index + 2, mod.m_index); Assert.Equal(12345UL, mod.m_originator.Full);
        Assert.IsType<ClientDynaModBehavior>(BehaviorCache.AllocateBehavior(0x26FCE31F));
    }
    [Fact] public void AliasIndicesAreDisjointAndProtectedWithoutBlockingOrdinaryDynamods() {
        var bindings = LegacyDoorBindings.All;
        var indices = bindings.SelectMany(b => new[] { "Off", "Quest", "On" }.Select(s => QuestService.LegacyDoorIndex(b, s))).ToArray();
        Assert.Equal(indices.Length, indices.Distinct().Count()); Assert.All(indices, i => Assert.True(i >= 0x51000000));
        foreach (var binding in bindings) foreach (var state in new[] { "Off", "Quest", "On" }) {
            Assert.True(LegacyDoorBindings.IsAuthoritativeAlias(QuestService.LegacyDoorAlias(binding, state)));
            var remove = QuestService.LegacyDoorRemove(12345, binding, state);
            Assert.Equal(1, remove.Remove); Assert.Equal(0, remove.Add); Assert.Equal(QuestService.LegacyDoorIndex(binding, state), remove.Index);
        }
        Assert.False(LegacyDoorBindings.IsAuthoritativeAlias("SomeLever")); Assert.False(LegacyDoorBindings.IsAuthoritativeAlias("WC_Unicorn_H02"));
    }
    [Fact] public void OwnedPlayerTemplateActuallyAttachesBehaviorAndSerializesWizardId() {
        var file = PrivateFixture("W101C_DOOR_FIXTURE_PLAYER");
        if (string.IsNullOrEmpty(file)) Assert.Skip("Requires private owned template 1 extraction.");
        using var data = JsonDocument.Parse(File.ReadAllText(file!));
        Assert.Equal(1U, data.RootElement.GetProperty("m_templateID").GetUInt32());
        var templates = data.RootElement.GetProperty("m_behaviors").EnumerateArray().Select(t => new BehaviorTemplate { m_behaviorName = t.GetProperty("m_behaviorName").GetString()! }).ToList();
        var player = CoreObjectFactory.InitializeCoreObjectBehaviors(new WizClientObject { m_globalID = 12345UL }, new WizGameObjectTemplate { m_templateID = 1, m_behaviors = templates });
        var attached = Assert.Single(player.m_inactiveBehaviors.OfType<ClientDynaModBehavior>());
        // Isolate the actual attached provider for the wire fixture: unrelated player
        // providers require inventory/avatar/game-stat initialization by WizardObjectLoader.
        player.m_inactiveBehaviors = [attached];
        var serializer = new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None);
        Assert.True(serializer.Serialize(player, 28, out var bytes));
        Assert.True(serializer.Deserialize<WizClientObject>((byte[])bytes, 28, out var outgoing));
        Assert.Equal(12345UL, outgoing.m_globalID.Full);
        Assert.Single(outgoing.m_inactiveBehaviors.OfType<ClientDynaModBehavior>());
        Assert.Equal(outgoing.m_globalID.Full, QuestService.LegacyDoorPacket(outgoing.m_globalID, new("Test", "Enter", "Door", "House", 0x51000000)).GlobalID);
    }
    [Fact] public void EveryRegisteredDoorHasActualPrivateSceneVolumeAndResolvedRouteEvidence() {
        var fixture = PrivateFixture("W101C_DOOR_FIXTURE_CENSUS");
        var overlay = PrivateFixture("W101C_DOOR_FIXTURE_OVERLAY");
        if (string.IsNullOrEmpty(fixture) || string.IsNullOrEmpty(overlay)) Assert.Skip("Requires owned scene/volume/quest and travel evidence; no payloads are committed.");
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };
        using var proof = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture!, "positive-bindings.json")), options);
        var registered = LegacyDoorBindings.All;
        Assert.Equal(proof.RootElement.EnumerateArray().Select(r => r.GetProperty("tag").GetString()).OrderBy(t => t), registered.Select(b => b.Tag).OrderBy(t => t));
        foreach (var binding in registered) {
            var record = Assert.Single(proof.RootElement.EnumerateArray(), r => r.GetProperty("tag").GetString() == binding.Tag);
            var sceneFile = Path.Combine(fixture!, "scenes", Path.GetFileNameWithoutExtension(record.GetProperty("wad").GetString()!) + "__" + record.GetProperty("scene_entry").GetString());
            Assert.Equal(record.GetProperty("scene_sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sceneFile))).ToLowerInvariant());
            Assert.Equal(binding.Zone, record.GetProperty("zone").GetString());
            Assert.Equal(binding.Event, record.GetProperty("event").GetString());
            Assert.Equal(binding.Destination, record.GetProperty("destination").GetString());
            var nodes = record.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.Equal(3, nodes.Length);
            Assert.Equal(new[] { binding.Tag + "_off", binding.Tag + "_quest", binding.Tag + "_on" }, nodes.Select(n => n.GetProperty("Name").GetString()));
            if (record.GetProperty("status").GetString() == "positive") {
                var entrance = Assert.Single(record.GetProperty("contained_entrances").EnumerateArray());
                var radius = entrance.GetProperty("radius").GetDouble();
                var center = entrance.GetProperty("center").EnumerateArray().Select(n => n.GetDouble()).ToArray();
                foreach (var node in nodes) {
                    var pos = node.GetProperty("position").EnumerateArray().Select(n => n.GetDouble()).ToArray();
                    Assert.True(Math.Sqrt(pos.Zip(center, (a,b) => (a-b)*(a-b)).Sum()) <= radius);
                }
            } else if (record.GetProperty("status").GetString() == "xy-physical-positive") {
                var physicalProof = record.GetProperty("physical_proof");
                Assert.Equal(1, physicalProof.GetProperty("containing_volume_count").GetInt32());
                Assert.All(physicalProof.GetProperty("vertical_offsets").EnumerateArray(), v => Assert.True(double.IsFinite(v.GetDouble()) && v.GetDouble() > 0));
                var allDestinations = record.GetProperty("routes").EnumerateArray().Select(t => t.GetProperty("destination").GetString()).Distinct().ToArray();
                Assert.Equal(allDestinations.OrderBy(d => d), binding.Destinations.OrderBy(d => d));
            } else if (record.GetProperty("status").GetString() == "xy-identity-positive") {
                Assert.Equal(binding.Tag, binding.Destination.Split('/').Last());
                var matching = record.GetProperty("resolved_entrances").EnumerateArray().Where(e =>
                    nodes.All(n => Math.Sqrt(n.GetProperty("position").EnumerateArray().Take(2).Select(v => v.GetDouble())
                        .Zip(e.GetProperty("center").EnumerateArray().Take(2).Select(v => v.GetDouble()), (a,b) => (a-b)*(a-b)).Sum()) <= e.GetProperty("radius").GetDouble())).ToArray();
                var entrance = Assert.Single(matching);
                Assert.Equal(record.GetProperty("volume").GetString(), entrance.GetProperty("volume").GetString());
            } else {
                using var authored = JsonDocument.Parse(File.ReadAllText(record.GetProperty("quest_source").GetString()!), options);
                var goal = Assert.Single(authored.RootElement.GetProperty("m_goals").EnumerateArray(), g => g.GetProperty("m_goalName").GetString() == record.GetProperty("goal").GetString());
                Assert.Contains(binding.Tag, goal.GetProperty("m_clientTags").EnumerateArray().Select(t => t.GetString()));
                if (record.GetProperty("status").GetString() == "authored-waypoint-bounty-positive") {
                    Assert.Contains("Rattlebones", goal.GetProperty("m_npcAdjectives").EnumerateArray().Select(t => t.GetString()));
                    var waypoint = Assert.Single(authored.RootElement.GetProperty("m_goals").EnumerateArray(), g =>
                        g.TryGetProperty("m_zoneEntry", out var entry) && entry.GetBoolean() &&
                        g.TryGetProperty("m_destinationZone", out var dest) && dest.GetString() == binding.Destination);
                    Assert.Equal(binding.Destination, waypoint.GetProperty("m_zoneTag").GetString());
                    Assert.Equal(record.GetProperty("waypoint_goal").GetString(), waypoint.GetProperty("m_goalName").GetString());
                    var footprint = Assert.Single(record.GetProperty("resolved_entrances").EnumerateArray(), e =>
                        nodes.All(n => Math.Sqrt(n.GetProperty("position").EnumerateArray().Take(2).Select(v => v.GetDouble())
                            .Zip(e.GetProperty("center").EnumerateArray().Take(2).Select(v => v.GetDouble()), (a,b) => (a-b)*(a-b)).Sum()) <= e.GetProperty("radius").GetDouble()));
                    Assert.Equal(record.GetProperty("volume").GetString(), footprint.GetProperty("volume").GetString());
                } else Assert.Equal(binding.Destination, goal.GetProperty("m_destinationZone").GetString());
            }
            var physical = Assert.Single(record.GetProperty("resolved_entrances").EnumerateArray(), e =>
                nodes.All(n => Math.Sqrt(n.GetProperty("position").EnumerateArray().Take(2).Select(v => v.GetDouble())
                    .Zip(e.GetProperty("center").EnumerateArray().Take(2).Select(v => v.GetDouble()), (a,b) => (a-b)*(a-b)).Sum()) <= e.GetProperty("radius").GetDouble()));
            Assert.Equal(record.GetProperty("volume").GetString(), physical.GetProperty("volume").GetString());
            var wad = record.GetProperty("wad").GetString()!;
            using var volumes = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture!, "data", wad + ".volumes.xml.json")), options);
            var volume = Assert.Single(volumes.RootElement.GetProperty("m_volumes").EnumerateArray(), v => v.GetProperty("m_volumeName").GetString() == record.GetProperty("volume").GetString());
            Assert.Contains(binding.Event, volume.GetProperty("m_enterEvents").EnumerateArray().Select(e => e.GetString()));
            using var triggers = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture!, "data", wad + ".triggers.xml.json")), options);
            using var travel = JsonDocument.Parse(File.ReadAllText(record.GetProperty("resolved_travel").GetString()!), options);
            Assert.Contains(triggers.RootElement.GetProperty("m_triggers").EnumerateArray(), t =>
                t.ValueKind == JsonValueKind.Object && t.GetProperty("m_fireEvents").ValueKind == JsonValueKind.Array &&
                t.GetProperty("m_fireEvents").EnumerateArray().Any(e => e.GetString() == binding.Event) &&
                travel.RootElement.GetProperty("Teleports").EnumerateArray().Any(route => route.GetProperty("TriggerName").GetString() == t.GetProperty("m_triggerName").GetString()
                    && route.GetProperty("Teleport").GetProperty("m_destinationZone").GetString() == binding.Destination));
        }
    }
    private static ClassicRules OwnedRules(string profile = "late-2009") {
        var data = Directory.GetParent(PrivateFixture("W101C_DOOR_FIXTURE_OVERLAY"))!.FullName;
        return new ClassicRules(ClassicProfileLoader.Load(Path.Combine(data, "profiles"), profile), ZoneWorldMapLoader.Load(Path.Combine(data, "zones", "worlds.yaml")));
    }
    [Fact] public void OwnedPost2009EntranceStaysDarkDespiteArmingAndEmptyRequirements() {
        var fixture = PrivateFixture("W101C_DOOR_FIXTURE_CENSUS");
        using var census = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "census.json")));
        var door = Assert.Single(census.RootElement.GetProperty("census").EnumerateArray(), r => r.GetProperty("tag").GetString() == "WC_Cyclops_H09");
        var entry = Assert.Single(door.GetProperty("resolved_entrances").EnumerateArray(), e => e.GetProperty("events").EnumerateArray().Any(ev => ev.GetString() == "Enter_TeleportVol_Teleport location"));
        var route = Assert.Single(entry.GetProperty("routes").GetProperty("Enter_TeleportVol_Teleport location").EnumerateArray());
        var dest = route.GetProperty("destination").GetString()!;
        Assert.Equal("Aquila/AQ_Z00_Hub", dest);
        Assert.Equal(JsonValueKind.Null, route.GetProperty("requirements").GetProperty("m_requirements").ValueKind);
        var rules = OwnedRules();
        Assert.False(rules.IsZoneAllowed(dest).Allowed);
        Assert.Equal("Off", DoorLightRules.State([DoorLightRules.ApplyZonePolicy(new(dest, true, true), rules)], [dest]));
    }
    [Fact] public void FirstClaimedClosedZoneDoesNotFallThroughToLaterOpenTeleport() {
        var rules = OwnedRules();
        var closed = DoorLightRules.ApplyZonePolicy(new("Aquila/AQ_Z00_Hub", true, true), rules);
        var open = DoorLightRules.ApplyZonePolicy(new("WizardCity/WC_Hub", true, true), rules);
        Assert.True(closed.RequirementsMet); Assert.False(closed.ZoneAllowed); Assert.True(open.ZoneAllowed);
        var dispatch = new TriggerEventDispatch<int, string>();
        var actual = dispatch.Dispatch(new[] { 0, 1 }, i => i, "Enter", "player", _ => true, _ => true, _ => true).ToArray();
        Assert.False(actual[0].SuppressTeleport); Assert.True(actual[1].SuppressTeleport);
        Assert.Equal("Off", DoorLightRules.State([closed, open], [open.Destination]));
        Assert.Equal("Quest", DoorLightRules.State([closed with { RequirementsMet = false }, open], [open.Destination]));
    }
    [Fact] public void OwnedClassicTwoStateMappingsCannotDisarmAndHaveNoEntryRequirementsUnderReleaseProfiles() {
        var fixture = PrivateFixture("W101C_DOOR_FIXTURE_CENSUS");
        using var approved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "always-eligible-two-state.json")));
        var tags = approved.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.Equal(14, tags.Length);
        foreach (var tag in tags) {
        var binding = Assert.Single(LegacyDoorBindings.All, b => b.Tag == tag);
        using var triggers = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "data", binding.Zone.Replace("/", "-") + ".wad.triggers.xml.json")));
        var trigger = triggers.RootElement.GetProperty("m_triggers").EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object && t.GetProperty("m_fireEvents").EnumerateArray().Any(e => e.GetString() == binding.Event)).First();
        var activations = trigger.GetProperty("m_activateEvents").EnumerateArray().Select(e => e.GetString()).ToArray();
        var deactivations = trigger.GetProperty("m_deactivateEvents").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "StartZone" }, activations); Assert.Empty(deactivations);
        Assert.Equal(uint.MaxValue, trigger.GetProperty("m_triggerMax").GetUInt32());
        Assert.Equal(0, trigger.GetProperty("m_cooldown").GetDouble()); Assert.Equal(0, trigger.GetProperty("m_cooldownRand").GetDouble());
        Assert.Equal(JsonValueKind.Null, trigger.GetProperty("m_requirements").GetProperty("m_requirements").ValueKind);
        var activation = new TriggerActivation<string>(activations, deactivations);
        Assert.False(activation.CanDisarm); Assert.True(activation.IsArmed("player"));
        using var travel = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "resolved-travel", binding.Zone.Replace("/", "-") + ".json")));
        var route = Assert.Single(travel.RootElement.GetProperty("Teleports").EnumerateArray(), t => t.GetProperty("TriggerName").GetString() == trigger.GetProperty("m_triggerName").GetString()).GetProperty("Teleport");
        Assert.Equal(binding.Destination, route.GetProperty("m_destinationZone").GetString());
        Assert.True(!route.TryGetProperty("m_requirements", out var requirements) || requirements.ValueKind == JsonValueKind.Null);
        foreach (var profile in new[] { "late-2009", "arc1-2009h1" }) {
            var eligible = DoorLightRules.ApplyZonePolicy(new(binding.Destination, true, true), OwnedRules(profile));
            Assert.True(eligible.RequirementsMet); Assert.True(eligible.ZoneAllowed);
            Assert.Equal("On", DoorLightRules.State([eligible], []));
            Assert.Equal("Quest", DoorLightRules.State([eligible], [binding.Destination]));
        }
        }
    }
    [Fact] public void AuthoredBonesLampTagColorsTheWinningDestinationAlternative() {
        var bindings = LegacyDoorBindings.ForZone("Marleybone/MB_Hub");
        var binding = Assert.Single(bindings, b => b.Tag == "MB_BonesHouse_Door");
        Assert.Single(binding.AlternateDestinations);
        var destinations = ZoneTriggerSupervisor.RelevantDoorDestinations([new BountyGoalTemplate { m_clientTags = [binding.Tag] }], bindings).ToArray();
        foreach (var destination in binding.Destinations)
            Assert.Equal("Quest", DoorLightRules.State([new(destination, true, true)], destinations));
        Assert.Equal("Off", DoorLightRules.State(binding.Destinations.Select(d => new DoorLightRules.Route(d, true, false)).ToArray(), destinations));
    }
    [Fact] public void PrivatePatchRecipeExactlyMatchesCanonicalRegistryAndReservedAliasIndices() {
        var fixture = PrivateFixture("W101C_DOOR_FIXTURE_CENSUS");
        using var recipe = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "patch-recipe.json")));
        var rows = recipe.RootElement.GetProperty("zones").EnumerateArray().SelectMany(z => z.GetProperty("bindings").EnumerateArray()
            .Select(b => (Zone: z.GetProperty("zone").GetString(), Binding: b))).ToArray();
        Assert.Equal(LegacyDoorBindings.All.Count, rows.Length);
        foreach (var binding in LegacyDoorBindings.All) {
            var row = Assert.Single(rows, r => r.Zone == binding.Zone && r.Binding.GetProperty("canonical_server_tag").GetString() == binding.Tag).Binding;
            Assert.Equal(binding.Tag, row.GetProperty("input_tag").GetString());
            Assert.Equal(binding.Event, row.GetProperty("event").GetString());
            Assert.Equal(binding.Destination, row.GetProperty("destination").GetString());
            Assert.Equal(binding.AlternateDestinations, row.GetProperty("alternate_destinations").EnumerateArray().Select(d => d.GetString()));
            Assert.Equal(binding.Index, row.GetProperty("base_index").GetInt32());
            foreach (var (color, state) in new[] { ("dark", "Off"), ("blue", "Quest"), ("yellow", "On") }) {
                var alias = row.GetProperty("aliases").GetProperty(color);
                Assert.Equal(QuestService.LegacyDoorAlias(binding, state), alias.GetProperty("tag").GetString());
                Assert.Equal(QuestService.LegacyDoorAlias(binding, state) + "_on", alias.GetProperty("node").GetString());
                Assert.Equal(QuestService.LegacyDoorIndex(binding, state), alias.GetProperty("index").GetInt32());
            }
        }
    }
    [Fact] public void PrivateRouteCensusMatchesProductionCacheAndOrderedOverlayMerge() {
        var fixture = PrivateFixture("W101C_DOOR_FIXTURE_CENSUS");
        using var resolution = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "route-resolution.json")));
        Assert.Empty(resolution.RootElement.GetProperty("errors").EnumerateArray());
        try { ConfigurationManager.GetSetting("Logging.LogLevel"); }
        catch (InvalidOperationException) {
            var config = Path.GetTempFileName();
            try {
                File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-door-tests.log")}\n");
                ConfigurationManager.Initialize(config);
            } finally { File.Delete(config); }
        }
        var loaded = new ConcurrentDictionary<string, WizardZoneData>(StringComparer.Ordinal);
        var method = typeof(SpiralDB).GetMethod("LoadZoneData", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(method);
        foreach (var root in resolution.RootElement.GetProperty("roots").EnumerateArray())
            method.Invoke(null, [Directory.GetParent(root.GetString()!)!.FullName, loaded]);
        using var proof = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "positive-bindings.json")));
        foreach (var record in proof.RootElement.EnumerateArray()) {
            var actual = loaded[record.GetProperty("zone").GetString()!];
            using var expected = JsonDocument.Parse(File.ReadAllText(record.GetProperty("resolved_travel").GetString()!));
            var rows = expected.RootElement.GetProperty("Teleports").EnumerateArray().ToArray();
            Assert.Equal(rows.Select(t => t.GetProperty("TriggerName").GetString()), actual.Teleports.Select(t => t.TriggerName));
            Assert.Equal(rows.Select(t => t.GetProperty("Teleport").GetProperty("m_destinationZone").GetString()), actual.Teleports.Select(t => (string)t.Teleport.m_destinationZone));
            Assert.Contains(actual.Teleports, t => t.Teleport.m_destinationZone == record.GetProperty("destination").GetString());
        }
    }
    [Fact] public async Task OwnedOryanEligibilityAndQuestPresentationReplayThroughAttachedPlayerFeed() {
        var overlay = PrivateFixture("W101C_DOOR_FIXTURE_OVERLAY");
        using var quest = JsonDocument.Parse(File.ReadAllText(Path.Combine(overlay, "QuestTemplates", "WC-ST01-C03-002.json")), new JsonDocumentOptions { AllowTrailingCommas = true });
        var binding = Assert.Single(LegacyDoorBindings.ForZone("WizardCity/WC_Streets/WC_Unicorn"), b => b.Tag == "WC_Unicorn_H02");
        var goals = quest.RootElement.GetProperty("m_goals").EnumerateArray().Select(g => g.GetProperty("m_destinationZone").GetString() ?? "").ToArray();
        using var system = ActorSystem.Create("door-source-replay", "akka.actor.provider = local");
        try {
            var actor = system.ActorOf(Props.Create(() => new QuietActor()));
            var feed = new LegacyDoorFeed();
            feed.ObserveCurrent(new(binding.Zone, actor, 10));
            feed.Ready(binding.Zone, actor, 10);
            LegacyDoorSnapshot Snapshot(bool eligible, string[] active, long generation = 10) => new() {
                Zone = binding.Zone, ZoneActor = actor, AttachGeneration = generation,
                States = [new(binding, DoorLightRules.State([new(binding.Destination, true, eligible)], active))],
            };
            var locked = feed.Apply(Snapshot(false, goals), binding.Zone, 12345, true);
            Assert.Equal(7, locked.Count);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "Off"), locked[^1].Index);
            var questPackets = feed.Apply(Snapshot(true, goals), binding.Zone, 12345, true);
            Assert.Equal(2, questPackets.Count);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "Off"), questPackets[0].Index);
            var wire = Assert.IsType<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(questPackets[1]))!));
            Assert.True(new ObjectSerializer(false, SerializerFlags.None).Deserialize<DynaMod>((byte[])wire.NewMod, 24, out var mod));
            Assert.Equal(binding.Tag + "_b", mod.m_clientTag);
            Assert.Equal(12345UL, wire.GlobalID);
            var completed = feed.Apply(Snapshot(true, []), binding.Zone, 12345, true);
            Assert.Equal(2, completed.Count);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "Quest"), completed[0].Index);
            Assert.Equal(QuestService.LegacyDoorIndex(binding, "On"), completed[1].Index);
            // Same zone actor, fresh attachment: the old generation is ineligible,
            // the current source-derived state rebuilds every alias baseline.
            feed.ObserveCurrent(new(binding.Zone, actor, 11));
            Assert.False(feed.Ready(binding.Zone, actor, 10));
            Assert.True(feed.Ready(binding.Zone, actor, 11));
            Assert.Empty(feed.Apply(Snapshot(false, goals), binding.Zone, 12345, true));
            Assert.Equal(8, feed.Apply(Snapshot(true, goals, 11), binding.Zone, 12345, true).Count);
        } finally { await system.Terminate(); }
    }
    [Fact] public void OwnedRattlebonesBountyTagKeepsDoorBlueWithoutDestinationAndNeverOverridesBlockedEntry() {
        var fixture = PrivateFixture("W101C_DOOR_FIXTURE_CENSUS");
        using var proof = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "positive-bindings.json")));
        var record = Assert.Single(proof.RootElement.EnumerateArray(), r => r.GetProperty("tag").GetString() == "WC_Unicorn_T2");
        using var quest = JsonDocument.Parse(File.ReadAllText(record.GetProperty("quest_source").GetString()!));
        var bounty = Assert.Single(quest.RootElement.GetProperty("m_goals").EnumerateArray(), g => g.GetProperty("m_goalName").GetString() == record.GetProperty("goal").GetString());
        var waypoint = Assert.Single(quest.RootElement.GetProperty("m_goals").EnumerateArray(), g => g.GetProperty("m_goalName").GetString() == record.GetProperty("waypoint_goal").GetString());
        Assert.Equal("", bounty.GetProperty("m_destinationZone").GetString());
        var bindings = LegacyDoorBindings.ForZone(record.GetProperty("zone").GetString()!);
        var binding = Assert.Single(bindings, b => b.Tag == record.GetProperty("tag").GetString());
        var active = new BountyGoalTemplate {
            m_destinationZone = bounty.GetProperty("m_destinationZone").GetString()!,
            m_clientTags = bounty.GetProperty("m_clientTags").EnumerateArray().Select(t => t.GetString()!).ToList(),
        };
        var destinations = ZoneTriggerSupervisor.RelevantDoorDestinations([active], bindings).ToArray();
        Assert.Contains(binding.Destination, destinations);
        Assert.Equal("Quest", DoorLightRules.State([new(binding.Destination, true, true)], destinations));
        Assert.Equal("Off", DoorLightRules.State([new(binding.Destination, true, false)], destinations));
        Assert.Equal("Off", DoorLightRules.State([new(binding.Destination, false, true)], destinations));
        Assert.Equal("On", DoorLightRules.State([new(binding.Destination, true, true)], ZoneTriggerSupervisor.RelevantDoorDestinations([], bindings)));
        // Exact tags only: another actor's dialogue tag cannot color a door.
        active.m_clientTags = active.m_clientTags.Where(t => t != binding.Tag).ToList();
        Assert.Empty(ZoneTriggerSupervisor.RelevantDoorDestinations([active], bindings));
        // Sparse waypoint projection exercises its authored zone-entry field.
        var typedWaypoint = new WaypointGoalTemplate {
            m_zoneEntry = waypoint.GetProperty("m_zoneEntry").GetBoolean(), m_zoneTag = waypoint.GetProperty("m_zoneTag").GetString()!, m_destinationZone = "",
        };
        Assert.Contains(binding.Destination, ZoneTriggerSupervisor.RelevantDoorDestinations([typedWaypoint], bindings));
        typedWaypoint.m_zoneEntry = false;
        Assert.Empty(ZoneTriggerSupervisor.RelevantDoorDestinations([typedWaypoint], bindings));
    }
    [Fact] public void OwnedOryanFixtureUsesTheSameFullDestinationAsResolvedRoute() {
        var overlay = PrivateFixture("W101C_DOOR_FIXTURE_OVERLAY");
        if (string.IsNullOrEmpty(overlay)) Assert.Skip("Requires owned private quest and travel fixtures; no game payload is committed.");
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };
        using var quest = JsonDocument.Parse(File.ReadAllText(Path.Combine(overlay!, "QuestTemplates", "WC-ST01-C03-002.json")), options);
        using var travel = JsonDocument.Parse(File.ReadAllText(Path.Combine(overlay!, "ZoneTransfer", "WizardCity-WC_Streets-WC_Unicorn.json")), options);
        var route = travel.RootElement.GetProperty("Teleports").EnumerateArray().Single(t => t.GetProperty("TriggerName").GetString() == "House 1 TeleTrigger").GetProperty("Teleport").GetProperty("m_destinationZone").GetString()!;
        var destinations = quest.RootElement.GetProperty("m_goals").EnumerateArray().Select(g => g.GetProperty("m_destinationZone").GetString() ?? "").ToArray();
        var binding = Assert.Single(LegacyDoorBindings.ForZone(travel.RootElement.GetProperty("ZoneName").GetString()!).Where(b => b.Tag == "WC_Unicorn_H02"));
        Assert.Equal(binding.Destination, route);
        Assert.Contains(route, destinations);
        Assert.Equal("Quest", DoorLightRules.State([new(route, true, true)], destinations));
        Assert.Equal("Off", DoorLightRules.State([new(route, true, false)], destinations));
    }
}
