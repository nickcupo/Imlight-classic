using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imlight.Classic.Net;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Shared.Cryptography;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Services;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>Production-audit fixes wired into the server: login checks, accept waits, password records, wire types.</summary>
public class SecurityServerTests {

    [Fact]
    public void ClientKey1_is_checked_against_the_protocol_hash_with_this_sessions_salt() {
        var h = PasswordHashing.ProtocolHash("wand-of-oak");
        var ck1 = ClientKey.HaskCK1("wand-of-oak", 77, 1_700_000_000, 123);
        Assert.True(UserAuthenticator.ClientKey1Matches(h, 77, 1_700_000_000, 123, ck1));
        Assert.False(UserAuthenticator.ClientKey1Matches(h, 78, 1_700_000_000, 123, ck1)); // another session
        Assert.False(UserAuthenticator.ClientKey1Matches(PasswordHashing.ProtocolHash("guess"), 77, 1_700_000_000, 123, ck1));
        // An account without H (in-client password login off, or a sealed hash without its key) never matches,
        // even for a client that computes ClientKey1 over an empty or placeholder hash.
        Assert.False(UserAuthenticator.ClientKey1Matches(null!, 77, 1_700_000_000, 123, ClientKey.SaltedClientKey1("", 77, 1_700_000_000, 123)));
        Assert.False(UserAuthenticator.ClientKey1Matches(h, 77, 1_700_000_000, 123, null!));
    }

    [Fact]
    public void PassKey3_is_compared_and_rejects_null() {
        var pk3 = PassKey3.EncodePK3("key", 5, 100, 7);
        Assert.True(PassKey3.VerifyPK3("key", 5, 100, 7, pk3));
        Assert.False(PassKey3.VerifyPK3("key", 5, 100, 8, pk3));
        Assert.False(PassKey3.VerifyPK3("key", 5, 100, 7, null!));
    }

    [Fact]
    public void Game_connections_get_a_short_accept_wait_and_login_keeps_the_long_one() {
        // The -L client holds its login connection unaccepted at the login screen; a game connection answers at once.
        Assert.Equal(300, ControlService.AcceptWait(300, 30, gameServer: false));
        Assert.Equal(30, ControlService.AcceptWait(300, 30, gameServer: true));
        Assert.Equal(5, ControlService.AcceptWait(300, 1, gameServer: true));
        Assert.Equal(300, ControlService.AcceptWait(300, 0, gameServer: true));
        Assert.Equal(20, ControlService.AcceptWait(20, 30, gameServer: true));
    }

    [Fact]
    public void New_passwords_store_a_pbkdf2_verifier_and_a_sealed_or_no_protocol_hash() {
        var key = PasswordHashing.ParseKey(PasswordHashing.NewKeyText())!;

        var (plain, v1) = PasswordStore.Records("pw", inClient: true, key: null);
        Assert.Equal(PasswordHashing.ProtocolHash("pw"), plain);
        Assert.True(PasswordHashing.Verify(v1, "pw"));

        var (sealedHash, v2) = PasswordStore.Records("pw", inClient: true, key: key);
        Assert.True(PasswordHashing.IsSealed(sealedHash));
        Assert.Equal(PasswordHashing.ProtocolHash("pw"), PasswordHashing.Open(sealedHash, key));
        Assert.True(PasswordHashing.Verify(v2, "pw"));

        var (none, v3) = PasswordStore.Records("pw", inClient: false, key: key);
        Assert.Equal("", none);
        Assert.True(PasswordHashing.Verify(v3, "pw"));
    }

    [Fact]
    public void Old_records_are_upgraded_on_the_next_password_login() {
        var key = PasswordHashing.ParseKey(PasswordHashing.NewKeyText())!;
        var h = PasswordHashing.ProtocolHash("pw");
        var verifier = PasswordHashing.CreateVerifier("pw");

        Assert.True(PasswordStore.NeedsUpgrade(h, null, inClient: true, key: null));        // no verifier
        Assert.False(PasswordStore.NeedsUpgrade(h, verifier, inClient: true, key: null));   // nothing more to do
        Assert.True(PasswordStore.NeedsUpgrade(h, verifier, inClient: true, key: key));     // seal H
        Assert.False(PasswordStore.NeedsUpgrade(PasswordHashing.Seal(h, key), verifier, inClient: true, key: key));
        Assert.True(PasswordStore.NeedsUpgrade(h, verifier, inClient: false, key: key));    // drop H
        Assert.False(PasswordStore.NeedsUpgrade("", verifier, inClient: false, key: key));
        Assert.True(PasswordStore.NeedsUpgrade("", verifier, inClient: true, key: key));    // in-client turned back on
        Assert.True(PasswordStore.NeedsUpgrade(h, PasswordHashing.CreateVerifier("pw", 10_000), inClient: true, key: null));
    }

    /// <summary>
    /// SessionActor.HandlePacket dispatches whatever the decoder returns by type, with no direction filter. The decoder
    /// (MessageEncoder / EnhancedMessageDecoder) only knows the generated KingsIsle protocols and the two client
    /// enhancement messages; Imlight's internal SERVER_100/SERVICE_101/ZONE_102/... packets are not in its tables, so a
    /// client cannot send them. This pins the generated message types the session services handle: every one must be
    /// a message the r806919 client sends. A new handler on a server-to-client type fails here and must be reviewed.
    /// </summary>
    [Fact]
    public void Session_services_handle_only_client_to_server_wire_messages() {
        var services = typeof(MessageService).Assembly.GetTypes()
            .Where(t => typeof(MessageService).IsAssignableFrom(t) && !t.IsAbstract);
        var wire = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var service in services) {
            foreach (var type in MessageHandlerTable.HandlersOf(service).Keys) {
                if (type.Namespace == "Imcodec.MessageLayer.Generated" || type.DeclaringType?.Namespace == "Imcodec.MessageLayer.Generated") {
                    wire.Add($"{type.DeclaringType?.Name}.{type.Name}");
                }
            }
        }

        var unexpected = wire.Where(name => ServerToClientOnly.Contains(name)).ToList();
        Assert.True(unexpected.Count == 0, "Server-to-client messages handled from the wire: " + string.Join(", ", unexpected));

    }

    [Theory]
    [InlineData(100, 12)] // SERVER_100 MSG_VALIDATESESSIONKEY
    [InlineData(101, 1)]  // SERVICE_101
    [InlineData(102, 1)]  // ZONE_102
    [InlineData(104, 1)]  // ACCOUNT_104 MSG_ACCOUNT
    [InlineData(250, 1)]
    public void Internal_packet_service_ids_do_not_decode_from_the_wire(byte serviceId, byte order) {
        var body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var frame = new List<byte> { 0x0D, 0xF0, 0, 0, 0, 0, 0, 0, serviceId, order };
        frame.AddRange(BitConverter.GetBytes((ushort) (body.Length + 4)));
        frame.AddRange(body);
        frame.Add(0);
        var length = BitConverter.GetBytes((ushort) (frame.Count - 4));
        frame[2] = length[0];
        frame[3] = length[1];

        // Only the generated KingsIsle protocols (and the two enhancement messages) can come out of the decoder; the
        // body may decode as stock messages, but never as one of Imlight's internal packet types.
        var decoded = EnhancedMessageDecoder.Decode(frame.ToArray());
        Assert.All(decoded, m => Assert.NotEqual("Imlight.CoreLib.Shared.Packets", m.GetType().Namespace));
        Assert.All(decoded, m => Assert.False(m is IServerMessage));
    }

    // Generated types that only the server sends; a session service must not take them from a client.
    private static readonly HashSet<string> ServerToClientOnly = new(StringComparer.Ordinal) {
        "LOGIN_7_PROTOCOL.MSG_USER_AUTHEN_RSP",
        "LOGIN_7_PROTOCOL.MSG_USER_VALIDATE_RSP",
        "LOGIN_7_PROTOCOL.MSG_CHARACTERSELECTED",
        "LOGIN_7_PROTOCOL.MSG_USER_ADMIT_IND",
        "LOGIN_7_PROTOCOL.MSG_CHARACTERINFO",
        "LOGIN_7_PROTOCOL.MSG_CHARACTERLIST",
        "LOGIN_7_PROTOCOL.MSG_STARTCHARACTERLIST",
        "LOGIN_7_PROTOCOL.MSG_CREATECHARACTERRESPONSE",
        "LOGIN_7_PROTOCOL.MSG_DELETECHARACTERRESPONSE",
        "GAME_5_PROTOCOL.MSG_LOGINCOMPLETE",
        "GAME_5_PROTOCOL.MSG_ATTACHFAILED",
        "GAME_5_PROTOCOL.MSG_SERVERTRANSFER",
        "GAME_5_PROTOCOL.MSG_SERVERTELEPORT",
        "GAME_5_PROTOCOL.MSG_NEWOBJECT",
        "GAME_5_PROTOCOL.MSG_DELETEOBJECT",
        "EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE",
        "WIZARD_12_PROTOCOL.MSG_UPDATEMANA",
    };
}
