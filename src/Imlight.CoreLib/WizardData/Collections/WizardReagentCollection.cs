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
*/

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.WizardData.Collections;

// CLASSIC: acquisition deltas and acknowledged receipts are separate from a reagent stack's snapshot quantity.
internal sealed record ReagentAcquisition(ClientReagentItem Candidate, int Quantity);
internal sealed record ReagentAcquisitionReceipt(ClientReagentItem Reagent, int Acquired);

internal sealed class WizardReagentCollection {

    public const string CollectionName = "WizardReagents";
    private static readonly Lazy<IDocumentStore> s_storeSource = new(() => PlayerDatabase.Instance.Store);
    private static IDocumentStore s_store => s_storeSource.Value;

    // CLASSIC: scoped test row loading uses the same tracked rows, staging and character write lane as production.
    internal static readonly System.Threading.AsyncLocal<Func<IDocumentSession, List<ClientReagentItem>>> TestRowsScope = new();

    // CLASSIC: a stale auto-index cannot declare a referenced reagent missing. Materialize before comparing the
    // generated GID.Full identities, and request every row rather than accepting Raven's default query page.
    internal static IQueryable<ClientReagentItem> ReagentQuery(IDocumentSession session)
        => session.Query<ClientReagentItem>(collectionName: CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(
                ConfigurationManager.Settings["Database.DatabaseWaitForNonStaleResultsTimeout"].AsByte(5))))
            .Take(int.MaxValue);

    private static List<ClientReagentItem> ReadRows(IDocumentSession session)
        => TestRowsScope.Value is { } load ? load(session) : ReagentQuery(session).ToList();

    /// <summary>
    /// CLASSIC: adds exactly one reagent, saving its count and the wizard's bag reference together. A caller may
    /// pass the live stack itself; its current quantity is a snapshot, never the quantity being acquired.
    /// </summary>
    internal static bool AddReagent(Wizard live, ClientReagentItem candidate, out ClientReagentItem updated) {
        updated = null;
        if (!AddReagents(live, [new(candidate, 1)], out var receipts)) return false;
        updated = receipts.Single().Reagent;
        return true;
    }

    // CLASSIC: a harvested normal/rare group commits once. Lost acknowledgement cannot leave an acknowledged
    // first copy followed by an uncertain second copy that a respawned node would grant again.
    internal static bool AddReagents(Wizard live, IReadOnlyList<ReagentAcquisition> acquisitions,
        out IReadOnlyList<ReagentAcquisitionReceipt> receipts) {
        receipts = [];
        if (live is null || live.CharId == 0 || WizardCollection.IsInventorySnapshotUncertain(live)
            || acquisitions is null || acquisitions.Count == 0 || acquisitions.Any(acquisition => acquisition is null
                || acquisition.Candidate is null || acquisition.Quantity <= 0
                || (acquisition.Candidate.m_characterId.Full != 0 && acquisition.Candidate.m_characterId.Full != live.CharId))) return false;

        var detached = acquisitions.Select(acquisition => new ReagentAcquisition(
            acquisition.Candidate with { m_characterId = live.CharId }, acquisition.Quantity)).ToArray();
        IReadOnlyList<ReagentAcquisitionReceipt> staged = [], published = [];
        // CLASSIC: recheck/mark inside the shared lane. A queued stale save cannot overtake a lost ACK.
        var committed = WizardCollection.CommitCharacterMutation(live.CharId,
            (session, saved) => !WizardCollection.IsInventorySnapshotUncertain(live)
                && TryStageAcquisitions(session, saved, detached, out staged),
            saved => published = staged.Select(receipt => new ReagentAcquisitionReceipt(
                PublishCommittedBag(live, saved, receipt.Reagent), receipt.Acquired)).ToArray(),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (!committed) return false;
        receipts = published;
        return true;
    }

    // CLASSIC: validate every identity/ownership/reference before staging any count, then use one fresh tracked
    // row snapshot for all templates. Same-template inputs sum explicit deltas and produce one canonical receipt.
    internal static bool TryStageAcquisitions(IDocumentSession session, Wizard saved,
        IReadOnlyList<ReagentAcquisition> acquisitions, out IReadOnlyList<ReagentAcquisitionReceipt> receipts)
        => TryStageAcquisitions(session, saved, acquisitions, out receipts, out _);

    // CLASSIC: an enclosing card/reagent reward must distinguish validated full stacks from a corrupt bag.
    // Existing harvest callers keep their false/no-save result when there are no fitting copies.
    internal static bool TryStageAcquisitions(IDocumentSession session, Wizard saved,
        IReadOnlyList<ReagentAcquisition> acquisitions, out IReadOnlyList<ReagentAcquisitionReceipt> receipts,
        out bool validated) {
        receipts = [];
        validated = false;
        if (session is null || saved is null || saved.CharId == 0 || acquisitions is null || acquisitions.Count == 0
            || acquisitions.Any(acquisition => acquisition is null || acquisition.Candidate is null
                || acquisition.Quantity <= 0 || acquisition.Candidate.m_characterId.Full != saved.CharId
                || acquisition.Candidate.m_globalID.Full == 0 || acquisition.Candidate.m_templateID.Full == 0)) return false;
        if (acquisitions.GroupBy(acquisition => acquisition.Candidate.m_globalID.Full)
            .Any(group => group.Select(acquisition => acquisition.Candidate.m_templateID.Full).Distinct().Count() != 1)) return false;

        var rows = ReadRows(session);
        if (!TryValidateBag(saved, rows, out var ids, out _)) return false;
        var plans = new List<(ClientReagentItem Row, bool IsNew, int Acquired)>();
        foreach (var group in acquisitions.GroupBy(acquisition => acquisition.Candidate.m_templateID.Full)) {
            var matching = rows.Where(row => row is not null && row.m_characterId.Full == saved.CharId
                && row.m_templateID.Full == group.Key).ToList();
            if (matching.Count > 1) return false;
            var existing = matching.SingleOrDefault();
            if (existing is not null && (!ids.Contains(existing.m_globalID.Full) || existing.m_quantity <= 0)) return false;
            // CLASSIC: even a full stack cannot hide a foreign/colliding identity in another input.
            foreach (var acquisition in group) {
                if (rows.Any(row => row is not null && row.m_globalID.Full == acquisition.Candidate.m_globalID.Full
                    && !ReferenceEquals(row, existing))) return false;
            }
            var requested = group.Sum(acquisition => (long) acquisition.Quantity);
            var available = existing is null ? ServerAlchemyBehavior.MaxReagentStack
                : Math.Max(0, ServerAlchemyBehavior.MaxReagentStack - existing.m_quantity);
            var acquired = (int) Math.Min(requested, available);
            if (acquired == 0) continue;
            plans.Add((existing ?? (group.First().Candidate with { m_quantity = 0 }), existing is null, acquired));
        }
        validated = true; // every bag reference and every input identity has passed, including full stacks
        if (plans.Count == 0) return false;

        var updated = new List<ReagentAcquisitionReceipt>();
        foreach (var plan in plans) {
            plan.Row.m_quantity += plan.Acquired;
            if (plan.IsNew) {
                session.Store(plan.Row);
                session.Advanced.GetMetadataFor(plan.Row)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
            }
            updated.Add(new(plan.Row, plan.Acquired));
        }
        if (plans.Any(plan => plan.IsNew)) {
            saved.AlchemyBehavior ??= new();
            saved.AlchemyBehavior.ReagentItemIds = [.. ids, .. plans.Where(plan => plan.IsNew).Select(plan => plan.Row.m_globalID.Full)];
        }
        receipts = updated;
        return true;
    }

    /// <summary>
    /// CLASSIC: removes one saved copy, including its row and bag reference when it was the last one.
    /// </summary>
    internal static bool RemoveReagent(Wizard live, ulong globalId, out ClientReagentItem updated) {
        updated = null;
        if (live is null || live.CharId == 0 || globalId == 0 || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        ClientReagentItem staged = null, published = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId,
            (session, saved) => !WizardCollection.IsInventorySnapshotUncertain(live)
                && TryStageRemove(session, saved, globalId, 1, out staged),
            saved => published = PublishCommittedBag(live, saved, staged),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (!committed) return false;
        updated = published;
        return true;
    }

    /// <summary>
    /// CLASSIC: session-only staging for an enclosing atomic transaction (including Bazaar gold and stock).
    /// Refuses ambiguous/missing/foreign bag references instead of repairing saved counts or guessing ownership.
    /// </summary>
    internal static bool TryStageAdd(IDocumentSession session, Wizard saved, ClientReagentItem candidate,
        int quantity, out ClientReagentItem updated) {
        updated = null;
        if (session is null || saved is null || saved.CharId == 0 || candidate is null
            || candidate.m_characterId.Full != saved.CharId || candidate.m_globalID.Full == 0
            || candidate.m_templateID.Full == 0 || quantity <= 0 || quantity > ServerAlchemyBehavior.MaxReagentStack) return false;

        var rows = ReadRows(session);
        if (!TryValidateBag(saved, rows, out var ids, out _)) return false;
        var matching = rows.Where(row => row is not null && row.m_characterId.Full == saved.CharId
            && row.m_templateID.Full == candidate.m_templateID.Full).ToList();
        if (matching.Count > 1) return false;
        var existing = matching.SingleOrDefault();
        if (existing is not null) {
            if (!ids.Contains(existing.m_globalID.Full) || existing.m_quantity <= 0
                || existing.m_quantity > ServerAlchemyBehavior.MaxReagentStack - quantity) return false;
            // CLASSIC: a newly allocated candidate may name the same template, but an existing global identity
            // can only refer to this exact saved stack. Neither other owners nor another template can be claimed.
            if (rows.Any(row => row is not null && row.m_globalID.Full == candidate.m_globalID.Full
                && !ReferenceEquals(row, existing))) return false;
            existing.m_quantity += quantity;
            updated = existing;
            return true;
        }

        if (ids.Contains(candidate.m_globalID.Full)
            || rows.Any(row => row is not null && row.m_globalID.Full == candidate.m_globalID.Full)) return false;
        var added = candidate with { m_quantity = quantity };
        session.Store(added);
        session.Advanced.GetMetadataFor(added)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
        saved.AlchemyBehavior ??= new();
        saved.AlchemyBehavior.ReagentItemIds = [.. ids, added.m_globalID.Full];
        updated = added;
        return true;
    }

    internal static bool TryStageRemove(IDocumentSession session, Wizard saved, ulong ownedGlobalId,
        int quantity, out ClientReagentItem updated) {
        updated = null;
        if (session is null || saved is null || saved.CharId == 0 || ownedGlobalId == 0
            || quantity <= 0 || quantity > ServerAlchemyBehavior.MaxReagentStack) return false;
        var rows = ReadRows(session);
        if (!TryValidateBag(saved, rows, out var ids, out var owned) || !ids.Contains(ownedGlobalId)) return false;
        var existing = owned.Single(row => row.m_globalID.Full == ownedGlobalId);
        if (rows.Count(row => row is not null && row.m_characterId.Full == saved.CharId
            && row.m_templateID.Full == existing.m_templateID.Full) != 1 || existing.m_quantity < quantity) return false;

        existing.m_quantity -= quantity;
        if (existing.m_quantity == 0) {
            session.Delete(existing);
            saved.AlchemyBehavior.ReagentItemIds = [.. ids.Where(id => id != ownedGlobalId)];
        }
        updated = existing;
        return true;
    }

    // CLASSIC: only materialize rows referenced exactly once by this saved wizard, with exact full native IDs.
    // This is validation, not a migration: orphaned rows and historic counts are left intact on refusal.
    // CLASSIC: characters saved before the atomic reagent bag keep ids whose row was deleted when the stack ran out.
    // That left the character unloadable ("ambiguous or missing owned rows"). A reference with no row anywhere is
    // dangling, not ambiguous: drop exactly those ids (idempotent, logged) and keep every strict check for the rest.
    internal static void RepairDanglingReferences(Wizard loaded, IDocumentSession readSession) {
        var ids = loaded?.AlchemyBehavior?.ReagentItemIds;
        if (ids is null || ids.Count == 0 || loaded.CharId == 0 || readSession is null) return;
        var present = ReadRows(readSession).Select(row => row.m_globalID.Full).ToHashSet();
        var dangling = ids.Where(id => id != 0 && !present.Contains(id)).ToList();
        if (dangling.Count == 0) return;
        WizardCollection.CommitCharacterMutation(loaded.CharId, (session, saved) => {
            var savedIds = saved.AlchemyBehavior?.ReagentItemIds;
            if (savedIds is null) return false;
            var rows = ReadRows(session).Select(row => row.m_globalID.Full).ToHashSet();
            var keep = savedIds.Where(id => id == 0 || rows.Contains(id)).ToList();
            if (keep.Count == savedIds.Count) return false;
            saved.AlchemyBehavior.ReagentItemIds = keep;
            return true;
        }, null);
        loaded.AlchemyBehavior.ReagentItemIds = [.. ids.Where(id => id == 0 || present.Contains(id))];
        Logger.Warning("Reagent bag of {0}: dropped {1} dangling reference(s) with no row (saved before the atomic bag).",
            Logger.Args(loaded.CharId, dangling.Count));
    }

    internal static bool TryReadOwnedBag(IDocumentSession session, Wizard saved, out List<ClientReagentItem> owned) {
        owned = [];
        return session is not null && saved is not null && saved.CharId != 0
            && TryValidateBag(saved, ReadRows(session), out _, out owned);
    }

    internal static bool TryValidateBag(Wizard saved, IReadOnlyList<ClientReagentItem> rows,
        out List<ulong> ids, out List<ClientReagentItem> owned) {
        ids = saved?.AlchemyBehavior?.ReagentItemIds ?? [];
        owned = [];
        if (saved is null || saved.CharId == 0 || rows is null || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count) return false;
        foreach (var id in ids) {
            var matches = rows.Where(row => row is not null && row.m_globalID.Full == id).ToList();
            if (matches.Count != 1 || matches[0].m_characterId.Full != saved.CharId
                || matches[0].m_templateID.Full == 0 || matches[0].m_quantity <= 0) return false;
            owned.Add(matches[0]);
        }
        return owned.Select(row => row.m_templateID.Full).Distinct().Count() == owned.Count;
    }

    /// <summary>CLASSIC: publish only the acknowledged reagent bag/count, never a whole stale wizard snapshot.</summary>
    internal static ClientReagentItem PublishCommittedBag(Wizard live, Wizard saved, ClientReagentItem updated) {
        if (live is null || saved is null || updated is null || live.CharId == 0 || live.CharId != saved.CharId
            || updated.m_characterId.Full != saved.CharId || updated.m_globalID.Full == 0 || updated.m_quantity < 0)
            throw new InvalidOperationException("Cannot publish a reagent for a different or invalid character.");
        live.AlchemyBehavior ??= new();
        live.AlchemyBehavior.ReagentItemIds = [.. saved.AlchemyBehavior?.ReagentItemIds ?? []];
        live.AlchemyBehavior.Reagents ??= [];
        var existing = live.AlchemyBehavior.Reagents.FirstOrDefault(row => row.m_globalID.Full == updated.m_globalID.Full
            && row.m_templateID.Full == updated.m_templateID.Full && row.m_characterId.Full == live.CharId);
        if (updated.m_quantity == 0) {
            live.AlchemyBehavior.Reagents = [.. live.AlchemyBehavior.Reagents.Where(row => row.m_globalID.Full != updated.m_globalID.Full)];
            if (existing is not null) existing.m_quantity = 0;
            return existing ?? updated with { };
        }
        if (existing is not null) {
            existing.m_quantity = updated.m_quantity;
            return existing;
        }
        var published = updated with { };
        live.AlchemyBehavior.Reagents.Add(published);
        return published;
    }

    /// <summary>
    /// Removes all reagents from the reagent collection for a given character.
    /// </summary>
    /// <param name="charId">The character ID.</param>
    public static void DeleteReagentBag(ulong charId) {
        using var session = s_store.OpenSession();

        var reagents = session.Query<ClientReagentItem>(collectionName: CollectionName)
            .Where(x => x.m_characterId == charId)
            .ToList();

        foreach (var reagent in reagents) {
            session.Delete(reagent);
        }

        session.SaveChanges();
    }

}
