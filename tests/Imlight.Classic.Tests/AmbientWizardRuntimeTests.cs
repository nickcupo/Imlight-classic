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
 * AMBIENT WIZARD RUNTIME TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The server side of ambient wizards that runs without a database: who
 * may join a duel (real players always; an ambient wizard only with a
 * permit for that duel, a street fight or a PvP seat), the endpoint's
 * answers, the move encoding a session relays, the classic look, the
 * records' round trip, chat text and zone names.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter AmbientWizardRuntimeTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Math;
using Imlight.Classic.Ambient;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientWizardRuntimeTests {

    private static readonly NameTableSizes s_tables = new(100, 100, 50, 50);

    private static AmbientWizardRecord Record(int seed, ulong charId)
        => AmbientWizardRecord.From(AmbientIdentity.Generate(seed, "WizardCity/WC_Hub", s_tables, (1, 12)), charId);

    private sealed class Sink : ReceiveActor {
        public Sink() => ReceiveAny(_ => { });
    }

    private static AmbientWizard Live(ActorSystem system, ulong charId) {
        var record = Record((int) charId, charId);
        var wizard = new AmbientWizard(record, new Wizard { CharId = charId }) {
            Endpoint = system.ActorOf(Props.Create(() => new Sink())),
        };
        AmbientWizards.Register(wizard);

        return wizard;
    }

    [Fact]
    public void RealPlayersAlwaysJoinAmbientWizardsOnlyWithAPermit() {
        using var system = ActorSystem.Create("ambient-permits", "akka.actor.provider = local");
        var player = system.ActorOf(Props.Create(() => new Sink()));
        var ambient = Live(system, 0xA3B1E00000000101);
        try {
            Assert.True(AmbientWizards.MayJoin(player, 42));
            Assert.True(AmbientWizards.MayEngage(player));
            Assert.False(AmbientWizards.MayJoin(ambient.Endpoint, 42));
            Assert.False(AmbientWizards.MayEngage(ambient.Endpoint));

            AmbientWizards.PermitJoin(ambient.Endpoint, 42); // a player's yes for duel 42
            Assert.True(AmbientWizards.MayJoin(ambient.Endpoint, 42));
            Assert.False(AmbientWizards.MayJoin(ambient.Endpoint, 43));
            Assert.False(AmbientWizards.MayEngage(ambient.Endpoint)); // walking into a circle pulls no creature

            AmbientWizards.PermitJoin(ambient.Endpoint, 0); // its own street fight
            Assert.True(AmbientWizards.MayEngage(ambient.Endpoint));

            AmbientWizards.RevokeJoin(ambient.Endpoint);
            Assert.False(AmbientWizards.MayJoin(ambient.Endpoint, 42));
        }
        finally {
            AmbientWizards.Unregister(ambient);
        }
    }

    [Fact]
    public void SparringSeatIsOnlyForThatCircle() {
        using var system = ActorSystem.Create("ambient-sparring", "akka.actor.provider = local");
        var ambient = Live(system, 0xA3B1E00000000102);
        try {
            AmbientWizards.MarkSparring(ambient.Endpoint, 7);
            Assert.True(AmbientWizards.IsSparringIn(ambient.Endpoint, 7));
            Assert.False(AmbientWizards.IsSparringIn(ambient.Endpoint, 8));
            AmbientWizards.EndSparring(ambient.Endpoint);
            Assert.False(AmbientWizards.IsSparringIn(ambient.Endpoint, 7));
        }
        finally {
            AmbientWizards.Unregister(ambient);
        }
    }

    [Fact]
    public void RegistryFindsAmbientWizardsByActorAndCharacterAndForgetsThem() {
        using var system = ActorSystem.Create("ambient-registry", "akka.actor.provider = local");
        var ambient = Live(system, 0xA3B1E00000000103);
        Assert.True(AmbientWizards.IsAmbient(ambient.Endpoint));
        Assert.True(AmbientWizards.IsAmbientChar(ambient.CharId));
        Assert.True(AmbientWizards.TryGetWizard(ambient.CharId, out var wizard));
        Assert.Same(ambient.Wizard, wizard);

        AmbientWizards.Unregister(ambient);
        Assert.False(AmbientWizards.IsAmbient(ambient.Endpoint));
        Assert.False(AmbientWizards.IsAmbientChar(ambient.CharId));
    }

    [Fact]
    public async Task TheEndpointAnswersTheWizardQueryItself() {
        using var system = ActorSystem.Create("ambient-endpoint", "akka.actor.provider = local");
        var group = system.ActorOf(Props.Create(() => new Sink()));
        var ambient = new AmbientWizard(Record(5, 0xA3B1E00000000104), new Wizard { CharId = 0xA3B1E00000000104 });
        var endpoint = system.ActorOf(AmbientEndpoint.Props(ambient, group));

        var answer = await endpoint.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Same(ambient.Wizard, answer.Wizard);
    }

    [Theory]
    [InlineData(1261.5f, -1985.9f, -28f, -1.042f)]
    [InlineData(-7788f, 7980f, 0f, 3.1f)]
    [InlineData(0f, 0f, 0f, 0f)]
    public void MovesUseTheEncodingSessionsDecode(float x, float y, float z, float yaw) {
        var ambient = new AmbientWizard(Record(1, 0xA3B1E00000000105), new Wizard { CharId = 0xA3B1E00000000105 }) {
            Position = new Vector3(x, y, z), Yaw = yaw,
        };
        ambient.Wizard.GameObject.m_nMobileID = 3277;

        var move = AmbientZone.Move(ambient);

        // WizardService.ReceiveClientMove's decode of a client move.
        Assert.Equal(x, unchecked((short) move.LocationX * 4), 4f);
        Assert.Equal(y, unchecked((short) move.LocationY * 4), 4f);
        Assert.Equal(z, unchecked((short) move.LocationZ * 4), 4f);
        var degrees = move.Direction * (360f / byte.MaxValue) * 1.035f;
        var expected = AmbientWizards.ClientYaw(yaw) * 180f / MathF.PI;
        Assert.InRange(MathF.Abs(degrees - expected) % 360f, 0f, 3f);
        Assert.Equal((ushort) 3277, move.MobileID);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.2f)]
    [InlineData(3.0f)]
    [InlineData(5.9f)]
    public void HeadingAndClientYawAreInverses(float heading) {
        var back = AmbientWizards.Heading(AmbientWizards.ClientYaw(heading));
        Assert.InRange(MathF.Abs(MathF.IEEERemainder(back - heading, 2 * MathF.PI)), 0f, 1e-4f);
    }

    [Fact]
    public void ClientYawMatchesTheDuelSeatConversion() {
        // CombatDuelComponent seats a duelist facing the circle's middle with 2pi - atan2(dy, dx) - 1.58.
        var theta = MathF.Atan2(-300f, 120f);
        var seat = 2 * MathF.PI - theta - 1.58f;
        seat = seat < 0 ? seat + 2 * MathF.PI : seat % (2 * MathF.PI);

        Assert.Equal(seat, AmbientWizards.ClientYaw(theta), 3);
    }

    [Fact]
    public void AvatarsStayInsideTheClassicCreationChoices() {
        for (var seed = 0; seed < 200; seed++) {
            var identity = AmbientIdentity.Generate(seed, "WizardCity/WC_Hub", s_tables, (1, 12));
            var avatar = AmbientWizards.AvatarFor(identity);

            Assert.InRange((byte) avatar.m_nHairModel, 0, 9);
            Assert.InRange((byte) avatar.m_nHairColor, 0, 99);
            Assert.InRange((byte) avatar.m_nSkinColor, 0, 6);
            Assert.InRange((byte) avatar.m_nSkinDecal, 0, 9);
            Assert.Equal((byte) avatar.m_nHatColor, (byte) avatar.m_nTorsoColor);
            Assert.Equal((byte) avatar.m_nTorsoColor, (byte) avatar.m_nFeetColor);
        }
    }

    [Fact]
    public void RecordsRoundTripTheirIdentity() {
        var identity = AmbientIdentity.Generate(77, "Krokotopia/KT_Hub", s_tables, (10, 22));
        var record = AmbientWizardRecord.From(identity, 0xA3B1E00000000106);

        Assert.Equal(identity, record.ToIdentity());
        Assert.Equal("AmbientWizards/" + 0xA3B1E00000000106UL, record.Id);
    }

    [Fact]
    public void SayIsEncodedAsTheOfficialClientWritesIt() {
        // GameClient::HandleSendRadialChat: a 16-bit character count, then UTF-16LE.
        Assert.Equal(new byte[] { 0x02, 0x00, (byte) 'h', 0x00, (byte) 'i', 0x00 }, AmbientChat.EncodeSay("hi"));
        var line = AmbientChat.EncodeSay("need a hand?");
        Assert.Equal(2 + 2 * 12, line.Length);
        Assert.Equal(12, line[0] | line[1] << 8);
        Assert.Equal("need a hand?", AmbientChat.Text(line));
    }

    [Fact]
    public void ChatOffSendsNothing() {
        var before = AmbientWizards.Settings;
        try {
            AmbientWizards.Settings = AmbientSettings.Parse("4", "", "false", "", "");
            var ambient = new AmbientWizard(Record(9, 0xA3B1E00000000109), new Wizard { CharId = 0xA3B1E00000000109 });
            Assert.False(AmbientChat.Say(ambient, "need a hand?"));
            Assert.False(AmbientChat.Whisper(ambient, 1234, "hi"));
        }
        finally {
            AmbientWizards.Settings = before;
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x00, (byte) 'h', 0x00, (byte) 'i', 0x00 }, "hi")]
    [InlineData(new byte[] { 0x01, (byte) 'h', (byte) 'i' }, "hi")]
    [InlineData(new byte[] { (byte) 'h', (byte) 'i', (byte) ' ' }, "hi")]
    [InlineData(new byte[0], "")]
    public void SayTextDropsTheClientMarker(byte[] raw, string text) => Assert.Equal(text, AmbientChat.Text(raw));

    [Theory]
    [InlineData("WizardCity/WC_Hub", "The Commons")]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", "Unicorn Way")]
    [InlineData("WizardCity/WC_Streets/WC_HauntedCave", "Haunted Cave")]
    [InlineData("Krokotopia/KT_Hub", "the hub")]
    [InlineData("WizardCity/WC_Shop_Area", "the Shopping District")]
    public void ZonesAreNamedAsPlayersSayThem(string zone, string name) => Assert.Equal(name, AmbientKnowledge.ZoneName(zone));

    [Fact]
    public void ThinkingTimeIsTwoToFiveSeconds() {
        for (var slot = 4; slot < 8; slot++) {
            for (var round = 1; round < 10; round++) {
                Assert.InRange(AmbientCombat.ThinkingTime(slot, round).TotalSeconds, 2, 5);
            }
        }
    }

}
