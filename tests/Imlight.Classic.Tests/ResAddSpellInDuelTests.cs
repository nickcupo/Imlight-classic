using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Results.Handlers;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// 2026-10-17: the tutorial duel's "player damage 1" carries two ResAddSpell results beside the ResDrawHand ones that
/// put its two cards in the hand. In a duel ResAddSpell made them session temporary spells (dealt into every later duel
/// until relog) and sent a MSG_COMBATHAND of the temporary-spell list with deck counters 0/0, which r806919 shows as
/// the hand. In a duel it now does neither; outside one it is unchanged.
/// </summary>
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ResAddSpellInDuelTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public ResAddSpellInDuelTests() {
        var config = System.IO.Path.GetTempFileName();
        try {
            System.IO.File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={System.IO.Path.Combine(System.IO.Path.GetTempPath(), "imlight-res-add-spell.log")}\n");
            Imlight.Common.ConfigurationManager.Initialize(config);
        }
        finally { System.IO.File.Delete(config); }
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(Imlight.CoreLib.Shared.Resources.CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache = cache;
        cache[Spell] = new SpellTemplate { m_name = "res-add-spell-fixture", m_sMagicSchoolName = "Fire", m_accuracy = 100 };
    }

    private const uint Spell = uint.MaxValue - 611;
    private readonly IDictionary<ulong, CoreTemplate> _cache;

    public void Dispose() => _cache.Remove(Spell);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InADuelTheCardIsNeitherASessionSpellNorAHandMessage(bool inDuel) {
        using var system = ActorSystem.Create("res-add-spell-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var sink = system.ActorOf(Props.Create(() => new PacketSink()));
        var wizard = new Wizard { CharId = 900_001, SpellbookBehavior = new ServerWizSpellbookBehavior() };
        wizard.IsInDuel = inDuel;
        var before = wizard.SpellbookBehavior.TemporarySpells.Count;
        var player = new CoreObject { m_globalID = 77 };
        ActiveWizardDirectory.SetWizard(sink, wizard);
        ActiveWizardDirectory.SetGameObject(sink, player);
        try {
            var handler = system.ActorOf(Props.CreateBy(new HandlerProducer()));
            var result = new ResAddSpell { m_templateID = Spell };
            Assert.True(await handler.Ask<bool>(new Step(new Context(sink, player, result), result), Timeout,
                TestContext.Current.CancellationToken));
            var packets = await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken);
            if (inDuel) {
                Assert.Empty(packets);
                Assert.Equal(before, wizard.SpellbookBehavior.TemporarySpells.Count);
            }
            else {
                Assert.IsType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>(Assert.Single(packets));
            }
        }
        finally {
            ActiveWizardDirectory.Remove(sink);
            await system.Terminate();
        }
    }

    private sealed record Snapshot;
    private sealed record Step(IResultContext Context, Result Result);

    private sealed class Context(IActorRef player, CoreObject supplied, Result result) : IResultContext {
        public IEnumerable<Result> GetResults() => [result];
        public IActorRef GetZoneActor() => ActorRefs.Nobody;
        public IActorRef GetPlayerRef() => player;
        public CoreObject GetPlayerObj() => supplied;
        public IActorRef GetReplyTo() => ActorRefs.Nobody;
    }

    private sealed class PacketSink : ReceiveActor {
        public PacketSink() {
            var packets = new List<object>();
            Receive<Snapshot>(_ => Sender.Tell(packets.ToArray()));
            ReceiveAny(packets.Add);
        }
    }

    private sealed class HandlerProducer : IIndirectActorProducer {
        public System.Type ActorType => typeof(ResAddSpellHandler);
        public ActorBase Produce() {
            var actual = new ResAddSpellHandler();
            bool Dispatch(object message) {
                if (message is not Step step) return true;
                var sender = (IActorRef) typeof(ActorBase).GetProperty("Sender", Private | BindingFlags.Public)!.GetValue(actual)!;
                try {
                    IResultHandler handler = actual;
                    handler.Initialize(step.Context, step.Result);
                    sender.Tell(handler.Execute(step.Context));
                }
                catch (Exception e) { sender.Tell(new Status.Failure(e)); }
                return true;
            }
            typeof(ActorBase).GetMethod("Become", Private, null, [typeof(Receive)], null)!.Invoke(actual, [new Receive(Dispatch)]);
            return actual;
        }
        public void Release(ActorBase actor) { }
    }
}
