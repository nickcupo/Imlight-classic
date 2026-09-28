# Classic portal results and marker traffic audit

Local audit, 2026-09-28. No deployment or client changes.

## Portal records

The r806919 client-generated registry does not declare `ResModifyTriggerObject`.
The supplementary server registry declares its state as `bool`; that declaration
loses the actual state string. The following hashes match the property names and
C++ types through `KingsIsleHash.Property`:

| Property | Type | Hash |
| --- | --- | --- |
| m_triggerObjName | std::string | C6E6048B |
| m_triggerObjState | std::string | 7B3D75AB |
| m_secondsToWait | double | 8F3C69B4 |

The actual extracted BINd records decode through the production zone loader:

| Zone / object | State sequence |
| --- | --- |
| Malistaire lair / TeleportToAmbrose | On |
| Burial Ground / MS_SpiritWorldPortal_Death1 instance | On, 30-second wait, candle resets, Off |
| Ancient Tree / MS_SpiritWorldPortal instance | On |

The state-result type hash is 1263108441. All 685 extracted state results carry
three properties. The third, 94EAC863, is an empty string in 683 records. Its only
nonempty values are `Off` in two later Marleybone G14 gauntlet records. Its meaning
is unconfirmed. Those two records retain the field and explicitly fail execution;
unknown added properties also prevent execution. There is no no-op success handler.

The wait type hash is 526762782. The stock decoder reads the first four bytes of
an eight-byte double as uint, turning the Burial Ground's 30 seconds into zero.
The classic registry now preserves the double; stock registry selection is unchanged.
The offline probe found 569 raw wait records, 568 reachable as top-level trigger
results, including fractional values such as 0.1, 0.25 and 6.5 seconds.

The handler addresses the placement's exact zone tag. State is transient on that
zone entity, broadcast with the existing generated client MSG_ENTERSTATE, and
replayed after MSG_NEWOBJECT when a player joins or the object is recreated for
them. Portal access checks use that current state while retaining quest and
feature requirements. No saved dynamod is invented. Other objects and other zone
instances retain their own state.

Tests use the real ZoneLoader deserializer, result dispatcher/executor, ZoneEntity
message handler, RenderComponent and InteractTeleportObjectComponent. They cover
all three portal destinations, untouched other objects, initial denial, opening,
quest gating, closing, state replay, unsupported records, stock gating and an
actual decoded fractional wait. The original event/prerequisite chain and native
portal rendering still need gameplay acceptance; this audit does not claim those
unrelated trigger types are implemented.

## WIZBANG source

Evidence: `/private/tmp/w101c-c7-acceptance/run3/app.log` reports 16,250 inbound
MSG_WIZBANG in 152.05 seconds. The client enters Unicorn Way at 29.03 seconds and
Commons at 147.68 seconds. `server-combat.log:558` and the later `server.log:1069`
report 195 ordinary Unicorn Way entities. The placement list has 212 objects,
including 17 duel circles handled separately.

The production path is:

1. InteractQuestSelectComponent attaches to a template with WizardSelectBehavior
   (also accepting the known WizadSelectBehavior spelling).
2. InteractServiceMementoComponent starts one 1-second timer for each entity with
   any service component, even when its current options are empty.
3. SendWizBang sends one marker to each player in render range on every tick,
   including an unchanged marker or None. InteractQuestSelectComponent's marker
   is always None.

The local r806919 template index and Unicorn Way placement data identify 119
WizardSelect-eligible placements. All 119 are within the 40,000-unit render radius
of the logged entry position (-95.4, -518.6, 18.5); the farthest is about 34,844 units.
Among them are 89 newer Rattlebones decorations:

| Template | Placements |
| --- | ---: |
| WC_Rattlebones_Rubble_03 | 23 |
| WC_Rattlebones_Flag_01 | 22 |
| WC_Rattlebones_Barricade | 16 |
| WC_Rattlebones_Rubble_01 | 14 |
| WC_Rattlebones_Rubble_02 | 14 |

These are eligible for approximately 119 marker sends per second near entry,
before other service-bearing entities. This explains the observed order of
magnitude without requiring duplicate timers. It is an inference from production
predicates and actual placement data, not a measured per-object packet breakdown:
the supplied aggregate logs do not establish the exact contributing IDs, their
option state, or uninterrupted time in range. The existing lifecycle test confirms
one timer per tested memento and one send per explicit tick, including None.

No resend/culling policy was changed. An exact attribution requires a bounded
per-object packet capture or counters during native acceptance. Suppressing None
or unchanged messages also needs scene-load/re-entry validation because the
current resend policy intentionally recovers markers sent while the scene loads.
