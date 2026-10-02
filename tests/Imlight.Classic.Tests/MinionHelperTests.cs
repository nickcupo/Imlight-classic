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
 * MINION HELPER TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: pairing codes and tokens, the helper's line protocol, and the hub
 * that joins a helper to its account's game session.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Akka.Actor;
using Imlight.CoreLib.Classic.MinionHelper;
using Xunit;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.Classic.Tests;

public sealed class MinionHelperTests : IDisposable {
    private readonly ActorSystem _system = ActorSystem.Create("minion-helper-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
    private readonly MinionHelperHub _hub = new();
    private readonly MemoryMinionHelperTokenStore _store = new();
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly MinionHelperPairing _pairing;

    public MinionHelperTests() => _pairing = new MinionHelperPairing(_store, () => _now);

    public void Dispose() => _system.Terminate().GetAwaiter().GetResult();

    [Fact]
    public void ACodeWorksOnceForItsOwnAccountAndTheTokenLastsAcrossRestarts() {
        var code = _pairing.IssueCode(77);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.True(_pairing.TryRedeem(code[..3] + " " + code[3..], out var token, out var account));
        Assert.Equal(77UL, account);
        Assert.False(_pairing.TryRedeem(code, out _, out _));

        // A new server process (new pairing state) still knows the token from the store, by hash only.
        var restarted = new MinionHelperPairing(_store, () => _now);
        Assert.True(restarted.TryResolve(token, out var again));
        Assert.Equal(77UL, again);
        Assert.False(restarted.TryResolve(token + "x", out _));
        Assert.False(_store.TryLoad(token, out _));
    }

    [Fact]
    public void CodesExpireAfterFiveMinutesAndANewCodeReplacesTheOld() {
        var first = _pairing.IssueCode(5);
        var second = _pairing.IssueCode(5);
        Assert.False(_pairing.TryRedeem(first, out _, out _));
        _now += MinionHelperPairing.CodeLifetime + TimeSpan.FromSeconds(1);
        Assert.False(_pairing.TryRedeem(second, out _, out _));
        Assert.Null(MinionHelperPairing.Normalize("12345"));
        Assert.Null(MinionHelperPairing.Normalize("12a456"));
        Assert.Equal("123456", MinionHelperPairing.Normalize("123-456"));
    }

    [Fact]
    public void AHelperMustPairOrShowATokenBeforeAnythingElse() {
        var (session, lines) = NewSession();
        Assert.True(session.HandleLine("""{"op":"ping"}"""));
        Assert.Equal("pong", Op(lines.Last()));
        Assert.False(session.HandleLine("""{"op":"order","move":"pass"}"""));
        Assert.False(session.HandleLine("""{"op":"hello","token":"nope"}"""));
        Assert.False(session.HandleLine("not json"));
    }

    [Fact]
    public void FiveWrongCodesCloseTheConnection() {
        var (session, _) = NewSession();
        for (var i = 1; i < MinionHelperSession.MaxBadCodes; i++) Assert.True(session.HandleLine("""{"op":"pair","code":"000000"}"""));
        Assert.False(session.HandleLine("""{"op":"pair","code":"000000"}"""));
    }

    [Fact]
    public void PairingLinksTheHelperToTheAccountsSessionAndOrdersReachIt() {
        var inbox = new ConcurrentQueue<object>();
        var sessionActor = _system.ActorOf(Props.Create(() => new Recorder(inbox)));
        _hub.BindSession(9, sessionActor);

        var (helper, lines) = NewSession();
        var code = _pairing.IssueCode(9);
        Assert.True(helper.HandleLine(JsonSerializer.Serialize(new { op = "pair", code })));
        Assert.Equal("paired", Op(lines[0]));
        Assert.Equal("welcome", Op(lines[1]));
        Assert.Equal(9UL, helper.AccountId);
        Assert.Equal(1, _hub.HelperCount(9));
        var link = Await<MSG_MINIONHELPERLINK>(inbox);
        Assert.True(link.Connected);

        Assert.True(helper.HandleLine("""{"op":"order","duel":123,"round":4,"minion":50,"request":7,"move":"cast","card":2,"target":1}"""));
        var order = Await<MSG_MINIONHELPERORDER>(inbox);
        Assert.Equal((123UL, 4, 50UL, 7u, (byte) 0, (byte) 2, 1u), (order.DuelID, order.Round, order.MinionID, order.RequestID, order.MoveType, order.SpellSelection, order.SpellTarget));
        Assert.False(order.Query);

        Assert.True(helper.HandleLine("""{"op":"order","duel":123,"round":4,"minion":50,"request":8,"move":"ai"}"""));
        Assert.Equal(OwnedMinionAiMove, Await<MSG_MINIONHELPERORDER>(inbox).MoveType);
        Assert.True(helper.HandleLine("""{"op":"order","duel":123,"round":4,"minion":50,"request":9,"move":"cast","card":0,"target":-1}"""));
        Assert.Equal(uint.MaxValue, Await<MSG_MINIONHELPERORDER>(inbox).SpellTarget);

        Assert.True(helper.HandleLine("""{"op":"order","duel":123,"round":4,"minion":50,"request":10,"move":"pass","card":0,"target":-1}"""));
        var pass = Await<MSG_MINIONHELPERORDER>(inbox);
        Assert.Equal((byte) Imlight.CoreLib.Game.Combat.CombatMoveType.Pass, pass.MoveType);
        Assert.Equal(uint.MaxValue, pass.SpellTarget);

        Assert.True(helper.HandleLine("""{"op":"control","enabled":false}"""));
        Assert.False(Await<MSG_MINIONHELPERCONTROL>(inbox).Enabled);

        _hub.Push(9, """{"op":"state","phase":"idle"}""");
        Assert.Equal("state", Op(lines.Last()));

        helper.Closed();
        Assert.False(Await<MSG_MINIONHELPERLINK>(inbox).Connected);
        Assert.Equal(0, _hub.HelperCount(9));
    }

    [Fact]
    public void AHelperWithoutAGameSessionSeesOfflineAndOrdersAreNotLost() {
        var (helper, lines) = NewSession();
        var code = _pairing.IssueCode(3);
        Assert.True(helper.HandleLine(JsonSerializer.Serialize(new { op = "pair", code })));
        Assert.Contains(lines, line => line.Contains("\"offline\""));
        Assert.True(helper.HandleLine("""{"op":"order","request":4,"move":"pass"}"""));
        Assert.Contains(lines, line => Op(line) == "ack" && line.Contains("Offline"));
    }

    [Fact]
    public void ASessionLeavingTellsItsHelpersAndAnotherSessionsUnbindIsIgnored() {
        var inbox = new ConcurrentQueue<object>();
        var first = _system.ActorOf(Props.Create(() => new Recorder(inbox)));
        var second = _system.ActorOf(Props.Create(() => new Recorder(inbox)));
        _hub.BindSession(4, first);
        _hub.BindSession(4, second); // a reconnect replaces the session
        var (helper, lines) = NewSession();
        Assert.True(helper.HandleLine(JsonSerializer.Serialize(new { op = "pair", code = _pairing.IssueCode(4) })));
        _hub.UnbindSession(4, first);
        Assert.True(_hub.HasSession(4));
        Assert.DoesNotContain(lines, line => line.Contains("\"offline\""));
        _hub.UnbindSession(4, second);
        Assert.False(_hub.HasSession(4));
        Assert.Contains(lines, line => line.Contains("\"offline\""));
    }

    private (MinionHelperSession Session, List<string> Lines) NewSession() {
        var lines = new List<string>();
        return (new MinionHelperSession(line => { lock (lines) lines.Add(line); }, _hub, _pairing), lines);
    }

    private static string Op(string line) => JsonDocument.Parse(line).RootElement.GetProperty("op").GetString();

    private static T Await<T>(ConcurrentQueue<object> inbox) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) {
            if (inbox.TryDequeue(out var item)) {
                if (item is T found) return found;
                continue;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException($"no {typeof(T).Name}");
    }

    private sealed class Recorder : ReceiveActor {
        public Recorder(ConcurrentQueue<object> inbox) => ReceiveAny(inbox.Enqueue);
    }
}
