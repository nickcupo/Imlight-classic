# Crafting inventory and unresolved contracts

Status: bounded local inventory complete; transactions are **not implemented or enabled**.
No service returning no-op success has been added. This document does not establish
historical economics from modern assets. No deployment, network access, or changes
to combat, project files, profiles, or the shared database were made.

## Verified local production inputs

The private r806919 `Recipes-WorldData.wad` contains
`ObjectData/Equipment_Recipes/Recipe-Athame-Craft-T1-002.xml`.
The production `ArchiveParser` and `BindSerializer` decode it as `RecipeTemplate`:

| Field | Decoded r806919 value |
| --- | --- |
| Name | Recipe-Athame-Craft-T1-002 |
| Display key | Recipes_00000199 (Dagger of Absolution in staged English locale) |
| Output item | 180048, Athame-Craft-T1-002, Items_00002402 |
| Ingredients | template 106947 x2; 106953 x4; 106931 x2; 106939 x2 |
| Ingredient type | 0 for each; no adjective or spell alternative |
| Purchase gold | 64 |
| Cook time | 60 |
| Categories | Recipe-Athame-Craft-T1-002.AdjRef, CraftGeneric |
| Purchase/display requirements | null |
| Craft results, loot table, spell output | null/empty |
| Other currency prices | zero |

These prices and times are **r806919 evidence**, not verified 2009 values.
Neighboring T1 recipes have different cooldowns (4500, 4500, 3000) and purchase
requirements. Enabling every T1 recipe or assigning them a common cooldown would
therefore be unsafe even before historical filtering.

`CraftingProtocolEvidenceTests.cs` pins all four real archive decodes. It does not
copy a transaction into a test or claim purchase/craft/persistence acceptance.
Set `W101C_CRAFTING_GAME_DATA` to the directory containing the local WAD. The default
is `/Users/nick/w101c-private/aurorium/data/V_r806919.Wizard_1_610/Data/GameData`.
With `W101C_REQUIRE_CLASSIC_DATA=1`, missing assets fail rather than silently skip.
Include this file in the main project's conditional private integration test list.

## Historical evidence found locally

`classic-data/badges/badges-2009.yaml` cites The Razor's Edge, oldid 57003,
2010-01-13, for Eudora and the Novice Crafter reward.
`classic-data/spiraldb-overlay/QuestTemplates/WC-CLASSIC-SIDE-063.provenance.yaml`
identifies the prerequisite as crafting two Daggers of Absolution and explicitly
notes that the engine lacks a crafting goal type.

The staged historical screenshot
`reference/ui-2009/vendors/fn-2009-11-16-Happy-Crafter-2.jpg` shows a Feint recipe
shop: price 1000 and ingredient counts, but no readable numeric cooldown. It does
not establish Dagger economics. The October crafting screenshots inspected show
reagent pickup and rank dialogue. No complete, dated Dagger price/material/time
record was found in the bounded local references searched.

A Dagger allowlist entry can establish historical *identity* from the quest
research; it cannot honestly label the modern 60-second time historical without
additional evidence or an explicit decision to accept that staged value.

## Server inventory

- `ClassicFeatures.Crafting` already exists. `late-2009` enables it;
  `arc1-2009h1` disables it. A handler must enforce this at transaction time.
- `GameServiceFactory.ServiceTypes` registers session services. This is the
  registration point, rather than a hardcoded service list in `SessionActor`.
- `SessionActor.SetServices` permits multiple handlers for the same message and
  forwards to all of them. Registering another shop-buy handler without routing
  would invoke both crafting and equipment shops.
- `ShopService` owns `MSG_SHOPBUYREQUEST`, assumes equipment, interprets `ShopID`
  through GID template bits, casts the template to `WizItemTemplate`, and checks
  `InteractVendorComponent`. Recipe buying cannot safely reuse that cast.
- `WizShopOffering` has **both** `m_shopList` (GIDs) and `m_recipeList` (strings).
  `RecipeShopOption`, `RecipeTemplate`, `RecipeTypeList`, `ClientRecipe`, and
  `CraftingSlot` are generated and usable. No CraftingService exists.
- `ServerAlchemyBehavior.Recipes` and `.CraftingSlots` are `[JsonIgnore]` lists;
  new Wizards initialize them empty. A runtime-only grant would disappear on
  reload. The client bags already serialize them when present.
- Reagents are separate `WizardReagents` documents; output items are separate
  `WizardItems` documents. Wizard stores their inventory IDs. Existing Wizard
  mutation methods save separately, so sequential RemoveReagent/AddItem calls
  are not an atomic craft transaction.
- `WizardCollection` has private per-character write lanes. Inventory has its
  own mutation lock. A new crafting-only lock would not serialize existing gold,
  reagent, and inventory writers. Persistence needs a shared transaction boundary,
  not a compensating series of independent database writes.

## Vendor/station decoder gap

Production `BindSerializer` on the staged Root files yields:

- `ObjectData/WC/WC-Shop-Recipe-NPC01.xml`, template **136764**, display
  `WizardNPC_00000218`: final three behavior entries decode to null. The staged
  template index identifies them as `BasicNPCServiceBehavior`, `ShoppingBehavior`,
  and `AlchemyStationBehavior`.
- `ObjectData/Housing/WC/Furniture/HOUSE_crafting_generic_01.xml`, template
  **164647**: its behavior list also contains unknown/null entries.

The inspected generated client types include AlchemyBehavior and the service
options, but not the vendor ShoppingBehaviorTemplate / station
AlchemyStationBehaviorTemplate payloads. The supplemental server registry does
not supply those decoders. Their recipe lists, conditions, station restrictions,
and any additional fields must be decoded and validated before using them as
transaction authority. A successful outer-template decode is not evidence that
these behaviors survived.

`ResAddRecipe` and `ResAddCraftingSlot` also exist as empty supplemental generated
result classes with commented field names; they do not provide functioning quest
recipe/slot rewards. That is separate from buying a recipe.

## Exact UI handoff contract and missing data

All message fields below are from the generated r806919 classes, not a proposed
custom wire protocol:

| Direction/purpose | Message and fields |
| --- | --- |
| Vendor offering | WIZARD service 12/order 196 MSG_SHOPLIST: GlobalID, Data, Credits, WebFailure; Data is a serialized WizShopOffering with m_recipeList |
| Purchase request | 12/195 MSG_SHOPBUYREQUEST: ShopID u64, CurrencyType u8, texture/decal/decal2 i32, petName u32, npcGlobalID u64, quantity u32 |
| Purchase result | 12/194 MSG_SHOPBUYCONFIRM: existing generated failure/credit response |
| Recipe grant | 12/152 MSG_RECIPEADD: GlobalID u64, Data bytes containing ClientRecipe/Recipe; recipe carries m_recipeNameID u32 |
| Station offer | 12/16 MSG_ALCHEMYSTATION: AllowedRecipes bytes, TitleOverride, CreateButtonOverride, SegmentedMessage, LastSegment; generated RecipeTypeList carries names, allowed types, failed requirements and flags |
| Craft request/result | 12/245 MSG_USERECIPE: RecipeName bytes, FinalItemID u64, Error u32, Quantity u32 |
| Cooldown slot | 12/37 MSG_CRAFTINGSLOTADD: GlobalID, Data; CraftingSlot has m_recipeName string and m_timeFinished i32 |
| Slot expiration | 12/39 MSG_CRAFTINGSLOTREMOVE: GlobalID, ItemID |

Still needed to wire a supported vertical slice without guessing:

1. One verified recipe purchase request, or the native client's encoder: how a
   string in `m_recipeList` becomes `MSG_SHOPBUYREQUEST.ShopID`, including any GID
   tag/bit layout. Also confirm `m_recipeNameID` hashing. The equipment ShopID
   interpretation alone does not establish the recipe encoding.
2. Verified `MSG_USERECIPE.Error` meanings and Quantity semantics; the generated
   schema describes fields, not the numeric failure enumeration. Validate whether
   `FinalItemID` is output-only and the time unit/epoch for `m_timeFinished`.
3. Decoded vendor/station payloads establishing the offered recipe and authorized
   station, plus the baseline crafting-slot count and rank/quest slot unlocks.
   Recipe use contains no station ID, so authority must come from server-held
   interaction context, with zone/transfer/disconnect invalidation.
4. A dated Dagger recipe record with price, materials, and cooldown, or an explicit
   product decision accepting the staged r806919 values for that identified classic
   recipe. Do not open the modern recipe archive by default.

The UI agent can use the table to prepare models and packet handling. It should not
invent ShopID mapping, error enums, or claim the server transaction is available.

## Required implementation and acceptance after those contracts

Use a name allowlist, evaluate the feature/requirements again at purchase and craft,
reject client-supplied output identity and unsupported quantities, check ownership,
station context, materials, inventory capacity, and available cooldown slots. Save
recipe ownership or the material deductions + output + cooldown + inventory IDs in
one database unit of work, then publish success and client deltas. Refresh runtime
bags on load from persisted state. A commit failure must publish no success and
leave runtime state consistent with persisted state.

Production regressions must drive real service dispatch and actual persistence:
duplicate purchase, insufficient gold/materials, forged NPC/station/recipe/output,
modern recipe and disabled-profile refusal, full inventory, cooldown across reload,
repeat craft at expiry, concurrent transactions, and database failure rollback.
The current four asset tests are groundwork only and do not satisfy that acceptance.
