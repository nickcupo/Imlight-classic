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
 * TRANSFER AND LISTENER REGRESSION TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Exercises production effect attachment and actor listener lifetime.
 *
 * USAGE EXAMPLE:
 * Run with the classic server test suite.
 *
 * NOTE:
 * Local actor systems and loopback sockets only.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class EquipmentAttachConcurrencyTests {
    [Fact]
    public void SnapshotSurvivesRemovalClearAndLaterAdditions() {
        var wizard = new Wizard();
        var effects = wizard.GameEffects;
        var first = new NamedEffect { m_internalID = 1 };
        effects.Add(first);
        var snapshot = effects.Snapshot();
        Assert.Same(first, effects.Find(effect => effect.m_internalID == 1));
        Assert.True(effects.Remove(first));
        effects.Add(new NamedEffect { m_internalID = 2 });
        effects.Clear();
        effects.Add(new NamedEffect { m_internalID = 3 });
        Assert.Same(effects, wizard.GameEffects);
        Assert.Same(first, Assert.Single(snapshot));
        snapshot.Clear();
        Assert.Equal(3, Assert.Single(effects.Snapshot()).m_internalID);
    }

    [Fact]
    public async Task AttachCompleteSerializesEffectsWhileOtherServicesMutateTheSameWizard() {
        Configure();
        using var system = ActorSystem.Create("equipment-attach-race", "akka.actor.provider = local");
        using var stop = new CancellationTokenSource();
        var wizard = new Wizard { CharId = 77, PetOwnerBehavior = new ServerPetOwnerBehavior() };
        var packets = Channel.CreateUnbounded<GAME_5_PROTOCOL.MSG_ADDEFFECT>();
        var recorder = system.ActorOf(Props.Create(() => new EffectRecorder(packets)));
        var session = (SessionActor) RuntimeHelpers.GetUninitializedObject(typeof(SessionActor));
        typeof(SessionActor).GetField("<ActorRef>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, recorder);
        var actor = system.ActorOf(Props.Create(() => new AttachHarness(session, wizard)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = 0;
        var writer = Task.Run(() => {
            while (!stop.IsCancellationRequested) {
                for (var i = 0; i < 64; i++) {
                    wizard.GameEffects.Add(new NamedEffect { m_effectNameID = 123, m_internalID = i });
                }
                wizard.GameEffects.Remove(wizard.GameEffects.Find(effect => effect.m_internalID == 0));
                wizard.GameEffects.Clear();
                Interlocked.Increment(ref mutations);
                started.TrySetResult();
            }
        }, TestContext.Current.CancellationToken);
        try {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            for (var i = 0; i < 1000; i++) {
                Assert.True(await actor.Ask<bool>(new AttachAndReport(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            stop.Cancel();
            await writer;
            Assert.True(mutations > 0);
            wizard.GameEffects.Clear();
            var finalEffect = new NamedEffect { m_effectNameID = 321, m_internalID = 99 };
            wizard.GameEffects.Add(finalEffect);
            Assert.True(await actor.Ask<bool>(new AttachAndReport(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await recorder.Ask<bool>(new Drain(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(packets.Reader.TryRead(out var packet));
            Assert.Equal((ulong) wizard.GameObject.m_globalID, packet.GameObjectID);
            while (packets.Reader.TryRead(out var next)) {
                packet = next;
            }
            var serializer = new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None);
            Assert.True(serializer.Serialize(finalEffect, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var expected));
            Assert.Equal((byte[]) expected, (byte[]) packet.EffectData);
        } finally {
            stop.Cancel();
            await writer;
            await system.Terminate();
        }
    }

    internal static void Configure(string? network = null) {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-transfer-tests.log")}\n{network}");
            ConfigurationManager.Initialize(path);
        } finally {
            File.Delete(path);
        }
    }

    private sealed record AttachAndReport;
    private sealed record Drain;

    private sealed class AttachHarness : EquipmentService {
        public AttachHarness(SessionActor session, Wizard wizard) : base(session) {
            typeof(MessageService).GetField("_cachedWizard", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, wizard);
            typeof(MessageService).GetField("_cachedWizardGameObject", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, wizard.GameObject);
        }

        protected override void ConfigureReceivers() {
            Receive<AttachAndReport>(_ => {
                try {
                    MessageHandlerTable.DispatcherFor(typeof(EquipmentService), typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))(
                        this, new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE());
                    Sender.Tell(true);
                } catch (Exception ex) {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
        }
    }

    private sealed class EffectRecorder : ReceiveActor {
        public EffectRecorder(Channel<GAME_5_PROTOCOL.MSG_ADDEFFECT> packets) {
            Receive<GAME_5_PROTOCOL.MSG_ADDEFFECT>(packet => packets.Writer.TryWrite(packet));
            Receive<Drain>(_ => Sender.Tell(true));
        }
    }
}
