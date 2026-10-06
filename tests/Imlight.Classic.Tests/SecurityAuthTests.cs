using System;
using System.Linq;
using Imlight.Classic.Net;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>Production-audit fixes B2/B3, M1, H2, H5: attach keys, login keys, throttles, passwords, limits.</summary>
public class SecurityAuthTests {

    private sealed class ManualClock : TimeProvider {
        public DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ------------------------------------------------------------ game attach keys (B2/B3)

    [Fact]
    public void Attach_keys_are_random_32_bytes_in_the_old_44_character_form() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        var a = keys.Issue(1, "10.0.0.2");
        var b = keys.Issue(2, "10.0.0.2");
        Assert.Equal(44, a.Length);
        Assert.Equal(32, Convert.FromBase64String(a).Length);
        Assert.NotEqual(a, b);
        // Not a function of the account id: issuing again for the same account gives a new key.
        Assert.NotEqual(a, keys.Issue(1, "10.0.0.2"));
    }

    [Fact]
    public void Attach_needs_the_key_issued_to_the_named_account() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        var victim = keys.Issue(100, "10.0.0.2");
        var attacker = keys.Issue(200, "10.0.0.3");

        // The old exploit: any key (or none) with the victim's UserID.
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("anything", 100, "10.0.0.3"));
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("", 100, "10.0.0.3"));
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume(null, 100, "10.0.0.3"));
        // Its own valid key with the victim's id.
        Assert.Equal(GameKeyResult.WrongAccount, keys.TryConsume(attacker, 100, "10.0.0.3"));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(victim, 100, "10.0.0.2"));
    }

    [Fact]
    public void A_key_is_consumed_by_its_attach_and_rearmed_only_after_a_failed_attach() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        var key = keys.Issue(7, "10.0.0.2");
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(key, 7, "10.0.0.2"));
        Assert.Equal(GameKeyResult.NotArmed, keys.TryConsume(key, 7, "10.0.0.2"));

        // MSG_ATTACHFAILED after the key check (or the login queue): the client falls back with the same key.
        Assert.True(keys.Arm(7));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(key, 7, "10.0.0.2"));
        Assert.Equal(GameKeyResult.NotArmed, keys.TryConsume(key, 7, "10.0.0.2"));
        Assert.False(keys.Arm(8));
    }

    // Live 2026-10-05/06: walking into WizardCity/WC_Duel_Arena (any zone change) disconnected the r806919 client with
    // "failed to validate their login key" (UnknownKey). The client copies the MSG_SERVERTRANSFER over its stored
    // MSG_CHARACTERSELECTED record and builds MSG_ATTACH field by field from it: LoginKey (STR) from the transfer's Key
    // (INT) comes out EMPTY, while GID fields such as SessionID are echoed. This is that attach.
    private static (string LoginKey, ulong SessionId) RealClientAttachAfter(GameSessionKeys.TransferKey transfer)
        => ("", transfer.SessionId);

    [Fact]
    public void After_a_transfer_the_real_client_attaches_with_an_empty_key_and_the_echoed_session_id() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        var select = keys.Issue(7, "10.0.0.2");
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(select, 7, "10.0.0.2"));

        var transfer = keys.IssueTransfer(7, "10.0.0.2");
        Assert.NotEqual(0UL, transfer.SessionId);
        var (loginKey, sessionId) = RealClientAttachAfter(transfer);

        // Without the echoed SessionID an empty key proves nothing (the live failure), nor does a guessed one.
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume(loginKey, 7, "10.0.0.2"));
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume(loginKey, 7, "10.0.0.2", transfer.SessionId ^ 2));
        // Bound to the account and the address.
        Assert.Equal(GameKeyResult.WrongAccount, keys.TryConsume(loginKey, 8, "10.0.0.2", sessionId));
        Assert.Equal(GameKeyResult.WrongAddress, keys.TryConsume(loginKey, 7, "10.0.0.9", sessionId));

        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(loginKey, 7, "::ffff:10.0.0.2", sessionId));
        // Single use: a second attach of the same transfer (the client's parallel or retried attach) is refused, and
        // so is the Key of that transfer (both proofs are one entry).
        Assert.Equal(GameKeyResult.NotArmed, keys.TryConsume(loginKey, 7, "10.0.0.2", sessionId));
        Assert.Equal(GameKeyResult.NotArmed, keys.TryConsume(GameSessionKeys.TransferKeyText(transfer.Key), 7, "10.0.0.2"));
        Assert.Equal(1, keys.Count);

        // A failed attach after the key check re-arms it for the client's fallback attach (same record, same SessionID).
        Assert.True(keys.Arm(7));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume("", 7, "10.0.0.2", sessionId));

        // The next zone change has new proofs; the old SessionID no longer works.
        var next = keys.IssueTransfer(7, "10.0.0.2");
        Assert.NotEqual(transfer.SessionId, next.SessionId);
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("", 7, "10.0.0.2", sessionId));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume("", 7, "10.0.0.2", next.SessionId));
        // The select key never comes back.
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume(select, 7, "10.0.0.2"));
        Assert.Equal(1, keys.Count);
    }

    [Fact]
    public void Exactly_one_of_two_parallel_attaches_of_a_transfer_wins() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        keys.Issue(7, "10.0.0.2");
        var transfer = keys.IssueTransfer(7, "10.0.0.2");
        var results = new GameKeyResult[2];
        System.Threading.Tasks.Parallel.For(0, 2, i => results[i] = keys.TryConsume("", 7, "10.0.0.2", transfer.SessionId));
        Assert.Equal(1, results.Count(r => r == GameKeyResult.Accepted));
        Assert.Equal(1, results.Count(r => r == GameKeyResult.NotArmed));
    }

    [Fact]
    public void A_client_that_sends_the_transfer_key_as_decimal_text_also_attaches() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        var transfer = keys.IssueTransfer(7, "10.0.0.2");
        Assert.InRange(transfer.Key, 1, int.MaxValue);
        var text = GameSessionKeys.TransferKeyText(transfer.Key);
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("0", 7, "10.0.0.2"));
        Assert.Equal(GameKeyResult.WrongAddress, keys.TryConsume(text, 7, "10.0.0.9"));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(text, 7, "10.0.0.2"));
        Assert.Equal(GameKeyResult.NotArmed, keys.TryConsume("", 7, "10.0.0.2", transfer.SessionId));

        // A stale key text with the right SessionID (the playbot resends its select key) still proves the transfer.
        var select = keys.Issue(7, "10.0.0.2");
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(select, 7, "10.0.0.2"));
        var next = keys.IssueTransfer(7, "10.0.0.2");
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(select, 7, "10.0.0.2", next.SessionId));

        // A new character select replaces a transfer.
        var again = keys.IssueTransfer(7, "10.0.0.2");
        var reselect = keys.Issue(7, "10.0.0.2");
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("", 7, "10.0.0.2", again.SessionId));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(reselect, 7, "10.0.0.2"));
        keys.Revoke(7);
        Assert.Equal(0, keys.Count);
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("", 7, "10.0.0.2", again.SessionId));
    }

    [Fact]
    public void A_transfer_expires_and_a_few_wrong_guesses_disarm_it() {
        var clock = new ManualClock();
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5), time: clock);
        var transfer = keys.IssueTransfer(7, "10.0.0.2");
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(GameKeyResult.Expired, keys.TryConsume("", 7, "10.0.0.2", transfer.SessionId));

        transfer = keys.IssueTransfer(7, "10.0.0.2");
        for (var i = 0; i < GameSessionKeys.MaxTransferFailures; i++) {
            Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume("", 7, "10.0.0.2", 1000UL + (ulong) i));
        }

        Assert.Equal(GameKeyResult.NotArmed, keys.TryConsume("", 7, "10.0.0.2", transfer.SessionId));

        // Wrong guesses naming another account do not touch this one's transfer.
        transfer = keys.IssueTransfer(7, "10.0.0.2");
        for (var i = 0; i < GameSessionKeys.MaxTransferFailures; i++) {
            keys.TryConsume("12345", 9, "10.0.0.2", 99);
        }

        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume("", 7, "10.0.0.2", transfer.SessionId));
    }

    [Fact]
    public void The_attach_timeout_fallback_needs_a_transfer_to_the_same_address() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        keys.Issue(7, "10.0.0.2");
        Assert.False(keys.HasTransferKeyFor(7, "10.0.0.2")); // only a select key
        keys.IssueTransfer(7, "10.0.0.2");
        Assert.True(keys.HasTransferKeyFor(7, "::ffff:10.0.0.2"));
        Assert.False(keys.HasTransferKeyFor(7, "10.0.0.9"));
        Assert.False(keys.HasTransferKeyFor(8, "10.0.0.2"));
    }

    /// <summary>
    /// Every MSG_SERVERTRANSFER the server builds (zone change, realm transfer, attach fallback: every door, sigil,
    /// arena match, Go Home, teleport to a friend) must carry fresh proofs from IssueTransfer (SessionID, Key and
    /// FallbackKey), and the attach must hand its SessionID to the key check.
    /// </summary>
    [Fact]
    public void Every_server_transfer_carries_fresh_proofs_and_the_attach_checks_its_session_id() {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "Imlight.CoreLib"))) {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var src = System.IO.Path.Combine(dir!.FullName, "src");
        var builds = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(src, "*.cs", System.IO.SearchOption.AllDirectories)) {
            if (file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")) {
                continue;
            }

            var text = System.IO.File.ReadAllText(file);
            var at = 0;
            while ((at = text.IndexOf("new GAME_5_PROTOCOL.MSG_SERVERTRANSFER", at, StringComparison.Ordinal)) >= 0) {
                builds++;
                var end = text.IndexOf("};", at, StringComparison.Ordinal);
                var body = text[at..end];
                var before = text[Math.Max(0, at - 1200)..at];
                var name = System.IO.Path.GetFileName(file);
                Assert.True(body.Contains("Key = transferKey.Key,") && body.Contains("FallbackKey = transferKey.Key,")
                            && body.Contains("SessionID = transferKey.SessionId,"),
                    $"{name}: a MSG_SERVERTRANSFER without the transfer proofs");
                Assert.True(before.Contains("IssueTransfer("), $"{name}: transferKey does not come from IssueTransfer");
                at = end;
            }
        }

        Assert.Equal(3, builds); // ZoneService (zone change, realm transfer), AttachService (attach-timeout fallback)

        var attach = System.IO.File.ReadAllText(System.IO.Path.Combine(src, "Imlight.CoreLib", "Game", "Services", "AttachService.cs"));
        Assert.Contains("ValidateLoginKey(message.LoginKey, message.UserID, message.SessionID, out var account)", attach);
        Assert.Contains("SessionID = sessionId,", attach);
        var server = System.IO.File.ReadAllText(System.IO.Path.Combine(src, "Imlight.CoreLib", "Game", "GameServer.cs"));
        Assert.Contains("Keys.TryConsume(message.Key.ToString(), message.UserID, message.SessionActor?.RemoteIp, message.SessionID)", server);
    }

    [Fact]
    public void A_key_expires_after_its_window_and_a_new_select_replaces_it() {
        var clock = new ManualClock();
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5), time: clock);
        var key = keys.Issue(7, "10.0.0.2");
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(GameKeyResult.Expired, keys.TryConsume(key, 7, "10.0.0.2"));
        keys.Arm(7);
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(key, 7, "10.0.0.2"));

        var second = keys.Issue(7, "10.0.0.2");
        keys.Arm(7);
        Assert.Equal(GameKeyResult.UnknownKey, keys.TryConsume(key, 7, "10.0.0.2"));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(second, 7, "10.0.0.2"));
        Assert.Equal(1, keys.Count);

        keys.Revoke(7);
        Assert.Equal(0, keys.Count);
    }

    [Fact]
    public void A_bound_key_only_attaches_from_the_selecting_address() {
        var keys = new GameSessionKeys(TimeSpan.FromMinutes(5));
        var key = keys.Issue(7, "::ffff:10.0.0.2");
        Assert.Equal(GameKeyResult.WrongAddress, keys.TryConsume(key, 7, "10.0.0.9"));
        Assert.Equal(GameKeyResult.Accepted, keys.TryConsume(key, 7, "10.0.0.2"));

        var loose = new GameSessionKeys(TimeSpan.FromMinutes(5), bindAddress: false);
        var other = loose.Issue(7, "10.0.0.2");
        Assert.Equal(GameKeyResult.Accepted, loose.TryConsume(other, 7, "10.0.0.9"));
    }

    [Fact]
    public void The_configured_validity_is_kept_to_minutes() {
        Assert.Equal(TimeSpan.FromMinutes(15), GameSessionKeys.ClampValidity(TimeSpan.FromHours(8))); // live had 28800 s
        Assert.Equal(TimeSpan.FromSeconds(30), GameSessionKeys.ClampValidity(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromMinutes(5), GameSessionKeys.ClampValidity(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(300), GameSessionKeys.ClampValidity(TimeSpan.FromSeconds(300)));
    }

    // ------------------------------------------------------------ login keys (M1)

    [Fact]
    public void A_login_key_expires_when_idle_or_too_old_and_never_without_an_issue_time() {
        var policy = LoginKeyPolicy.From(30, 12, true);
        var issued = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(LoginKeyRefusal.None, policy.Check("k", issued, issued, "10.0.0.2", "10.0.0.2", issued.AddMinutes(29)));
        Assert.Equal(LoginKeyRefusal.Expired, policy.Check("k", issued, issued, "10.0.0.2", "10.0.0.2", issued.AddMinutes(31)));
        // Used (validate / attach / leaving the game) at 11:50 of play: valid 30 more minutes.
        Assert.Equal(LoginKeyRefusal.None, policy.Check("k", issued, issued.AddHours(11).AddMinutes(50), "10.0.0.2",
            "10.0.0.2", issued.AddHours(11).AddMinutes(59)));
        // But never past the 12 h cap.
        Assert.Equal(LoginKeyRefusal.Expired, policy.Check("k", issued, issued.AddHours(11).AddMinutes(50), "10.0.0.2",
            "10.0.0.2", issued.AddHours(12)));
        // Records from before expiry existed (the live store) are refused.
        Assert.Equal(LoginKeyRefusal.Expired, policy.Check("k", DateTime.MinValue, DateTime.MinValue, null, "10.0.0.2", issued));
        Assert.Equal(LoginKeyRefusal.Missing, policy.Check(null, issued, issued, null, null, issued));
    }

    [Fact]
    public void A_login_key_answers_only_its_address_unless_issued_through_a_local_proxy() {
        var policy = LoginKeyPolicy.Default;
        var t = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(LoginKeyRefusal.WrongAddress, policy.Check("k", t, t, "203.0.113.5", "198.51.100.7", t));
        Assert.Equal(LoginKeyRefusal.None, policy.Check("k", t, t, "203.0.113.5", "::ffff:203.0.113.5", t));
        Assert.Equal(LoginKeyRefusal.None, policy.Check("k", t, t, "127.0.0.1", "198.51.100.7", t));
        Assert.Equal(LoginKeyRefusal.None, (policy with { BindAddress = false }).Check("k", t, t, "203.0.113.5", "198.51.100.7", t));
    }

    // ------------------------------------------------------------ brute force (H2)

    [Fact]
    public void Five_failures_lock_the_account_with_doubling_lockouts() {
        var clock = new ManualClock();
        var throttle = new LoginThrottle(new LoginThrottleOptions(), clock);
        for (var i = 0; i < 4; i++) {
            Assert.False(throttle.Failure("Wizard1", $"10.0.0.{i}"));
        }

        Assert.Null(throttle.LockedFor("wizard1", "10.0.0.50"));
        Assert.True(throttle.Failure("wizard1", "10.0.0.4"));
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.LockedFor("WIZARD1", "10.0.0.99"));

        clock.Now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        Assert.Null(throttle.LockedFor("wizard1", null));
        for (var i = 0; i < 5; i++) {
            throttle.Failure("wizard1", null);
        }

        Assert.Equal(TimeSpan.FromMinutes(2), throttle.LockedFor("wizard1", null));

        // A success clears the account.
        clock.Now += TimeSpan.FromMinutes(3);
        throttle.Success("wizard1");
        for (var i = 0; i < 5; i++) {
            throttle.Failure("wizard1", null);
        }

        Assert.Equal(TimeSpan.FromMinutes(1), throttle.LockedFor("wizard1", null));
    }

    [Fact]
    public void An_address_guessing_many_accounts_is_locked_and_lockouts_cap() {
        var clock = new ManualClock();
        var throttle = new LoginThrottle(new LoginThrottleOptions { AddressFailures = 20, MaxLockout = TimeSpan.FromMinutes(30) }, clock);
        for (var i = 0; i < 20; i++) {
            throttle.Failure($"user{i}", "203.0.113.9");
        }

        Assert.NotNull(throttle.LockedFor("someone-else", "203.0.113.9"));
        Assert.Null(throttle.LockedFor("someone-else", "203.0.113.10"));
        // A known password does not clear the address.
        throttle.Success("user0");
        Assert.NotNull(throttle.LockedFor(null, "203.0.113.9"));

        for (var round = 0; round < 10; round++) {
            clock.Now += TimeSpan.FromHours(1);
            for (var i = 0; i < 20; i++) {
                throttle.Failure(null, "203.0.113.9");
            }
        }

        Assert.True(throttle.LockedFor(null, "203.0.113.9") <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Failures_spread_over_more_than_the_window_do_not_lock() {
        var clock = new ManualClock();
        var throttle = new LoginThrottle(new LoginThrottleOptions(), clock);
        for (var i = 0; i < 20; i++) {
            throttle.Failure("owner", "10.0.0.2");
            clock.Now += TimeSpan.FromMinutes(4);
        }

        Assert.Null(throttle.LockedFor("owner", "10.0.0.2"));
    }

    // ------------------------------------------------------------ password storage (H2)

    [Fact]
    public void The_protocol_hash_is_what_the_client_builds_ClientKey1_on() {
        // Base64(SHA-512("password")).
        Assert.Equal("sQnzu7wkTrgkQZF+0G1hi5AI3Qmzvv0bXgc5THBqi7mAsdd4Xll27ASbRt9fEyavWi6m0QP9B8lThf+rDKy8hg==",
            PasswordHashing.ProtocolHash("password"));
        Assert.True(PasswordHashing.LooksLikeProtocolHash(PasswordHashing.ProtocolHash("x")));
        Assert.False(PasswordHashing.LooksLikeProtocolHash("-"));
        Assert.False(PasswordHashing.LooksLikeProtocolHash(""));
    }

    [Fact]
    public void Pbkdf2_verifiers_are_salted_and_checked() {
        var a = PasswordHashing.CreateVerifier("correct horse", 10_000);
        var b = PasswordHashing.CreateVerifier("correct horse", 10_000);
        Assert.NotEqual(a, b);
        Assert.StartsWith("pbkdf2-sha512$10000$", a);
        Assert.True(PasswordHashing.Verify(a, "correct horse"));
        Assert.False(PasswordHashing.Verify(a, "correct horse "));
        Assert.False(PasswordHashing.Verify(a, ""));
        Assert.False(PasswordHashing.Verify(null, "correct horse"));
        Assert.False(PasswordHashing.Verify("pbkdf2-sha512$5$AA==$AA==", "x"));
        Assert.False(PasswordHashing.Verify("garbage", "x"));
        Assert.True(PasswordHashing.VerifierNeedsRehash(a));
        Assert.False(PasswordHashing.VerifierNeedsRehash(PasswordHashing.CreateVerifier("x")));
    }

    [Fact]
    public void A_sealed_protocol_hash_opens_only_with_its_key() {
        var key = PasswordHashing.ParseKey(PasswordHashing.NewKeyText())!;
        var other = PasswordHashing.ParseKey(PasswordHashing.NewKeyText())!;
        var h = PasswordHashing.ProtocolHash("wand-of-oak");
        var sealedHash = PasswordHashing.Seal(h, key);
        Assert.StartsWith("enc1:", sealedHash);
        Assert.DoesNotContain(h, sealedHash);
        Assert.NotEqual(sealedHash, PasswordHashing.Seal(h, key)); // random nonce
        Assert.Equal(h, PasswordHashing.Open(sealedHash, key));
        Assert.Null(PasswordHashing.Open(sealedHash, other));
        Assert.Null(PasswordHashing.Open(sealedHash, null));
        Assert.Null(PasswordHashing.Open("enc1:AAAA", key));
        Assert.Equal(h, PasswordHashing.Open(h, null)); // a legacy plain hash
        Assert.Null(PasswordHashing.Open("", key));
        Assert.Null(PasswordHashing.Open("-", key));
        Assert.Null(PasswordHashing.ParseKey("too short"));
        Assert.True(PasswordHashing.SameHash(h, PasswordHashing.ProtocolHash("wand-of-oak")));
        Assert.False(PasswordHashing.SameHash(h, null));
    }

    // ------------------------------------------------------------ connection limits (H5)

    [Fact]
    public void Connections_are_limited_per_address_and_per_server() {
        Assert.Null(ConnectionLimits.Refuse("203.0.113.9", 15, 100, 16, 2000, false));
        Assert.NotNull(ConnectionLimits.Refuse("203.0.113.9", 16, 100, 16, 2000, false));
        Assert.NotNull(ConnectionLimits.Refuse("203.0.113.10", 0, 2000, 16, 2000, false));
        Assert.Null(ConnectionLimits.Refuse("203.0.113.9", 500, 600, 0, 2000, false));
        // Local bots and a local proxy are exempt unless asked.
        Assert.Null(ConnectionLimits.Refuse("127.0.0.1", 100, 100, 16, 2000, false));
        Assert.NotNull(ConnectionLimits.Refuse("127.0.0.1", 100, 100, 16, 2000, true));
    }

    [Fact]
    public void Only_a_few_messages_wait_for_the_handshake() {
        Assert.True(ConnectionLimits.MayCachePreHandshake(15, 16));
        Assert.False(ConnectionLimits.MayCachePreHandshake(16, 16));
        Assert.False(ConnectionLimits.MayCachePreHandshake(ConnectionLimits.DefaultPreHandshakeMessages, 0));
        Assert.Equal(16, Enumerable.Range(0, 100).TakeWhile(n => ConnectionLimits.MayCachePreHandshake(n, 16)).Count());
    }
}
