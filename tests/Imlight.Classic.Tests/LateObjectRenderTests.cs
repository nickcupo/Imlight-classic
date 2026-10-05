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
 * LATE OBJECT RENDER
 * ========================================================================
 *
 * PURPOSE:
 * An object that starts while a player is already in the zone (a spawner a
 * trigger or quest started) must come back for that player after they leave
 * its render range and return, as one present at their join does.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter LateObjectRenderTests
 *
 * NOTE:
 * Found on Big Ben (playbot begst s2): Stealthy Stuff's level-5 Travis
 * Pawman, started by MovePawman, vanished for good after the wizard
 * passed a level teleport.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.IO;
using Vector3 = Imcodec.Math.Vector3;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LateObjectRenderTests : IDisposable {

    private const float RenderDistance = 1000f;

    public LateObjectRenderTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap()));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-lateobj-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private sealed class Recorder : ReceiveActor {
        public Recorder(Channel<object> packets) { Receive<object>(p => packets.Writer.TryWrite(p)); }
    }

    private sealed record Move(CoreObject Player, IActorRef Actor, Wizard Wizard);
    private sealed record Join(CoreObject Player, IActorRef Actor, Wizard Wizard);

    // A path NPC at the origin with a fading render (distance-checked), as a started spawner makes it.
    private sealed class NpcEntity : ZoneEntity {
        public NpcEntity(IActorRef zone)
            : base(new WizClientObject { m_globalID = 903UL, m_location = new Vector3(0, 0, 0) },
                new GameObjectTemplate { m_templateID = 39870u, m_objectName = "MB-MUSE3-NPC05_Maintenance", m_behaviors = [] },
                new CoreObjectInfo { m_zoneTag = "73CB7" }, zone, null) {
            AddComponent(typeof(RenderComponent));
        }

        protected override void ConfigureReceivers() {
            Receive<Move>(m => {
                var render = GetComponentOfType<RenderComponent>();
                Arm(render);
                render.OnPlayerMove(m.Player, m.Actor, m.Wizard);
                Sender.Tell(true);
            });
            Receive<Join>(m => {
                var render = GetComponentOfType<RenderComponent>();
                Arm(render);
                render.OnPlayerJoin(m.Player, m.Actor, m.Wizard);
                Sender.Tell(true);
            });
            base.ConfigureReceivers();
        }

        // What OnStart sets from the template's fade animation and the zone's far clip (no zone here).
        private static void Arm(RenderComponent render) {
            typeof(RenderComponent).GetField("_doesDistanceCheck", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(render, true);
            typeof(RenderComponent).GetField("_renderDistance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(render, RenderDistance);
        }
    }

    private static Wizard NewWizard() => new() { CharId = 7, QuestBehavior = new ServerQuestBehavior() };

    // The packets that reached the player within a short wait (a pending read must not swallow a later packet).
    private static async Task<object?> Next(Channel<object> packets) {
        for (var i = 0; i < 20; i++) {
            if (packets.Reader.TryRead(out var packet)) {
                return packet;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }

    [Fact]
    public async Task AnObjectStartedAfterThePlayerJoinedComesBackInRange() {
        using var system = ActorSystem.Create("late-object-render", "akka.actor.provider = local");
        try {
            var packets = Channel.CreateUnbounded<object>();
            var player = system.ActorOf(Props.Create(() => new Recorder(packets)));
            var npc = system.ActorOf(Props.Create(() => new NpcEntity(player)));
            var wizard = NewWizard();
            var body = new CoreObject { m_location = new Vector3(100, 0, 0) };
            var timeout = TimeSpan.FromSeconds(5);
            var cancel = TestContext.Current.CancellationToken;

            // In range at first sight: the start broadcast already showed it, nothing new is sent.
            await npc.Ask<bool>(new Move(body, player, wizard), timeout, cancel);
            Assert.Null(await Next(packets));

            body.m_location = new Vector3(5000, 0, 0); // out of range
            await npc.Ask<bool>(new Move(body, player, wizard), timeout, cancel);
            Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEOBJECT>(await Next(packets));

            body.m_location = new Vector3(100, 0, 0); // back in range: sent again
            await npc.Ask<bool>(new Move(body, player, wizard), timeout, cancel);
            Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(await Next(packets));
        } finally {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AJoinedPlayerIsNotTakenOnTwice() {
        using var system = ActorSystem.Create("late-object-render-joined", "akka.actor.provider = local");
        try {
            var packets = Channel.CreateUnbounded<object>();
            var player = system.ActorOf(Props.Create(() => new Recorder(packets)));
            var npc = system.ActorOf(Props.Create(() => new NpcEntity(player)));
            var wizard = NewWizard();
            var body = new CoreObject { m_location = new Vector3(100, 0, 0) };
            var timeout = TimeSpan.FromSeconds(5);
            var cancel = TestContext.Current.CancellationToken;

            await npc.Ask<bool>(new Join(body, player, wizard), timeout, cancel);
            Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(await Next(packets)); // the join's own send

            await npc.Ask<bool>(new Move(body, player, wizard), timeout, cancel);
            Assert.Null(await Next(packets));

            body.m_location = new Vector3(5000, 0, 0);
            await npc.Ask<bool>(new Move(body, player, wizard), timeout, cancel);
            Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEOBJECT>(await Next(packets));

            body.m_location = new Vector3(100, 0, 0);
            await npc.Ask<bool>(new Move(body, player, wizard), timeout, cancel);
            Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(await Next(packets));
        } finally {
            await system.Terminate();
        }
    }

}
