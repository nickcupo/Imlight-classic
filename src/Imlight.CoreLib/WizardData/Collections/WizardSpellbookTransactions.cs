// CLASSIC: selected learned spells, trainer payment and item-card exclusions publish only after the saved write is acknowledged.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal enum SpellbookMutationStatus { Refused, Unchanged, Committed }
internal enum SpellbookMutationKind { Learn, Train, Exclusion }
internal sealed record SpellbookReceipt(SpellbookMutationKind Kind, uint TemplateId, uint SpellHash,
    ulong ItemId, bool Exclude, int Cost, int TrainingPoints, IReadOnlyList<uint> LearnedSpells,
    IReadOnlyList<uint> ExcludedSpells, IReadOnlyList<IMessage> Messages);
internal sealed class SpellbookMutationDependencies {
    internal Func<uint, Spell> Spell;
    internal Func<uint, SpellTemplate> Template;
    internal Func<IMessage, bool> Prepare;
    internal Action<Wizard> BeforePublish;
}

internal static class WizardSpellbookTransactions {
    internal static readonly AsyncLocal<SpellbookMutationDependencies> TestScope = new();
    internal static bool IsActive => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive;

    // CLASSIC: prepare the actual bounded native frame before Save. The socket actor later re-encodes
    // these fixed generated messages; preparation proves encoding/framing, not socket delivery.
    internal static bool PrepareNative(IMessage message) {
        try {
            if (message is null || message.ServiceId == 0 || TestScope.Value?.Prepare?.Invoke(message) == false) return false;
            var frame = MessageEncoder.Encode(message);
            return frame.Length >= 13 && frame.Length - 13 <= 0x777f
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2)) == 0xf00d
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2)) == frame.Length - 4
                && frame[4] == 0 && frame[5] == 0 && frame[6] == 0 && frame[7] == 0
                && frame[8] == message.ServiceId && frame[9] == message.MessageOrder
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10, 2)) == frame.Length - 9
                && frame[^1] == 0;
        }
        catch { return false; }
    }

    internal static SpellbookMutationStatus TryLearn(Wizard live, uint templateId, out SpellbookReceipt receipt,
        Func<SpellbookReceipt, bool> preparePublication = null, Action<SpellbookReceipt> afterCommit = null,
        ulong? expectedAccountId = null)
        => Commit(live, templateId, SpellbookMutationKind.Learn, null, 0, false, 0,
            out receipt, preparePublication, afterCommit, expectedAccountId, null, null);

    // The caller supplies only an entry from the queried trainer's authoritative inventory, never a client price.
    internal static SpellbookMutationStatus TryTrain(Wizard live, NPCSpellEntry entry, ulong trainerTemplateId,
        out SpellbookReceipt receipt, Func<SpellbookReceipt, bool> preparePublication = null,
        Action<SpellbookReceipt> afterCommit = null, ulong? expectedAccountId = null,
        Func<Wizard, bool> validateFresh = null) {
        receipt = null;
        if (entry is null || entry.TemplateID is 0 or > uint.MaxValue || entry.RequiredSpellID > uint.MaxValue || entry.Level < 0)
            return SpellbookMutationStatus.Refused;
        var detached = new NPCSpellEntry { TemplateID = entry.TemplateID, RequiredSpellID = entry.RequiredSpellID, Level = entry.Level };
        return Commit(live, (uint)detached.TemplateID, SpellbookMutationKind.Train, detached, trainerTemplateId,
            false, 0, out receipt, preparePublication, afterCommit, expectedAccountId, validateFresh, null);
    }

    internal static SpellbookMutationStatus TrySetItemSpellExclusion(Wizard live, ulong itemId, uint templateId,
        bool exclude, out SpellbookReceipt receipt, Func<SpellbookReceipt, bool> preparePublication = null,
        Action<SpellbookReceipt> afterCommit = null, ulong? expectedAccountId = null, int? wireSpellId = null)
        => Commit(live, templateId, SpellbookMutationKind.Exclusion, null, 0, exclude, itemId,
            out receipt, preparePublication, afterCommit, expectedAccountId, null, wireSpellId);

    private sealed record Publication(WizClientObject Object, object NativeBehaviors, ServerWizSpellbookBehavior Book, List<uint> Learned,
        Dictionary<ulong, HashSet<uint>> Exclusions, HashSet<uint> SelectedExclusions,
        ServerMagicSchoolBehavior School, ClientSpellbookBehavior NativeBook, List<SpellIDTracker> NativeLearned,
        IReadOnlyList<SpellIDTracker> ProposedNativeLearned, ClientMagicSchoolBehavior NativeSchool,
        IReadOnlyList<uint> OriginalLearned, IReadOnlyList<uint> OriginalExclusions,
        IReadOnlyList<(SpellIDTracker Tracker, uint Id)> OriginalNativeTrackers);

    private static SpellbookMutationStatus Commit(Wizard live, uint templateId, SpellbookMutationKind kind,
        NPCSpellEntry training, ulong trainer, bool exclude, ulong itemId, out SpellbookReceipt receipt,
        Func<SpellbookReceipt, bool> preparePublication, Action<SpellbookReceipt> afterCommit,
        ulong? expectedAccountId, Func<Wizard, bool> validateFresh, int? wireSpellId) {
        receipt = null;
        if (templateId == 0 || !Usable(live, expectedAccountId) || kind == SpellbookMutationKind.Exclusion && itemId == 0)
            return SpellbookMutationStatus.Refused;
        SpellbookReceipt staged = null;
        Publication publication = null;
        var status = SpellbookMutationStatus.Refused;
        try {
            // Repeat the refusal after acquiring the lane and before opening a session. A queued stale caller must
            // not open another transaction between a lost ACK and the first caller's session close.
            var committed = WizardCollection.WithCharacterLock(live.CharId, () => Usable(live, expectedAccountId)
                && WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
                if (!ValidFresh(session, live, saved, expectedAccountId)) return false;
                if (kind == SpellbookMutationKind.Train && (saved.MagicSchoolBehavior is null || saved.MagicSchoolBehavior.TrainingPoints < 0)) return false;
                var dependencies = TestScope.Value;
                var spell = dependencies?.Spell is { } resolve ? resolve(templateId) : SpellFactory.GetSpell(templateId);
                if (spell is null || spell.m_templateID != templateId) return false;
                var book = saved.SpellbookBehavior;
                var cost = 0;
                var points = saved.MagicSchoolBehavior?.TrainingPoints ?? 0;
                var changed = false;
                IReadOnlyList<uint> selected = Array.AsReadOnly(Array.Empty<uint>());
                if (kind == SpellbookMutationKind.Exclusion) {
                    if (book.ExcludedItemSpellIds is null || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, itemId, out _)) return false;
                    book.ExcludedItemSpellIds.TryGetValue(itemId, out var current);
                    if (current is null && book.ExcludedItemSpellIds.ContainsKey(itemId)) return false;
                    changed = exclude != (current?.Contains(templateId) == true);
                    var proposed = current is null ? new HashSet<uint>() : new HashSet<uint>(current);
                    if (exclude) proposed.Add(templateId); else proposed.Remove(templateId);
                    selected = Array.AsReadOnly(proposed.OrderBy(id => id).ToArray());
                }
                else {
                    changed = !book.LearnedSpellTemplateIds.Contains(templateId);
                    if (kind == SpellbookMutationKind.Train && changed) {
                        if (training is null) return false;
                        var template = dependencies?.Template is { } resolveTemplate ? resolveTemplate(templateId)
                            : CoreObjectFactory.GetCoreTemplate(templateId) as SpellTemplate;
                        if (template is null || !ClassicOctoberTraining.CanTrain(saved, trainer, templateId)
                            || validateFresh?.Invoke(saved) == false) return false;
                        cost = TrainRules.Cost(saved.MagicSchoolBehavior.MagicSchool.ToString(), template.m_sMagicSchoolName);
                        if (TrainRules.Check(false, saved.MagicSchoolBehavior.Level, training.Level, training.RequiredSpellID,
                            id => book.HasSpell((uint)id), points, cost) != TrainRefusal.None) return false;
                        points -= cost;
                    }
                }
                var learned = book.LearnedSpellTemplateIds.ToList();
                if (kind != SpellbookMutationKind.Exclusion && changed) learned.Add(templateId);
                var messages = changed ? Messages(kind, templateId, wireSpellId ?? unchecked((int)templateId), itemId, exclude, points) : Array.Empty<IMessage>();
                staged = new(kind, templateId, spell.m_spellID, itemId, exclude, cost, points,
                    Array.AsReadOnly(learned.ToArray()), selected, Array.AsReadOnly(messages));
                if (!TryPreparePublication(live, kind, itemId, staged.LearnedSpells, out publication)) { staged = null; return false; }
                if (!changed) {
                    // A validated fresh read can rehydrate already-durable aliases without a new grant, debit or notice.
                    try {
                        TestScope.Value?.BeforePublish?.Invoke(live);
                        if (!StillAttached(live, publication, kind, itemId)) throw new InvalidOperationException("Classic saved spellbook projection changed.");
                        Publish(publication, staged, publishTraining: false);
                        status = SpellbookMutationStatus.Unchanged;
                    }
                    catch { WizardCollection.MarkInventorySnapshotUncertain(live); staged = null; throw; }
                    return false;
                }
                // Preparation sees detached receipts and the untouched live aliases, before any selected saved mutation.
                try {
                    if (messages.Any(message => !PrepareNative(message))
                        || preparePublication?.Invoke(staged) == false) { staged = null; return false; }
                }
                catch { staged = null; return false; }
                if (!Usable(live, expectedAccountId) || !StillAttached(live, publication, kind, itemId)) { staged = null; return false; }
                if (kind == SpellbookMutationKind.Exclusion) {
                    if (selected.Count == 0) book.ExcludedItemSpellIds.Remove(itemId);
                    else book.ExcludedItemSpellIds[itemId] = new HashSet<uint>(selected);
                }
                else {
                    book.LearnedSpellTemplateIds.Add(templateId);
                    if (kind == SpellbookMutationKind.Train) saved.MagicSchoolBehavior.TrainingPoints = points;
                }
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                return true;
            }, saved => {
                TestScope.Value?.BeforePublish?.Invoke(live);
                if (!Usable(live, expectedAccountId) || !StillAttached(live, publication, kind, itemId))
                    throw new InvalidOperationException("Classic spellbook publication context changed after acknowledgement.");
                Publish(publication, staged);
                afterCommit?.Invoke(staged);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live)));
            if (committed) status = SpellbookMutationStatus.Committed;
        }
        catch (Exception error) {
            Logger.Error("Classic spellbook mutation for wizard {0} failed: {1}", Logger.Args(live.CharId, error.Message));
            return SpellbookMutationStatus.Refused;
        }
        if (status != SpellbookMutationStatus.Refused) receipt = staged;
        return status;
    }

    private static bool Usable(Wizard live, ulong? expectedAccountId) {
        if (live is null || live.CharId == 0 || live.CharId > ulong.MaxValue - 2
            || Classic.Ambient.AmbientWizards.IsAmbientChar(live.CharId) || WizardCollection.IsInventorySnapshotUncertain(live)
            || live.SpellbookBehavior?.LearnedSpellTemplateIds is null || live.GameObject is null
            || live.GameObject.m_characterId.Full != live.CharId || live.GameObject.m_globalID.Full != live.GameObjectID
            || live.GameObject.m_permID.Full != live.GameObjectID
            || expectedAccountId is { } expected && (expected == 0 || expected != live.AccountId)) return false;
        return live.Account is not { } account || account.AccountId == live.AccountId
            && account.CharacterIds?.Contains(live.CharId) == true;
    }

    private static bool ValidFresh(IDocumentSession session, Wizard live, Wizard saved, ulong? expectedAccountId) {
        if (!Usable(live, expectedAccountId) || saved?.CharId != live.CharId || saved.AccountId != live.AccountId
            || saved.SpellbookBehavior?.LearnedSpellTemplateIds is null || saved.GameObject is null
            || saved.GameObject.m_characterId.Full != saved.CharId || saved.GameObject.m_globalID.Full != saved.GameObjectID
            || saved.GameObject.m_permID.Full != saved.GameObjectID) return false;
        var learned = saved.SpellbookBehavior.LearnedSpellTemplateIds;
        if (learned.Any(id => id == 0) || learned.Distinct().Count() != learned.Count) return false;
        // CharId is an authored full identity, not the Raven document name. Refuse collisions rather than selecting a row.
        var characters = session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(character => character.CharId == live.CharId).Take(2).ToList();
        return characters.Count == 1 && ReferenceEquals(characters[0], saved);
    }

    private static bool TryPreparePublication(Wizard live, SpellbookMutationKind kind, ulong itemId,
        IReadOnlyList<uint> learned, out Publication publication) {
        publication = null;
        var book = live.SpellbookBehavior;
        if (book.ExcludedItemSpellIds is null || kind == SpellbookMutationKind.Train && live.MagicSchoolBehavior is null) return false;
        book.ExcludedItemSpellIds.TryGetValue(itemId, out var selected);
        if (kind == SpellbookMutationKind.Exclusion && selected is null && book.ExcludedItemSpellIds.ContainsKey(itemId)) return false;
        var nativeBooks = live.GameObject.m_inactiveBehaviors?.OfType<ClientSpellbookBehavior>().ToArray() ?? [];
        var nativeSchools = live.GameObject.m_inactiveBehaviors?.OfType<ClientMagicSchoolBehavior>().ToArray() ?? [];
        if (nativeBooks.Length > 1 || kind == SpellbookMutationKind.Train && nativeSchools.Length > 1
            || live.HasInitializedGameObject && (nativeBooks.Length != 1 || kind == SpellbookMutationKind.Train && nativeSchools.Length != 1)) return false;
        var native = nativeBooks.SingleOrDefault();
        if (native is not null && (native.m_spellIDList is null || native.m_spellIDList.Any(tracker => tracker is null || tracker.m_spellID == 0)
            || native.m_spellIDList.Select(tracker => tracker.m_spellID).Distinct().Count() != native.m_spellIDList.Count)) return false;
        var proposed = new List<SpellIDTracker>();
        if (native is not null && kind != SpellbookMutationKind.Exclusion) {
            var remaining = native.m_spellIDList.ToList();
            foreach (var id in learned) {
                var tracker = remaining.FirstOrDefault(candidate => candidate.m_spellID == id);
                if (tracker is not null) remaining.Remove(tracker);
                proposed.Add(tracker ?? new SpellIDTracker { m_spellID = id, m_isRetired = false, m_tieredSpellGroupIndex = -1 });
            }
        }
        publication = new(live.GameObject, live.GameObject.m_inactiveBehaviors, book, book.LearnedSpellTemplateIds, book.ExcludedItemSpellIds, selected,
            live.MagicSchoolBehavior, native, native?.m_spellIDList, proposed,
            kind == SpellbookMutationKind.Train ? nativeSchools.SingleOrDefault() : null,
            book.LearnedSpellTemplateIds.ToArray(), selected?.ToArray() ?? [],
            native?.m_spellIDList.Select(tracker => (Tracker: tracker, Id: tracker.m_spellID)).ToArray() ?? []);
        return true;
    }

    private static bool StillAttached(Wizard live, Publication publication, SpellbookMutationKind kind, ulong itemId) {
        if (publication is null || !ReferenceEquals(live.GameObject, publication.Object)
            || !ReferenceEquals(live.GameObject.m_inactiveBehaviors, publication.NativeBehaviors)
            || !ReferenceEquals(live.SpellbookBehavior, publication.Book)
            || !ReferenceEquals(live.SpellbookBehavior.LearnedSpellTemplateIds, publication.Learned)
            || !live.SpellbookBehavior.LearnedSpellTemplateIds.SequenceEqual(publication.OriginalLearned)
            || !ReferenceEquals(live.SpellbookBehavior.ExcludedItemSpellIds, publication.Exclusions)
            || kind == SpellbookMutationKind.Train && !ReferenceEquals(live.MagicSchoolBehavior, publication.School)) return false;
        if (kind == SpellbookMutationKind.Exclusion) {
            live.SpellbookBehavior.ExcludedItemSpellIds.TryGetValue(itemId, out var selected);
            if (!ReferenceEquals(selected, publication.SelectedExclusions)
                || selected is not null && !selected.SetEquals(publication.OriginalExclusions)) return false;
        }
        return (publication.NativeBook is null || live.GameObject.m_inactiveBehaviors?.Count(behavior => ReferenceEquals(behavior, publication.NativeBook)) == 1
                && ReferenceEquals(publication.NativeBook.m_spellIDList, publication.NativeLearned)
                && publication.NativeLearned.Count == publication.OriginalNativeTrackers.Count
                && publication.NativeLearned.Zip(publication.OriginalNativeTrackers).All(pair => ReferenceEquals(pair.First, pair.Second.Tracker)
                    && pair.First.m_spellID == pair.Second.Id))
            && (kind != SpellbookMutationKind.Train || publication.NativeSchool is null
                || live.GameObject.m_inactiveBehaviors?.Count(behavior => ReferenceEquals(behavior, publication.NativeSchool)) == 1);
    }

    private static void Publish(Publication publication, SpellbookReceipt receipt, bool publishTraining = true) {
        if (receipt.Kind == SpellbookMutationKind.Exclusion) {
            var selected = publication.SelectedExclusions;
            if (selected is not null) { selected.Clear(); selected.UnionWith(receipt.ExcludedSpells); }
            if (receipt.ExcludedSpells.Count == 0) publication.Exclusions.Remove(receipt.ItemId);
            else publication.Exclusions[receipt.ItemId] = selected ?? new HashSet<uint>(receipt.ExcludedSpells);
            return;
        }
        publication.Learned.Clear(); publication.Learned.AddRange(receipt.LearnedSpells);
        if (publication.NativeBook is not null) {
            publication.NativeLearned.Clear(); publication.NativeLearned.AddRange(publication.ProposedNativeLearned);
        }
        if (receipt.Kind == SpellbookMutationKind.Train && publishTraining) {
            publication.School.TrainingPoints = receipt.TrainingPoints;
            if (publication.NativeSchool is not null) publication.NativeSchool.m_trainingPoints = receipt.TrainingPoints;
        }
    }

    private static IMessage[] Messages(SpellbookMutationKind kind, uint templateId, int wireSpellId, ulong itemId,
        bool exclude, int points) => kind switch {
        SpellbookMutationKind.Exclusion => [new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
            SpellID = wireSpellId, DeckID = itemId, Exclude = exclude ? (byte)1 : (byte)0, Success = 1 }],
        SpellbookMutationKind.Train => [new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = unchecked((int)templateId) },
            new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = points },
            new WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE { SpellID = templateId, DisplayText = "WizTraining_00000040", Success = 1 }],
        _ => [new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = unchecked((int)templateId) }],
    };
}
