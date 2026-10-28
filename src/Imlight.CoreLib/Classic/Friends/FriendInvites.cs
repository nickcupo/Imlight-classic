// CLASSIC (outside-access): per-friend invite codes for the friend page's sign-up (Imlight.Classic.Net.InviteBook).
// RavenDB documents FriendInvites/<sha256 of the code>; the code itself is never stored or logged.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Net;
using Imlight.CoreLib.WizardData.Databases;
using Raven.Client.Documents;

namespace Imlight.CoreLib.Classic.Friends;

internal sealed class RavenInviteStore : IInviteStore {
    // Documents FriendInvites/<hash>, in the type's default collection (InviteRecords), as Accounts are.
    private static string DocumentId(string hash) => "FriendInvites/" + hash;

    public InviteRecord? Load(string hash) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        return session.Load<InviteRecord>(DocumentId(hash));
    }

    public void Save(InviteRecord record) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        session.Store(record, DocumentId(record.Hash));
        session.SaveChanges();
    }

    public IReadOnlyList<InviteRecord> All() {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        return session.Query<InviteRecord>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Take(1024)
            .ToList();
    }
}

internal static class FriendInvites {
    /// <summary>The server's invite book (created on first use, after the database is up).</summary>
    internal static readonly Lazy<InviteBook> Book = new(() => new InviteBook(new RavenInviteStore()));

    /// <summary>[Classic] FriendSignupInviteRequired: the friend page's sign-up needs an invite (default false).</summary>
    internal static bool Required => Imlight.CoreLib.Auth.SecuritySettings.Bool("Classic.FriendSignupInviteRequired", false);
}
