using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

// CLASSIC: authored template/native-byte fixtures preserve the real ACK and canonical runtime publication paths.
internal sealed class ElixirRuntimePublicationDependencies {
    internal Func<uint, WizItemTemplate> Template;
    internal Func<GameEffectBase, ByteString> SerializeEffect;
    internal System.Action<Wizard> BeforeEffects;
    internal System.Action<Wizard> BeforeCombatEffects;
}

// CLASSIC: no login/attach packet changes. The existing trusted attach-complete signal
// starts each session's clock; Game timers pause offline and continue while PvP benefits are off.
internal class ElixirService : MessageService {
    internal static readonly AsyncLocal<ElixirRuntimePublicationDependencies> TestRuntimeScope = new();
    private const string Tick = "ClassicElixirTick";
    private Wizard _wizard;
    private readonly ElixirOnlineClock _clock = new(Stopwatch.Frequency);
    private bool _checkpointWarning;
    private bool _timerRunning;
    public ElixirService(SessionActor session) : base(session) { }
    protected static Props Props(SessionActor session) => Akka.Actor.Props.Create(() => new ElixirService(session));

    protected override void ConfigureReceivers() {
        Receive<string>(value => value == Tick, _ => Refresh());
        base.ConfigureReceivers();
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void Attached(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (!Enabled() || _wizard is not null) return;
        var wizard = GetActiveWizard();
        if (wizard is null) return;
        // CLASSIC: validation and initial effects are one synchronous lane publication, not two actor-visible states.
        WizardCollection.WithCharacterLock(wizard.CharId, () => {
            if (!ElixirCollection.LoadValidated(wizard)) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
                return false;
            }
            _wizard = wizard;
            _clock.Begin(ElixirRuntime.RemainingTimers(wizard).Select(t => t.ItemId), Stopwatch.GetTimestamp());
            Refresh(sendTimers: true);
            return true;
        });
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_ELIXIRCHANGED))]
    private void Changed(CLASSIC_FEATURES_PROTOCOL.MSG_ELIXIRCHANGED message) {
        // CLASSIC: the committing caller already emitted the native effect/state/timer receipt; refresh only the clock.
        if (_wizard is not null && message.CharacterId == _wizard.CharId) Refresh();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL))]
    // CLASSIC: this actor's mailbox can lag behind a later duel exit. Refresh current mode only.
    private void DuelEntered(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL message) => Refresh();

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void Won(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) => DuelLeft();
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT))]
    private void Lost(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT message) => DuelLeft();
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE))]
    private void Released(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE message) => DuelLeft();
    private void DuelLeft() => Refresh();

    private void Refresh(bool sendTimers = false) {
        if (_wizard is null || !Enabled() || SessionActor.TransferringOut) return;
        if (WizardCollection.IsInventorySnapshotUncertain(_wizard)) { CloseSession(); return; }
        Checkpoint(publish: true);
        if (WizardCollection.IsInventorySnapshotUncertain(_wizard)) return;
        try {
            var messages = RuntimeWrite(_wizard, () => SynchronizeEffectsLocked(_wizard, sendTimers));
            foreach (var message in messages) SendToSocket(message);
        } catch {
            if (WizardCollection.IsInventorySnapshotUncertain(_wizard)) CloseSession();
            throw;
        }
        var timers = ElixirRuntime.RemainingTimers(_wizard);
        if (timers.Length > 0 && !_timerRunning) {
            Timers.StartPeriodicTimer(Tick, Tick, TimeSpan.FromSeconds(1));
            _timerRunning = true;
        }
        else if (timers.Length == 0 && _timerRunning) { Timers.Cancel(Tick); _timerRunning = false; }
    }

    private void Checkpoint(bool publish) {
        // CLASSIC: an unknown ACK cannot safely charge its elapsed seconds a second time.
        if (WizardCollection.IsInventorySnapshotUncertain(_wizard)) { CloseSession(); return; }
        var elapsed = _clock.Pending(ElixirRuntime.RemainingTimers(_wizard).Select(t => t.ItemId), Stopwatch.GetTimestamp());
        if (elapsed.Count == 0) return;
        var result = ElixirCollection.AdvanceOnline(_wizard, elapsed);
        if (!result.Saved) {
            if (WizardCollection.IsInventorySnapshotUncertain(_wizard)) { CloseSession(); return; }
            if (!_checkpointWarning) Logger.Warning("Elixir online time for {0} could not checkpoint; elapsed time is retained for retry.", Logger.Args(_wizard.CharId));
            _checkpointWarning = true;
            return;
        }
        _clock.Commit(elapsed);
        _checkpointWarning = false;
        foreach (var message in ExpireCommitted(_wizard, result, publish)) SendToSocket(message);
    }

    // CLASSIC: the native EquipItem confirmation transfers an unequipped item back to
    // inventory. Its separate equipment-behavior message only removes the original item
    // and slot. Clear the timer/benefit/effects first, while the client can still find it.
    internal static List<IMessage> ExpireCommitted(Wizard wizard, ElixirResult result, bool publish,
        Func<uint, WizItemTemplate> templateFor = null) {
        if (!result.Saved || wizard is null) return [];
        // CLASSIC: production removal already happened inside its ACK lane. Replaying a receipt never subtracts twice.
        if (result.RuntimeMessages is not null) return publish ? [.. result.RuntimeMessages] : [];
        return RuntimeWrite(wizard, () => RemoveCommittedLocked(wizard, result, publish, templateFor));
    }

    // CLASSIC: called from the collection's afterCommit callback, including the account->character purchase lane.
    internal static List<IMessage> PublishCommittedRuntime(Wizard wizard, ElixirResult result, bool sendTimers = false) {
        if (!result.Saved || wizard is null) return [];
        return RuntimeWrite(wizard, () => {
            TestRuntimeScope.Value?.BeforeEffects?.Invoke(wizard);
            var messages = RemoveCommittedLocked(wizard, result, true, TestRuntimeScope.Value?.Template);
            messages.AddRange(SynchronizeEffectsLocked(wizard, sendTimers));
            return messages;
        });
    }

    // CLASSIC: CombatService alone publishes duel mode. The mode and canonical effect offsets
    // change under the same lane as progression; later service notifications only refresh this state.
    internal static List<IMessage> PublishCombatTransition(Wizard wizard, bool inCombat, bool pvp) {
        if (wizard is null) return [];
        return RuntimeWrite(wizard, () => {
            wizard.IsInDuel = inCombat;
            ElixirRules.SetCombatContext(wizard, inCombat, pvp);
            TestRuntimeScope.Value?.BeforeCombatEffects?.Invoke(wizard);
            return Enabled() ? SynchronizeEffectsLocked(wizard, false) : [];
        });
    }

    // CLASSIC: disconnect/logout can precede an asynchronously held or fleeing seat.
    // Preserve its trusted combat context and stats until the duel actually releases it.
    internal static void DetachCombatSession(Wizard wizard) {
        if (wizard is null) return;
        RuntimeWrite(wizard, () => { wizard.IsInDuel = false; return []; });
    }

    private static List<IMessage> RuntimeWrite(Wizard wizard, Func<List<IMessage>> publish)
        => WizardCollection.WithCharacterLock(wizard.CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard))
                throw new InvalidOperationException("An uncertain elixir snapshot requires authoritative reload.");
            try { return publish(); }
            catch {
                // CLASSIC: partial runtime publication after an ACK is quarantined before another stat writer enters.
                WizardCollection.MarkInventorySnapshotUncertain(wizard);
                throw;
            }
        });

    private static List<IMessage> RemoveCommittedLocked(Wizard wizard, ElixirResult result, bool publish,
        Func<uint, WizItemTemplate> templateFor) {
        var messages = new List<IMessage>();
        foreach (var removed in result.Removed ?? []) {
            var template = templateFor is null
                ? CoreObjectFactory.GetCoreTemplate(removed.TemplateId) as WizItemTemplate
                : templateFor(removed.TemplateId);
            var effects = RemoveEffectsLocked(wizard, removed.ItemId, template);
            if (!publish) continue;
            messages.Add(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER {
                GlobalID = removed.ItemId, TimerTime = 0,
            });
            messages.Add(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
                parentID = removed.ItemId, EffectEnabled = 0,
            });
            foreach (var effect in effects) messages.Add(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID, EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
            messages.Add(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM {
                GlobalID = wizard.GameObjectID, ItemID = removed.ItemId,
            });
        }
        return messages;
    }

    private static List<GameEffectBase> RemoveEffectsLocked(Wizard wizard, ulong id, WizItemTemplate template) {
        var removed = ElixirRuntime.RemoveItemEffects(wizard, id, template);
        if (wizard.GameEffects.Snapshot().Any(effect => effect.m_originatorID == id
            && effect.m_itemSlotID == Imcodec.Cryptography.StringHash.Compute(ElixirRules.SlotName)))
            throw new InvalidOperationException("An elixir's exact canonical effects could not be removed.");
        return removed;
    }

    private static List<IMessage> SynchronizeEffectsLocked(Wizard wizard, bool sendTimers) {
        var messages = new List<IMessage>();
        foreach (var item in wizard.EquipmentBehavior.EquippedItems) {
            var behavior = item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault();
            if (behavior is null) continue;
            var template = TestRuntimeScope.Value?.Template is { } authored ? authored((uint)item.m_templateID.Full)
                : CoreObjectFactory.GetCoreTemplate(item.m_templateID) as WizItemTemplate;
            var wasApplied = behavior.m_statsApplied;
            var enabled = ElixirRuntime.CanApplyEffects(wizard, item, template, false, wizard.IsInDuel)
                && ElixirRules.CanActivate(wizard,
                    item.m_templateID.Full < (1UL << 28) ? ElixirRules.Approved((uint)item.m_templateID.Full) : null);
            if (!enabled) {
                foreach (var effect in RemoveEffectsLocked(wizard, item.m_globalID, template))
                    messages.Add(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT { GameObjectID = wizard.GameObjectID,
                        EffectNameID = effect.m_effectNameID, InternalID = effect.m_internalID });
            } else {
                foreach (var effect in ElixirRuntime.AddApprovedEffects(wizard, item, template, false, wizard.IsInDuel)) {
                    ByteString data;
                    if (TestRuntimeScope.Value?.SerializeEffect is { } serialize) data = serialize(effect);
                    else if (!new ObjectSerializer(Versionable: false).Serialize(effect,
                        (uint)(PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit), out data))
                        throw new InvalidOperationException("An approved elixir effect could not serialize.");
                    if (data.Length == 0) throw new InvalidOperationException("An approved elixir effect serialized empty data.");
                    messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = wizard.GameObjectID, EffectData = data });
                }
                var definition = ElixirRules.Approved((uint)item.m_templateID.Full);
                var applied = wizard.GameEffects.Snapshot().Where(effect => effect.m_originatorID == item.m_globalID
                    && effect.m_itemSlotID == Imcodec.Cryptography.StringHash.Compute(ElixirRules.SlotName)).ToArray();
                if (!behavior.m_statsApplied || applied.Length != definition.Effects.Count
                    || !applied.Select(effect => effect.m_effectNameID).OrderBy(hash => hash)
                        .SequenceEqual(definition.Effects.Select(effect => Imcodec.Cryptography.StringHash.Compute(effect.Name)).OrderBy(hash => hash)))
                    throw new InvalidOperationException("An approved elixir's canonical effects were not completely published.");
            }
            if (wasApplied != enabled || sendTimers) messages.Add(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
                parentID = item.m_globalID, EffectEnabled = (sbyte)(enabled ? 1 : 0),
            });
            behavior.m_statsApplied = enabled;
        }
        if (sendTimers) foreach (var timer in ElixirRuntime.RemainingTimers(wizard))
            messages.Add(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER { GlobalID = timer.ItemId, TimerTime = timer.RemainingSeconds });
        return messages;
    }
    private static bool Enabled() => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Elixirs);
    protected override void OnPreDispose() {
        Timers.Cancel(Tick);
        _timerRunning = false;
        if (_wizard is not null && !WizardCollection.IsInventorySnapshotUncertain(_wizard)) Checkpoint(publish: false);
        base.OnPreDispose();
    }
}
