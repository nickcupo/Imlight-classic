// CLASSIC: passive expressed talents belong to the exact equipped pet. Prepare detached data before a write;
// admit it only after acknowledgement. Removal uses the old admitted values, never today's grown pet/template.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

internal sealed record PetTalentEffect(GameEffectBase Effect, string Category, Spell[] Cards);
internal sealed record PetTalentReceipt(ulong Owner, ulong PetId, IReadOnlyList<PetTalentEffect> Effects);
internal sealed record PetTalentTransition(PetTalentReceipt Receipt, IReadOnlyList<IMessage> Messages);

internal sealed class PetTalentDependencies {
    internal Func<uint, PetTalentTemplate> Talent;
    internal Func<string, PetBoostPlayerStatEffectTemplate> Stat;
    internal Func<GameEffectBase, ByteString> Serialize;
}

internal static class PetTalentRuntime {
    internal static readonly AsyncLocal<PetTalentDependencies> TestScope = new();
    private static readonly ConditionalWeakTable<Wizard, PetTalentReceipt> s_admitted = new();
    private static readonly uint s_petSlot = StringHash.Compute("Pet");

    internal static bool Enabled => ClassicRuntime.IsInitialized
        && ClassicRuntime.Rules.Profile.Id == "october-2010-arc1"
        && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsTalents);

    // CLASSIC: saved maximums remain hereditary baseline values. A derived capacity is never re-saved as a new base.
    internal static IReadOnlyDictionary<string, int> EffectiveMaximums(WizClientObjectItem pet) {
        var behavior = PetProgress.Behavior(pet);
        var maximums = PetProgress.Stats(behavior?.m_maxStats);
        if (!Enabled || behavior is null || behavior.m_level == 0) return maximums;
        foreach (var talent in Expressed(behavior).Where(PetTalentPolicy.IsCapacityTalent))
            foreach (var cap in talent.m_maxStatList) {
                var name = cap.m_name.ToString();
                maximums[name] = (int)Math.Clamp((long)maximums.GetValueOrDefault(name) + cap.m_value, 0, int.MaxValue);
            }
        return maximums;
    }

    internal static PetTalentReceipt Prepare(Wizard wizard, WizClientObjectItem pet) {
        if (!Enabled || wizard is null || pet is null || pet.m_globalID.Full == 0
            || pet.m_characterId != wizard.CharId || PetProgress.Behavior(pet) is not { m_level: > 0 } behavior)
            return null;
        var stats = PetProgress.Stats(behavior.m_currentStats);
        var caps = EffectiveMaximums(pet);
        // A corrupt or later snapshot cannot amplify a passive using out-of-cap/negative attributes.
        var values = PetRules.StatNames.ToDictionary(name => name,
            name => Math.Clamp(stats.GetValueOrDefault(name), 0, Math.Max(0, caps.GetValueOrDefault(name))), StringComparer.Ordinal);
        var effects = new List<PetTalentEffect>();
        foreach (var talent in Expressed(behavior)) {
            foreach (var info in talent.m_effectList ?? []) {
                if (info is StatisticEffectInfo stat && PetTalentPolicy.Binding(stat.m_effectName.ToString()) is { } binding) {
                    var native = TestScope.Value?.Stat is { } resolve ? resolve(binding.Name) : PetTalentNativeEffects.Instance.Find(binding.Name);
                    if (!PetTalentPolicy.IsStatTalent(talent, stat, binding) || !PetTalentPolicy.Matches(native, binding))
                        throw new InvalidOperationException("Native passive pet stat does not match the approved inferred binding: " + binding.Name);
                    var weighted = values[binding.First] + (double)values[binding.Second] + .5 * values["Power"];
                    var value = (float)(weighted * binding.Coefficient);
                    if (!float.IsFinite(value)) throw new InvalidOperationException("Invalid passive pet stat value.");
                    var effect = new WizStatisticEffect { m_lookupIndex = -1,
                        m_effectNameID = StringHash.Compute(binding.Name), m_itemSlotID = s_petSlot, m_originatorID = pet.m_globalID };
                    switch (binding.Field) {
                        case "damage": effect.m_damageBonusPercent = value; break;
                        case "accuracy": effect.m_accuracyBonusPercent = value; break;
                        case "resistance": effect.m_damageReducePercent = value; break;
                        case "healing": effect.m_healBonusPercent = value; break;
                        case "incoming_healing": effect.m_healIncBonusPercent = value; break;
                        // The integer resource aggregator truncates flat bonuses. Preserve the native fractional receipt;
                        // add/remove use that exact same value, without rounding percentages or inventing a minimum.
                        case "health": effect.m_hitPointBonus = value; break;
                        case "mana": effect.m_manaBonus = value; break;
                        case "power_pips": effect.m_powerPipBonusPercent = value; break;
                    }
                    effects.Add(new(effect, "Canonical" + binding.Category, []));
                }
                else if (info is ProvideSpellEffectInfo card && PetTalentPolicy.IsCardTalent(talent, card)
                    && PetTalentPolicy.IsApprovedGrantedSpell(card.m_spellName.ToString())) {
                    var effect = (ProvideSpellEffect)GameEffectFactory.CreateEffectFromInfo(card, s_petSlot);
                    effect.m_originatorID = pet.m_globalID;
                    // SpellFactory returns a fresh card per call. Do not share references with another provider.
                    var cards = SpellFactory.CreateSpellsFromEffect(effect);
                    if (cards is null || cards.Length != effect.m_numSpells)
                        throw new InvalidOperationException("Could not prepare the approved pet card grant.");
                    effects.Add(new(effect, "", cards));
                }
                // Unsupported (including may-cast) effects remain separate; never enter the canonical factory.
            }
        }
        foreach (var entry in effects) entry.Effect.m_internalID = ElixirRuntime.NextEffectId(wizard);
        return new(wizard.CharId, pet.m_globalID.Full, effects.ToArray());
    }

    internal static PetTalentTransition PrepareTransition(Wizard wizard, WizClientObjectItem pet) {
        var receipt = Prepare(wizard, pet);
        if (receipt is null || !HasExactEquippedPet(wizard, pet.m_globalID.Full)) return new(receipt, []);
        var messages = new List<IMessage>();
        if (s_admitted.TryGetValue(wizard, out var prior))
            messages.AddRange(prior.Effects.Select(entry => (IMessage)new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID, EffectNameID = entry.Effect.m_effectNameID,
                InternalID = entry.Effect.m_internalID }));
        foreach (var entry in receipt.Effects) {
            ByteString data;
            if (TestScope.Value?.Serialize is { } serialize) data = serialize(entry.Effect);
            // CLASSIC: use the same compact native effect envelope as EquipmentService.
            else if (!ClassicCoreObjectSerializer.Create(false, SerializerFlags.None).Serialize(entry.Effect,
                PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out data))
                throw new InvalidOperationException("Could not prepare the passive pet effect payload.");
            if (data.Length == 0) throw new InvalidOperationException("Empty passive pet effect payload.");
            messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = wizard.GameObjectID, EffectData = data });
        }
        return new(receipt, messages.ToArray());
    }

    internal static List<GameEffectBase> Publish(Wizard wizard, WizClientObjectItem pet, PetTalentReceipt prepared) {
        if (prepared is null || wizard is null || pet is null || prepared.Owner != wizard.CharId
            || prepared.PetId != pet.m_globalID.Full || pet.m_characterId != wizard.CharId
            || WizardCollection.IsInventorySnapshotUncertain(wizard) || !HasExactEquippedPet(wizard, prepared.PetId)
            || !ReferenceEquals(wizard.EquipmentBehavior.GetItem(prepared.PetId), pet)) return [];
        Retire(wizard);
        foreach (var entry in prepared.Effects) {
            if (entry.Effect is WizStatisticEffect stat)
                CharacterEffectHelper.AddStatisticEffectToStats(wizard.GameStats, entry.Category, stat);
            foreach (var card in entry.Cards) wizard.AddTemporarySpell(card);
            wizard.GameEffects.Add(entry.Effect);
        }
        s_admitted.Add(wizard, prepared);
        return prepared.Effects.Select(entry => entry.Effect).ToList();
    }

    internal static List<GameEffectBase> Remove(Wizard wizard, ulong petId) {
        if (!s_admitted.TryGetValue(wizard, out var receipt) || receipt.PetId != petId) return [];
        return Retire(wizard);
    }

    internal static void RetireForRebuild(Wizard wizard) => Retire(wizard, reset: true);

    private static List<GameEffectBase> Retire(Wizard wizard, bool reset = false) {
        if (!s_admitted.TryGetValue(wizard, out var receipt)) return [];
        foreach (var entry in receipt.Effects) {
            if (!reset && entry.Effect is WizStatisticEffect stat)
                CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, entry.Category, stat);
            wizard.GameEffects.Remove(entry.Effect);
            foreach (var card in entry.Cards) {
                var book = wizard.SpellbookBehavior?.TemporarySpells;
                var index = book?.FindIndex(candidate => ReferenceEquals(candidate, card)) ?? -1;
                if (index >= 0) book.RemoveAt(index);
            }
        }
        s_admitted.Remove(wizard);
        return receipt.Effects.Select(entry => entry.Effect).ToList();
    }

    internal static bool HasExactEquippedPet(Wizard wizard, ulong petId) {
        var equipment = wizard?.EquipmentBehavior;
        return petId != 0 && equipment?.SlotList?.Count(slot => slot is { SlotType: EquipmentSlotType.Pet }) == 1
            && equipment.SlotList.Any(slot => slot is { SlotType: EquipmentSlotType.Pet } && slot.ItemId == petId)
            && equipment.EquippedItemIds?.Count(id => id == petId) == 1
            && equipment.EquippedItems?.Count(item => item is not null && item.m_globalID.Full == petId) == 1
            && wizard.InventoryBehavior?.InventoryItemIds?.Contains(petId) != true
            && wizard.StorageBehavior?.BankItemIds?.Contains(petId) != true
            && wizard.InventoryBehavior?.Items?.Any(item => item is not null && item.m_globalID.Full == petId) != true
            && wizard.StorageBehavior?.Items?.Any(item => item is not null && item.m_globalID.Full == petId) != true;
    }

    private static IEnumerable<PetTalentTemplate> Expressed(ClientPetItemBehavior behavior) {
        foreach (var id in (behavior.m_expressedTalents ?? []).Distinct().Take(PetRules.MaxTalents)) {
            if (behavior.m_allTalents?.Contains(id) != true) continue;
            var talent = TestScope.Value?.Talent is { } resolve ? resolve(id) : CoreObjectFactory.GetCoreTemplate(id) as PetTalentTemplate;
            if (talent is not null && PetProgress.TalentId(talent.m_talentName.ToString()) == id) yield return talent;
        }
    }
}
