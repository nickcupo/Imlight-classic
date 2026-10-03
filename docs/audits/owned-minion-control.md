# Optional owned-minion control: duel-core handoff

2026-10-01. Implemented only in the isolated `wt-server-enhancements` worktree. No live/shared server, database, account, GUI, deploy, commit or push actions were performed by this worker.

## Internal integration contract

Namespace `Imlight.CoreLib.Shared.Packets`, nested in `COMBAT_106_PROTOCOL`, service 106:

| Order | Internal type | Fields |
|---|---|---|
| 28 | `MSG_OWNEDMINIONREQUEST` | `IActorRef OwnerActor; ulong DuelID; int Round; ulong MinionID; uint RequestID; byte MoveType; byte SpellSelection; uint SpellTarget; bool Query` |
| 29 | `MSG_OWNEDMINIONRESPONSE` | `IActorRef OwnerActor; ulong DuelID; int Round; ulong MinionID; uint RequestID; bool Accepted; OwnedMinionStatus Status; OwnedMinionSnapshot[] Snapshots` |
| 30 | `MSG_OWNEDMINIONDISABLE` | `IActorRef OwnerActor` |

`OwnerActor` is injected from the authenticated session by CombatService and is never a client wire field. Replies are delivered to that owner actor. The parent owns CombatService, extension framing/decoding and client integration.

`OwnedMinionSnapshot` fields are exactly:

```csharp
ulong OwnerID;
ulong MinionID;
byte Slot;
byte Team;
int Health;
byte GenericPips;
byte PowerPips;
byte[] HandData;
byte[] ParticipantData;
bool HasOrder;
byte MoveType;
byte SpellSelection;
uint SpellTarget;
```

`HandData` is the current Hand, non-versionable ObjectSerializer, SerializerFlags.None, property mask 5. `ParticipantData` uses the same serializer and property mask 4. The parent's explicit JSON projection represents the byte arrays as base64. Do not serialize the internal response object or OwnerActor directly.

The conservative aggregate budget is 22,000 bytes, accounting for base64 expansion and per-snapshot/response JSON overhead. No partial hand or truncated participant is advertised. Unavailable/oversized snapshots return `SnapshotUnavailable`, no snapshots, and no new opt-in/order. If an existing owner's periodic snapshot becomes unavailable, that owner's control is disabled and manual orders are withdrawn in planning.

Statuses, serialized as byte internally and names by the parent's JSON bridge:

```text
0 Accepted          1 InvalidDuel       2 InvalidRound
3 NotPlanning       4 InvalidOwner      5 NotMyth
6 NotOwnedMinion    7 NotOptedIn        8 InvalidMove
9 InvalidCard      10 InsufficientPips 11 InvalidTarget
12 RequestReplay   13 SnapshotUnavailable
14 UnsupportedSummon                  15 Disabled
```

## Request semantics

* `Query=true, MinionID=0` opts the live authenticated Myth wizard into this duel and returns all its currently live controllable minions. A query naming a specific minion first validates its ownership. Every successful query/order response still carries the full available owner list.
* Opt-in persists across rounds, scoped to the owner object identity and this duel. Accepted orders and request high-water marks reset on a new round. Request IDs must strictly increase within a round, including across a disable/re-enable handshake; rejected requests do not consume a new ID. Same/older accepted IDs are rejected rather than replacing a later order.
* `DuelID` must equal the authoritative duel ID; `Round` must equal the authoritative current round. Only planning permits query/order changes. The one-second completion grace remains cancellable by an opt-in/ChangeMind that makes completion incomplete, using the remaining original planning deadline.
* Moves: Attack=0, Pass=3, ChangeMind=4. Flee=1, Discard=2, enchant and unknown values are rejected for minion control. Pass is a submitted order; ChangeMind withdraws it and restores the saved AI fallback, so it does not count as a submitted minion order.
* `SpellSelection` is the current hand index. `SpellTarget` is a **raw sigil slot**, not the normal client's bitmask. `uint.MaxValue` is the no-selection sentinel for supported self/global or untargeted team spells. Single-enemy and pre-May targeted area cards require an actual live enemy slot.
* Target validation uses authoritative template effects, including nested effect lists, and the existing school mastery/pip cost check. Wrong-side, empty, defeated, out-of-range and required-target sentinel inputs are rejected without substituting another enemy. Unsupported target semantics fail closed as `InvalidCard`.
* The strict switch is independently checked by the duel via `Classic.EnhancedGameplaySettings.Enabled`, currently backed by `Classic.OwnedMinionControl`. Service negotiation alone cannot bypass it.

## Summon ownership and lifecycle

Eligibility uses the owner's actual `Wizard.MagicSchoolBehavior.MagicSchool == Myth`, plus human participant identity, live state, duel membership, planning and the existing reference-based `IsOwnedMinionOf` check. All six other wizard schools remain AI-only, including for owned Monstrology summons. Minion school does not restrict Myth ownership control.

Normal deferred `kSummonCreature` spawning calls `SpawnAndAssignMinion(..., controllableSummon: true)` and registers the resulting circle. Existing Monstrology casts using this same effect path receive the same treatment. Hire-henchman spawning retains the default `false` and remains AI-only. An existing alternate summon path can explicitly call:

```csharp
RegisterOwnedMinionForControl(CombatDuelSubCircle minion, CombatDuelSubCircle owner)
```

This hook does not grant another school control: the duel validates the owner wizard on every request. No Monstrology ledger, handlers, spell catalog or summon quantities were added by this worker.

AI retains its normal queued move unless the owner submits a valid replacement. The first replaced AI action is saved; later AI messages cannot overwrite an accepted manual order. ChangeMind/disable restores that saved fallback. No order by the deadline leaves AI queued, or the existing resolver's normal pass if no AI action exists. Planning cannot finish early while an opted-in live owner has a live controllable minion without a submitted order.

Orders/fallbacks use participant object identity rather than reusable circles/slots. Stale round/IDs, dead or removed minions and replacement occupants cannot inherit an order. Before execution, invalidated manual orders revert to fallback. Completed orders are cleared before resolution snapshots. Removal purges control records and publishes the refreshed owner list. Owner flee disables its control before participant removal; duel teardown clears all control state.

Periodic state responses use `MinionID=0, RequestID=0`, the current duel/round, and a full owner list. They are notifications, not acknowledgements of client request zero. The client should replace its list, ignore older rounds, and use normal duel phase/remove/end messages to disable controls outside planning. No minion/client UI implementation or visible acceptance is claimed here.

## Verification

Focused own feature: 20 passing tests, no skips. Combined relevant combat/minion/deck/enchantment/utility regressions: **117 passing, 0 failures, 0 skips**. Evidence: `/private/tmp/w101c-owned-minion-regressions.log`.

Coverage includes pure access/target/state validation; all six non-Myth schools; owned other-school summon versus allied/henchman rejection; serialized current-hand round-trip and real JSON projection size; no drawing/refilling/spending on query; card/pip/target rejection; replay/stale identity; AI overwrite prevention; ChangeMind/deadline restoration; disable and strict switch; multiple minions; completion-grace opt-in; timeout fallback; target/owner/minion death and removal/replacement. An accepted manual attack is executed through the normal resolver under stun, preserving action order, normal stun consumption, pips and the uncast card.

Director executable build also passed, 0 errors and one existing NU1510 warning. Its missing project.assets.json was generated from the already installed exact-version package cache using an empty offline source; no network, package-version changes or source dependency edits were needed. Evidence: `/private/tmp/w101c-owned-minion-director-build.log`. `git diff --check` passed.

The test run compiles CoreLib successfully with the private generated inputs already present. Existing unrelated package/compiler/analyzer warnings remain. No generated inputs, dependencies, classic-data or other workers' feature files were changed by this worker.

## Exact worker files

* `src/Imlight.CoreLib/Game/Combat/OwnedMinionControl.cs` (new validation/state helper)
* `src/Imlight.CoreLib/Game/Zone/Components/CombatDuelComponent.OwnedMinions.cs` (new partial)
* `src/Imlight.CoreLib/Game/Zone/Components/CombatDuelComponent.cs` (partial declaration and scoped planning/summon/lifecycle hooks; parent-owned enemy-cap edit preserved)
* `src/Imlight.CoreLib/Shared/Packets/COMBAT_106_PROTOCOL.cs` (internal messages/status/snapshot contract)
* `tests/Imlight.Classic.Tests/OwnedMinionControlTests.cs` (new pure validation tests)
* `tests/Imlight.Classic.Tests/OwnedMinionDuelTests.cs` (new isolated duel integration tests)
* `tests/Imlight.Classic.Tests/Imlight.Classic.Tests.csproj` (two integration-test entries, preserving other additions)
* `docs/audits/owned-minion-control.md` (this handoff)

Limitations: tests run isolated actors/components with authoritative test state and generated serialization; they do not certify an installed client UI or a live end-to-end summon/order/cast session. The parent must complete its negotiated client acceptance and deployment gates. Unrecognized modern target enum semantics remain deliberately unsupported rather than guessed or retargeted.

## Integrated release checkpoint

The combined implementation passes 893 server tests with real classic-data and door fixtures, and 509 public CI tests, with zero failures or skips in both runs. Excluded roaming enemies use a rejection-specific internal message (order 31), checked against object identity and current duel admission, instead of a combat-death message. Seven actor-path cases cover cap/full circles, competing admission, duplicate movement and replacement objects.

Door source currently covers 46 verified bindings in 19 zones; broader visible-lamp coverage and installed graphical acceptance remain open. Monstrology stays globally disabled while exact reward and training contracts are unresolved. The stock-client minion hand UI is not installed. These are source/test checkpoints, not a completed client or live deployment claim.
