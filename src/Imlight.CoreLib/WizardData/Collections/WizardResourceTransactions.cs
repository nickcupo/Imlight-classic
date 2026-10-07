// CLASSIC: result/tutorial refills save only the selected current resource and publish after its ACK.
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal enum ResourceMutationStatus { Refused, Unchanged, Committed }
internal enum ResourceKind { Health, Mana }
internal sealed record ResourceReceipt(ResourceKind Kind, ulong GameObjectId, int Value, int RuntimeMax, int ClientMax) {
    // Reconstruct from immutable scalars: preparation/publication cannot alter the staged amount.
    internal IMessage Message => Kind == ResourceKind.Health
        ? new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH { CharacterID = GameObjectId, NewHealth = Value, NewHealthMax = ClientMax }
        : new WIZARD_12_PROTOCOL.MSG_UPDATEMANA { Mana = Value, MaxMana = ClientMax };
}
internal sealed class ResourceMutationDependencies {
    internal Func<IMessage, bool> Prepare;
    internal Action<Wizard> BeforePublish;
}
internal static class WizardResourceTransactions {
    internal static readonly AsyncLocal<ResourceMutationDependencies> TestScope = new();
    internal static bool IsActive => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive;
    private sealed record ManaRequest(bool Flat, int Amount, float Percent);
    private sealed record Publication(ServerWizGameStats Stats, WizClientObject Object, int Current, int Maximum, Wizard Context);

    internal static ResourceMutationStatus TryRefillHealth(Wizard live, out ResourceReceipt receipt,
        Func<ResourceReceipt, bool> preparePublication = null, Action<ResourceReceipt> afterCommit = null,
        ulong? expectedAccountId = null)
        => Commit(live, ResourceKind.Health, null, out receipt, preparePublication, afterCommit, expectedAccountId);
    internal static ResourceMutationStatus TryRefillMana(Wizard live, out ResourceReceipt receipt,
        Func<ResourceReceipt, bool> preparePublication = null, Action<ResourceReceipt> afterCommit = null,
        ulong? expectedAccountId = null)
        => Commit(live, ResourceKind.Mana, new(false, 0, 0), out receipt, preparePublication, afterCommit, expectedAccountId);
    internal static ResourceMutationStatus TryApplyMana(Wizard live, ResAddMana result, out ResourceReceipt receipt,
        Func<ResourceReceipt, bool> preparePublication = null, Action<ResourceReceipt> afterCommit = null,
        ulong? expectedAccountId = null) {
        receipt = null;
        return result is null ? ResourceMutationStatus.Refused : Commit(live, ResourceKind.Mana,
            new(result.m_useFlat, result.m_manaFlat, result.m_manaPercent), out receipt,
            preparePublication, afterCommit, expectedAccountId);
    }

    internal static bool PrepareNative(IMessage message) {
        try {
            if (message is null || message.ServiceId == 0 || TestScope.Value?.Prepare?.Invoke(message) == false) return false;
            var frame = MessageEncoder.Encode(message);
            return frame.Length >= 13 && frame.Length - 13 <= 0x777f
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2)) == 0xf00d
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2)) == frame.Length - 4
                && frame[4] == 0 && frame[5] == 0 && frame[6] == 0 && frame[7] == 0
                && frame[8] == message.ServiceId && frame[9] == message.MessageOrder
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10, 2)) == frame.Length - 9 && frame[^1] == 0;
        }
        catch { return false; }
    }

    private static ResourceMutationStatus Commit(Wizard live, ResourceKind kind, ManaRequest request,
        out ResourceReceipt receipt, Func<ResourceReceipt, bool> preparePublication,
        Action<ResourceReceipt> afterCommit, ulong? accountId) {
        receipt = null;
        if (!Usable(live, accountId)) return ResourceMutationStatus.Refused;
        ResourceReceipt staged = null;
        Publication publication = null;
        var status = ResourceMutationStatus.Refused;
        try {
            var committed = WizardCollection.WithCharacterLock(live.CharId, () => Usable(live, accountId)
                && WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
                    if (!ValidFresh(session, live, saved, accountId) || !TryContext(live, saved, session, out var context)) return false;
                    var stats = live.GameStats;
                    var maximum = kind == ResourceKind.Health ? stats.m_baseHitpoints : stats.m_baseMana;
                    if (maximum < 0 || kind == ResourceKind.Health && maximum == 0) return false;
                    var table = WizardProgressionTransactions.LevelInfo(stats.MagicSchool, stats.Level);
                    if (table is null || table.m_hitpoints <= 0 || table.m_mana < 0) return false;
                    var value = maximum;
                    if (kind == ResourceKind.Mana && !TryMana(request, maximum, out value)) return false;
                    staged = new(kind, live.GameObjectID, value, maximum,
                        kind == ResourceKind.Health ? table.m_hitpoints : table.m_mana);
                    publication = new(stats, live.GameObject,
                        kind == ResourceKind.Health ? stats.m_currentHitpoints : stats.m_currentMana, maximum, context);
                    try {
                        if (preparePublication?.Invoke(staged) == false || !PrepareNative(staged.Message)) { staged = null; return false; }
                    }
                    catch { staged = null; return false; }
                    if (!StillAttached(live, publication, kind, accountId)) { staged = null; return false; }
                    var current = kind == ResourceKind.Health ? saved.GameStats.m_currentHitpoints : saved.GameStats.m_currentMana;
                    if (current == value) {
                        // A healthy fresh read can restore the selected live scalar and repeat its required packet.
                        // No SaveChanges occurs; a failed publication still quarantines inside this held lane.
                        try { Publish(live, publication, staged, accountId, afterCommit); status = ResourceMutationStatus.Unchanged; }
                        catch { WizardCollection.MarkInventorySnapshotUncertain(live); staged = null; throw; }
                        return false;
                    }
                    if (kind == ResourceKind.Health) saved.GameStats.m_currentHitpoints = value;
                    else saved.GameStats.m_currentMana = value;
                    WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                    return true;
                }, saved => Publish(live, publication, staged, accountId, afterCommit),
                    onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live)));
            if (committed) status = ResourceMutationStatus.Committed;
        }
        catch (Exception error) {
            Logger.Error("Classic resource reward for wizard {0} failed: {1}", Logger.Args(live.CharId, error.Message));
            return ResourceMutationStatus.Refused;
        }
        if (status != ResourceMutationStatus.Refused) receipt = staged;
        return status;
    }

    private static void Publish(Wizard live, Publication publication,
        ResourceReceipt receipt, ulong? accountId, Action<ResourceReceipt> afterCommit) {
        TestScope.Value?.BeforePublish?.Invoke(live);
        if (!StillAttached(live, publication, receipt.Kind, accountId))
            throw new InvalidOperationException("Classic resource publication context changed.");
        if (receipt.Kind == ResourceKind.Health) publication.Stats.m_currentHitpoints = receipt.Value;
        else publication.Stats.m_currentMana = receipt.Value;
        afterCommit?.Invoke(receipt);
    }

    private static bool TryMana(ManaRequest request, int maximum, out int value) {
        value = 0;
        if (request is null) return false;
        if (request.Flat) value = Math.Clamp(request.Amount, 0, maximum);
        else {
            if (!float.IsFinite(request.Percent)) return false;
            if (request.Percent <= 0) value = maximum;
            else {
                var product = request.Percent * maximum; // Preserve native float multiplication and integer truncation.
                if (!float.IsFinite(product) || product >= 2147483648f || product < 0) return false;
                value = Math.Clamp((int)product, 0, maximum);
            }
        }
        return true;
    }

    private static bool Usable(Wizard live, ulong? accountId) {
        if (live?.CharId is not > 0 || live.CharId > ulong.MaxValue - 2 || live.GameStats is null
            || WizardCollection.IsInventorySnapshotUncertain(live) || Classic.Ambient.AmbientWizards.IsAmbientChar(live.CharId)
            || live.GameObject?.m_characterId.Full != live.CharId || live.GameObject.m_globalID.Full != live.GameObjectID
            || live.GameObject.m_permID.Full != live.GameObjectID || accountId is { } expected && (expected == 0 || expected != live.AccountId)) return false;
        return live.Account is not { } account || account.AccountId == live.AccountId && account.CharacterIds?.Contains(live.CharId) == true;
    }
    private static bool ValidFresh(IDocumentSession session, Wizard live, Wizard saved, ulong? accountId) {
        if (!Usable(live, accountId) || saved?.CharId != live.CharId || saved.AccountId != live.AccountId || saved.GameStats is null
            || saved.GameObject?.m_characterId.Full != saved.CharId || saved.GameObject.m_globalID.Full != saved.GameObjectID
            || saved.GameObject.m_permID.Full != saved.GameObjectID) return false;
        var matches = session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(character => character.CharId == live.CharId).Take(2).ToList();
        return matches.Count == 1 && ReferenceEquals(matches[0], saved);
    }
    private static bool StillAttached(Wizard live, Publication publication,
        ResourceKind kind, ulong? accountId) => publication is not null && Usable(live, accountId)
        && ReferenceEquals(publication.Stats, live.GameStats) && ReferenceEquals(publication.Object, live.GameObject)
        && publication.Maximum == (kind == ResourceKind.Health ? live.GameStats.m_baseHitpoints : live.GameStats.m_baseMana)
        && publication.Current == (kind == ResourceKind.Health ? live.GameStats.m_currentHitpoints : live.GameStats.m_currentMana)
        && WizardProgressionTransactions.RuntimeContextMatches(live, publication.Context);

    private static bool TryContext(Wizard live, Wizard saved, IDocumentSession session, out Wizard context) {
        context = null;
        if (WizardProgressionTransactions.RuntimeContextMatches(live, saved)) { context = saved; return true; }
        if (saved?.MagicSchoolBehavior is not { } school) return false;
        // Read-only equivalence for existing attachment clamps/rental expiry. Normalize only detached selected
        // context copies: the tracked saved level, XP, equipment and item documents are never changed here.
        var equipment = saved.EquipmentBehavior;
        var shadow = new Wizard {
            CharId = saved.CharId, GameStats = saved.GameStats,
            MagicSchoolBehavior = new() { MagicSchool = school.MagicSchool, Level = school.Level,
                ExperiencePoints = school.ExperiencePoints, TrainingPoints = school.TrainingPoints, OverflowXp = school.OverflowXp },
            EquipmentBehavior = equipment is null ? null : new() { EquippedItemIds = equipment.EquippedItemIds?.ToList(),
                SlotList = equipment.SlotList?.ToList(), EquippedItems = equipment.EquippedItems },
            InventoryBehavior = saved.InventoryBehavior, StorageBehavior = saved.StorageBehavior,
        };
        if (!WizardProgressionTransactions.TryNormalizeAttachedRuntime(live, shadow, session, out _)
            || !WizardProgressionTransactions.RuntimeContextMatches(live, shadow)) return false;
        context = shadow; return true;
    }
}
