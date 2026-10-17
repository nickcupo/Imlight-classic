// CLASSIC: in-process test sessions must not share session id 0 with the virtual online players other tests register.
using System;
using System.IO;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Misc;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class InProcessSessionIdTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const ulong Character = 0x7E57_0000_5E55_1D00;

    [Fact]
    public async Task InProcessSessionsHaveDistinctNonZeroIdsAndCloseLeavesVirtualPlayersAlone() {
        EnsureConfiguration();
        using var system = ActorSystem.Create("session-ids-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var socket = system.ActorOf(Props.Empty);
        var first = await system.ActorOf(Props.CreateBy(new Producer(socket)))
            .Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
        var second = await system.ActorOf(Props.CreateBy(new Producer(socket)))
            .Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, first.SessionID);
        Assert.NotEqual(0, second.SessionID);
        Assert.NotEqual(first.SessionID, second.SessionID);

        // What MessageService.CloseSession does: a virtual player (SessionId 0) is not this session's to remove, and
        // with nothing removed the player database is never opened.
        var wasCreated = PlayerDatabase.IsCreated;
        OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer { AccountId = Character, CharacterId = Character });
        try {
            OnlinePlayerCollection.RemoveOnlinePlayer(first.SessionID);
            Assert.NotNull(OnlinePlayerCollection.GetOnlinePlayer(Character));
            Assert.Equal(wasCreated, PlayerDatabase.IsCreated);
        }
        finally { OnlinePlayerCollection.RemoveVirtualOnlinePlayer(Character); }
        await system.Terminate();
    }

    // A session reads [Advanced] settings when it is created. Run alone, nothing has initialised the configuration yet;
    // otherwise leave whatever another test set up.
    private static void EnsureConfiguration() {
        try { ConfigurationManager.GetSetting("Logging.LogLevel"); return; }
        catch (InvalidOperationException) { }
        var path = Path.Combine(Path.GetTempPath(), "w101c-session-ids-" + Guid.NewGuid().ToString("N") + ".ini");
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}session-id-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    private sealed class Producer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
}
