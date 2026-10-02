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
 * BAZAAR SERVER STOCK LEDGER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: how many copies of each template the server itself put in the
 * Bazaar, so a restock can rotate its own copies out without touching what
 * players sold. One document.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>The server's own copies in the Bazaar, by template.</summary>
public sealed class BazaarServerStock {

    public string Id { get; set; } = BazaarServerStockCollection.DocumentId;

    public Dictionary<string, int> Copies { get; set; } = [];

    public System.DateTime LastRestockUtc { get; set; }

}

internal static class BazaarServerStockCollection {

    public const string CollectionName = "BazaarServerStock";
    public const string DocumentId = "classic/bazaar-server-stock";

    public static BazaarServerStock Load() {
        using var session = PlayerDatabase.Instance.Store.OpenSession();

        return session.Load<BazaarServerStock>(DocumentId) ?? new BazaarServerStock();
    }

    public static void Save(IReadOnlyDictionary<ulong, int> copies, System.DateTime restockUtc) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var doc = session.Load<BazaarServerStock>(DocumentId);
        if (doc is null) {
            doc = new BazaarServerStock();
            session.Store(doc, DocumentId);
            session.Advanced.GetMetadataFor(doc)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
        }

        doc.Copies = copies.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
        doc.LastRestockUtc = restockUtc;
        session.SaveChanges();
    }

}
