using Akka.Actor;
using Imcodec.IO;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;
using System;
using System.Linq;
using System.Collections.Generic;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Monstrology;

internal sealed class MonstrologyService(SessionActor session) : MessageService(session) {
    private readonly HashSet<string> _announcedExtractions = new();
    // Separate from Classic.OwnedMinionControl. No legacy profile gate; explicit opt-in until stock contract is validated.
    internal static bool Enabled => bool.TryParse(ConfigurationManager.Settings["Classic.Monstrology"].AsString(), out var enabled) && enabled;
    private bool Available => SessionActor.MonstrologySession.Allows(Enabled);
    public static Props Props(SessionActor parent) => Akka.Actor.Props.Create(() => new MonstrologyService(parent));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void Attached(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        _announcedExtractions.Clear();
        MonstrologySessionPolicy.Bind(GetActiveWizard(), SessionActor.MonstrologySession);
        if (!Available) return;
        SendProgression(); // New attach/reconnect reads persistent state; no cached zero balance.
    }
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME))]
    private void RequestTome(WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME message) {
        var wizard = GetActiveWizard();
        if (!Available || wizard == null || (message.GlobalID != wizard.CharId && message.GlobalID != wizard.GameObjectID)) return;
        SendProgression();
        var state = MonstrologyRepository.ForPlayers().Read(wizard.CharId);
        Logger.Information("Monstrology tome: wizard {0} level {1}, {2} creatures with Animus",
            Logger.Args(wizard.CharId.ToString(), state.Level.ToString(), state.Animus.Count.ToString()));
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME {
            GlobalID = wizard.GameObjectID,
            MonsterData = new ByteString(MonstrologyTomeCodec.Encode(state.Animus))
        }); // Never echo incoming MonsterData; authoritative raw binary only.
    }
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICREQUESTCREATE))]
    private void RequestCreate(WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICREQUESTCREATE message) {
        var wizard = GetActiveWizard();
        if (!Available || wizard == null || (message.GlobalID != wizard.CharId && message.GlobalID != wizard.GameObjectID)) return;
        var validKind = MonstrologyCreation.TryKind(message.RequestType, out var kind);
        // CLASSIC: every creation request and its result at Information level, so a wrong button mapping shows.
        Logger.Information("Monstrology create: wizard {0} RequestType {1} ({2}) creature {3}",
            Logger.Args(wizard.CharId.ToString(), message.RequestType.ToString(), validKind ? kind.ToString() : "invalid",
                message.MobTemplate.ToString()));
        if (!validKind || message.MobTemplate == 0) {
            InformGameClient("Invalid Monstrology creation request.");
            return;
        }
        var creature = CoreObjectFactory.GetCoreTemplate(message.MobTemplate);
        var metadata = creature?.m_behaviors?.OfType<MobMonsterMagicBehaviorTemplate>().SingleOrDefault();
        if (metadata == null) { InformGameClient("This creature has no Monstrology recipe."); return; }
        var mob = MonstrologyMetadata.ReadMob(metadata);
        var collected = mob.CollectedTemplate != 0 ? mob.CollectedTemplate : message.MobTemplate;
        var cost = MonstrologyCreation.Cost(mob, kind);
        MonstrologyCard card = null;
        WizClientObjectItem guest = null;
        ByteString guestBytes = default;
        uint output;
        if (kind == MonstrologyCreationKind.HouseGuest) {
            output = cost.KnownOutputTemplate;
            if (output == 0 || CoreObjectFactory.GetCoreTemplate(output) is not WizItemTemplate
                || wizard.InventoryBehavior.IsFull) {
                InformGameClient("House guest recipe or backpack capacity is unavailable."); return;
            }
            guest = CoreObjectFactory.FinalizeCoreObject(output) as WizClientObjectItem;
            if (guest == null || guest.m_globalID == 0) { InformGameClient("Invalid house guest output identity."); return; }
            CoreObjectFactory.InitializeCoreObjectBehaviors(guest, output);
            guest.m_characterId = wizard.CharId;
            if (!new CoreObjectSerializer(behaviors: Imcodec.ObjectProperty.SerializerFlags.None).Serialize(guest,24,out guestBytes)) {
                InformGameClient("House guest delivery could not be serialized."); return;
            }
        } else {
            if (!MonstrologyCardCatalog.TryResolve(collected, kind, out card)) {
                InformGameClient("No matching installed Monstrology card is available."); return;
            }
            output = card.TemplateId;
        }
        var request = new AnimusCreation(Guid.NewGuid().ToString("N"), wizard.CharId, collected,
            output, cost.Animus, cost.Gold, true, kind);
        MonstrologyResult result;
        int gold = 0;
        try {
            result = SessionActor.MonstrologySession.WithPermission(Enabled, () =>
                MonstrologyRepository.CreateCard(wizard.CharId, request, out gold, guest: guest,
                afterCommit: persisted => {
                    // Publish before the shared write lane is released: stale whole-state saves must not undo delivery.
                    wizard.GameStats.m_currentGold = persisted.GameStats.m_currentGold;
                    if (guest == null) wizard.SpellbookBehavior.TreasureCardTemplateIds = persisted.SpellbookBehavior.TreasureCardTemplateIds?.ToList() ?? [];
                    else {
                        wizard.InventoryBehavior.InventoryItemIds = persisted.InventoryBehavior.InventoryItemIds.ToList();
                        wizard.InventoryBehavior.Items.Add(guest);
                    }
                }));
        }
        catch (Exception ex) {
            Logger.Warning("Monstrology create: wizard {0} {1} creature {2} failed: {3}",
                Logger.Args(wizard.CharId.ToString(), kind.ToString(), collected.ToString(), ex.Message));
            InformGameClient("Monstrology creation could not be committed. Refresh the tome before retrying."); return;
        }
        Logger.Information("Monstrology create: wizard {0} {1} creature {2} -> template {3}: {4} (Animus {5}, gold {6})",
            Logger.Args(wizard.CharId.ToString(), kind.ToString(), collected.ToString(), output.ToString(), result.ToString(),
                cost.Animus.ToString(), cost.Gold.ToString()));
        if (result != MonstrologyResult.Applied) { InformGameClient(CreationFailureText(result)); return; }
        if (guest == null) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                SpellID = unchecked((int)Imcodec.Cryptography.StringHash.Compute(card.SpellName)), EnchantmentID = 0
            });
        } else {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.GameObjectID, SerializedItem = guestBytes });
        }
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = gold, MaxGold = wizard.GameStats.m_baseGoldPouch });
        RequestTome(new WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME { GlobalID = wizard.GameObjectID });
        // CLASSIC: say what was made and where it went.
        InformGameClient(guest == null
            ? $"Monstrology: you made a {(kind == MonstrologyCreationKind.KillCard ? "kill" : "summon")} Treasure Card ({CardDisplayName(card)}). It is in your Treasure Cards (spellbook, Treasure Card tab)."
            : "Monstrology: you made a house guest. It is in your backpack.");
    }

    private static string CreationFailureText(MonstrologyResult result) => result switch {
        MonstrologyResult.InsufficientAnimus => "Monstrology: not enough Animus for that.",
        MonstrologyResult.InsufficientGold => "Monstrology: not enough gold for that.",
        _ => "Monstrology creation refused: " + result,
    };

    private static string CardDisplayName(MonstrologyCard card) {
        var template = CoreObjectFactory.GetCoreTemplate(card.TemplateId) as SpellTemplate;
        var name = template?.m_displayName is { Length: > 0 } key ? Locale.GetEnglishName(key) : "";
        return string.IsNullOrEmpty(name) ? card.SpellName : name;
    }
    [MessageHandler(typeof(MonstrologyExtractionCommitted))]
    private void ExtractionCommitted(MonstrologyExtractionCommitted message) {
        var wizard = GetActiveWizard();
        if (!Available || wizard == null || wizard.CharId != message.OwnerId || string.IsNullOrEmpty(message.OperationId)
            || _announcedExtractions.Contains(message.OperationId)) return;
        var state = MonstrologyRepository.ForPlayers().Read(wizard.CharId);
        if (!state.Extractions.TryGetValue(message.OperationId, out var receipt)) return;
        Logger.Information("Monstrology extraction: wizard {0} creature {1} +{2} Animus, +{3} XP",
            Logger.Args(wizard.CharId.ToString(), receipt.Creature.ToString(), receipt.Animus.ToString(), receipt.Experience.ToString()));
        var bytes = MonstrologyContracts.EncodeEssence(wizard.GameObjectID, receipt);
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATECOLLECTEDESSENCES { EssenceData = new ByteString(bytes) });
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_DISPLAYCOLLECTEDESSENCES { EssenceData = new ByteString(bytes) });
        _announcedExtractions.Add(message.OperationId);
        var oldLevel = wizard.GameStats.m_monsterMagicLevel;
        SendProgression();
        if (state.Level > oldLevel) SendToSocket(new WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICLEVELUP {
            GlobalID = wizard.GameObjectID, NewLevel = state.Level
        });
    }
    private void SendProgression() {
        var wizard = GetActiveWizard();
        if (!Available || wizard == null) return;
        var state = MonstrologyRepository.ForPlayers().Read(wizard.CharId);
        wizard.GameStats.m_monsterMagicLevel = checked((byte)state.Level);
        wizard.GameStats.m_monsterMagicXP = state.Experience;
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP {
            GlobalID = wizard.GameObjectID, XP = state.Experience, Level = state.Level
        });
    }
}
