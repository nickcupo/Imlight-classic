using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

// CLASSIC: no login/attach packet changes. The existing trusted attach-complete signal
// starts each session's clock; Game timers pause offline and continue while PvP benefits are off.
internal class ElixirService : MessageService {
    private const string Tick = "ClassicElixirTick";
    private Wizard _wizard;
    private readonly ElixirOnlineClock _clock = new(Stopwatch.Frequency);
    private bool _checkpointWarning;
    private bool _timerRunning;
    private bool _combat;
    private bool _pvp;
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
        if (wizard is null || !ElixirCollection.LoadValidated(wizard)) return;
        _wizard = wizard;
        _combat = wizard.IsInDuel;
        _pvp = _combat && Classic.Arena.ClassicArena.IsArenaZone(wizard.Zone);
        _clock.Begin(ElixirRuntime.RemainingTimers(wizard).Select(t => t.ItemId), Stopwatch.GetTimestamp());
        Refresh(sendTimers: true);
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_ELIXIRCHANGED))]
    private void Changed(CLASSIC_FEATURES_PROTOCOL.MSG_ELIXIRCHANGED message) {
        if (_wizard is not null && message.CharacterId == _wizard.CharId) Refresh(sendTimers: true);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL))]
    private void DuelEntered(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL message) {
        _combat = true;
        _pvp = message.Duel?.Duel?.m_bPVP ?? true; // unknown duel contexts suppress benefits.
        Refresh();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void Won(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) => DuelLeft();
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT))]
    private void Lost(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT message) => DuelLeft();
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE))]
    private void Released(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE message) => DuelLeft();
    private void DuelLeft() { _combat = _pvp = false; Refresh(); }

    private void Refresh(bool sendTimers = false) {
        if (_wizard is null || !Enabled() || SessionActor.TransferringOut) return;
        Checkpoint(publish: true);
        WizardCollection.WithCharacterLock(_wizard.CharId, () => {
            foreach (var item in _wizard.EquipmentBehavior.EquippedItems) {
                if (CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template
                    || !ElixirRuntime.IsElixir(template)) continue;
                var behavior = item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault();
                var wasApplied = behavior?.m_statsApplied ?? false;
                var enabled = ElixirRuntime.CanApplyEffects(_wizard, item, template, _pvp, _combat);
                if (!enabled) SendRemoved(ElixirRuntime.RemoveItemEffects(_wizard, item.m_globalID, template));
                else {
                    var serializer = new ObjectSerializer(Versionable: false);
                    foreach (var effect in ElixirRuntime.AddApprovedEffects(_wizard, item, template, _pvp, _combat)) {
                        if (!serializer.Serialize(effect, (uint)(PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit), out var data))
                            throw new InvalidOperationException("An approved elixir effect could not serialize.");
                        SendToSocket(new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = _wizard.GameObjectID, EffectData = data });
                    }
                }
                if (behavior is not null && (wasApplied != enabled || sendTimers)) {
                    behavior.m_statsApplied = enabled;
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
                        parentID = item.m_globalID, EffectEnabled = (sbyte)(enabled ? 1 : 0),
                    });
                }
            }
            return true;
        });
        var timers = ElixirRuntime.RemainingTimers(_wizard);
        if (sendTimers) foreach (var timer in timers) SendTimer(timer.ItemId, timer.RemainingSeconds);
        if (timers.Length > 0 && !_timerRunning) {
            Timers.StartPeriodicTimer(Tick, Tick, TimeSpan.FromSeconds(1));
            _timerRunning = true;
        }
        else if (timers.Length == 0 && _timerRunning) { Timers.Cancel(Tick); _timerRunning = false; }
    }

    private void Checkpoint(bool publish) {
        var elapsed = _clock.Pending(ElixirRuntime.RemainingTimers(_wizard).Select(t => t.ItemId), Stopwatch.GetTimestamp());
        if (elapsed.Count == 0) return;
        var result = ElixirCollection.AdvanceOnline(_wizard, elapsed);
        if (!result.Saved) {
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
        return WizardCollection.WithCharacterLock(wizard.CharId, () => {
            var messages = new List<IMessage>();
            foreach (var removed in result.Removed ?? []) {
                var template = templateFor is null
                    ? CoreObjectFactory.GetCoreTemplate(removed.TemplateId) as WizItemTemplate
                    : templateFor(removed.TemplateId);
                var effects = ElixirRuntime.RemoveItemEffects(wizard, removed.ItemId, template);
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
        });
    }

    private void SendRemoved(System.Collections.Generic.IEnumerable<GameEffectBase> effects) {
        foreach (var effect in effects) SendToSocket(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
            GameObjectID = _wizard.GameObjectID, EffectNameID = effect.m_effectNameID, InternalID = effect.m_internalID,
        });
    }
    private void SendTimer(ulong id, uint seconds) => SendToSocket(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER {
        GlobalID = id, TimerTime = seconds,
    });
    private static bool Enabled() => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Elixirs);
    protected override void OnPreDispose() {
        Timers.Cancel(Tick);
        _timerRunning = false;
        if (_wizard is not null) Checkpoint(publish: false);
        base.OnPreDispose();
    }
}
