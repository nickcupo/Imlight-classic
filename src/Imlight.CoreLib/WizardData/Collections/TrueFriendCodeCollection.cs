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
 * TRUE FRIEND CODE COLLECTION
 * ========================================================================
 *
 * PURPOSE:
 * Keeps True Friend codes in the player database until they are used or
 * expire, so a code outlives both players' sessions and a server restart.
 *
 * USAGE EXAMPLE:
 * var made = TrueFriendCodes.Create(TrueFriendCodeCollection.Instance, charId, name, DateTimeOffset.UtcNow);
 *
 * NOTE:
 * One document per code, under the id TrueFriendCodes/<code>, so a lookup is
 * a load by id. RavenDB's expiration (when enabled) removes a document 48
 * hours after it was made; TrueFriendCode.IsExpired is checked either way.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using System.Linq;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Databases;
using Raven.Client;
using Raven.Client.Documents;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// A True Friend code as stored.
/// </summary>
public sealed class TrueFriendCodeDocument {

    public string Code { get; set; } = "";
    public ulong CreatorCharId { get; set; }
    public string CreatorName { get; set; } = "";
    public long CreatedUnixSeconds { get; set; }

}

/// <summary>
/// True Friend codes in the player database.
/// </summary>
public sealed class TrueFriendCodeCollection : ITrueFriendCodeStore {

    public const string CollectionName = "TrueFriendCodes";

    public static TrueFriendCodeCollection Instance { get; } = new();

    private static IDocumentStore Store => PlayerDatabase.Instance.Store;

    private static string IdFor(string code) => $"{CollectionName}/{code}";

    public TrueFriendCode? Find(string code) {
        using var session = Store.OpenSession();
        var document = session.Load<TrueFriendCodeDocument>(IdFor(code));

        return document is null
            ? null
            : new TrueFriendCode(document.Code, document.CreatorCharId, document.CreatorName, document.CreatedUnixSeconds);
    }

    public int CountOpen(ulong creatorCharId, DateTimeOffset now) {
        using var session = Store.OpenSession();
        var oldest = now.ToUnixTimeSeconds() - (long) TrueFriendCodes.Lifetime.TotalSeconds;

        return session.Query<TrueFriendCodeDocument>(collectionName: CollectionName)
            .Customize(options => options.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Count(document => document.CreatorCharId == creatorCharId && document.CreatedUnixSeconds > oldest);
    }

    public bool Add(TrueFriendCode code) {
        using var session = Store.OpenSession();
        var id = IdFor(code.Code);
        if (session.Load<TrueFriendCodeDocument>(id) is not null) {
            return false;
        }

        var document = new TrueFriendCodeDocument {
            Code = code.Code,
            CreatorCharId = code.CreatorCharId,
            CreatorName = code.CreatorName,
            CreatedUnixSeconds = code.CreatedUnixSeconds,
        };
        session.Store(document, id);
        var metadata = session.Advanced.GetMetadataFor(document);
        metadata[Constants.Documents.Metadata.Collection] = CollectionName;
        metadata[Constants.Documents.Metadata.Expires] = DateTimeOffset.FromUnixTimeSeconds(code.CreatedUnixSeconds)
            .Add(TrueFriendCodes.Lifetime).UtcDateTime;
        session.SaveChanges();

        return true;
    }

    public void Remove(string code) {
        using var session = Store.OpenSession();
        session.Delete(IdFor(code));
        session.SaveChanges();
    }

}
