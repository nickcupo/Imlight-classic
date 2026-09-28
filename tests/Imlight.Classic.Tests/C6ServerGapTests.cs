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
 *
 * ========================================================================
 * C6 SERVER GAP TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The server gaps the native client's interface work found: the starter kit
 * (once, the school's own wand), trainers that teach only spells of the
 * profile, zone objects that keep sending markers to a wizard who comes back,
 * library prices, and True Friend codes.
 *
 * USAGE EXAMPLE:
 * W101C_REQUIRE_CLASSIC_DATA=1 dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class C6ServerGapTests {

    // ---- 1. Starter kit ----------------------------------------------------------------------------------------

    [Fact]
    public void EverySchoolGetsItsOwnStarterWandAndTheStarterDeck() {
        MagicSchool[] schools = [MagicSchool.Balance, MagicSchool.Death, MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Life,
                                 MagicSchool.Myth, MagicSchool.Storm];
        var kits = schools.Select(ClassicStart.StarterItemTemplateIds).ToList();

        Assert.All(kits, kit => Assert.Equal(ClassicStart.StarterDeckTemplateId, kit[1]));
        Assert.Equal(7, kits.Select(kit => kit[0]).Distinct().Count());
        Assert.Equal(87256ul, ClassicStart.StarterItemTemplateIds(MagicSchool.Myth)[0]); // Wand-T1-016, Antiquated Wand
        Assert.Equal(87247ul, ClassicStart.StarterItemTemplateIds(MagicSchool.Fire)[0]); // Wand-T1-007, Branded Wand
        Assert.Equal(87241ul, ClassicStart.StarterItemTemplateIds(MagicSchool.Balance)[0]); // Wand-T1-001, Symmetrical Wand
    }

    [Fact]
    public void TheStarterKitIsGivenOnceEvenWhenTheClassicStartEquipsAllOfIt() {
        var fresh = KitWizard();
        Assert.True(TutorialService.NeedsStarterKit(fresh, classicStart: true));
        Assert.True(TutorialService.NeedsStarterKit(fresh, classicStart: false));

        // The classic start equips the wand and deck: the backpack is empty again, but the kit was given.
        var equipped = KitWizard();
        equipped.EquipmentBehavior.EquippedItems.Add(new WizClientObjectItem());
        Assert.False(TutorialService.NeedsStarterKit(equipped, classicStart: true));
        Assert.False(TutorialService.NeedsStarterKit(equipped, classicStart: false));

        var marked = KitWizard();
        marked.QuestBehavior.SetRegistryValue(ClassicStart.StarterKitGivenEntry, 1);
        Assert.False(TutorialService.NeedsStarterKit(marked, classicStart: true));
        Assert.True(TutorialService.NeedsStarterKit(marked, classicStart: false));

        var done = KitWizard();
        done.QuestBehavior.SetRegistryValue(ClassicStart.CompletedEntry, 1);
        Assert.False(TutorialService.NeedsStarterKit(done, classicStart: true));

        Assert.False(TutorialService.NeedsStarterKit(null!, classicStart: true));
    }

    private static Wizard KitWizard() => new() {
        CharId = 91,
        QuestBehavior = new ServerQuestBehavior(),
        InventoryBehavior = new ServerWizInventoryBehavior { Items = [], InventoryItemIds = [] },
        EquipmentBehavior = new ServerWizEquipmentBehavior { SlotList = [], EquippedItemIds = [], EquippedItems = [] },
    };

    // ---- 2. Trainers -------------------------------------------------------------------------------------------

    private static ClassicSpellOverrides Overrides(string profileId)
        => new(ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")), ClassicDataFixture.LoadProfile(profileId));

    [Fact]
    public void TrainersTeachOnlyTrainedAndCrossoverSpellsOfTheProfile() {
        var late = Overrides("late-2009");

        Assert.True(late.IsTrainable("Spells/Tiered Spells/Sandstorm.xml"));
        Assert.True(late.IsTrainable("Spells/Tiered Spells/Fire Cat.xml"));
        Assert.True(late.IsTrainable("Spells/Power Play.xml"));

        // No record: the r806919 client's later trainer spells (Gearhead Destroyer, Elemental Golem, Tri shields).
        Assert.False(late.IsTrainable("Spells/GearheadDestroyer_Trainable.xml"));
        Assert.False(late.IsTrainable("Spells/SteelGolemBalance_Trainable.xml"));
        Assert.False(late.IsTrainable(null));

        // A record that is not taught: a treasure card or an item card.
        var treasure = late.Book.Records.First(record => record is { Kind: "treasure_card", ClientTemplate: not null });
        var item = late.Book.Records.First(record => record is { Kind: "item", ClientTemplate: not null });
        Assert.False(late.IsTrainable(treasure.ClientTemplate));
        Assert.False(late.IsTrainable(item.ClientTemplate));

        // Every trained or crossover record of the profile with a client template is trainable.
        Assert.All(late.Book.Records.Where(record => record is { Kind: "trained" or "crossover", ClientTemplate: not null }
                                                     && record.IsInProfile("late-2009")),
            record => Assert.True(late.IsTrainable(record.ClientTemplate)));
    }

    [Fact]
    public void ACardFirstDatedAfterTheArc1CutoffIsNotTaughtInArc1() {
        var arc1 = Overrides("arc1-2009h1");
        var later = arc1.Book.Records.FirstOrDefault(record => record is { Kind: "trained" or "crossover", ClientTemplate: not null }
                                                               && record.IsInProfile("late-2009") && !record.IsInProfile("arc1-2009h1"));
        if (later is null) {
            return; // Every trained card is dated before 2009-06-30.
        }

        Assert.False(arc1.IsTrainable(later.ClientTemplate));
        Assert.True(Overrides("late-2009").IsTrainable(later.ClientTemplate));
    }

    [Fact]
    public void ASpellWhoseRequirementIsWithheldRequiresTheNextSpellUpTheChain() {
        // Arthur Wethersfield in r806919: ... Sandstorm (5) <- Gearhead Destroyer (7) <- Power Play (8); Tri Shield (9) is
        // taught elsewhere and withheld too; Steel Golem (6) requires it.
        List<NPCSpellEntry> inventory = [
            new() { TemplateID = 1, RequiredSpellID = 0, Level = 1 },
            new() { TemplateID = 5, RequiredSpellID = 1, Level = 16 },
            new() { TemplateID = 6, RequiredSpellID = 9, Level = 28 },
            new() { TemplateID = 7, RequiredSpellID = 5, Level = 28 },
            new() { TemplateID = 8, RequiredSpellID = 7, Level = 22 },
            new() { TemplateID = 10, RequiredSpellID = 11, Level = 5 },
            new() { TemplateID = 12, RequiredSpellID = 0, Level = 5 },
        ];
        HashSet<ulong> withheld = [6, 7, 9];
        HashSet<ulong> broken = [11, 12];

        var offered = InteractTrainerComponent.OfferedSpells(inventory, id => !broken.Contains(id), id => !withheld.Contains(id));

        Assert.Equal([1ul, 5, 8, 10], offered.Select(spell => spell.TemplateID));
        Assert.Equal(5ul, offered.Single(spell => spell.TemplateID == 8).RequiredSpellID);
        Assert.Equal(0ul, offered.Single(spell => spell.TemplateID == 10).RequiredSpellID); // its requirement does not load
        Assert.Same(inventory[1], offered[1]);
        Assert.Equal(7ul, inventory[4].RequiredSpellID); // the SpiralDB entry is not changed
        Assert.Equal(22, offered[2].Level);
    }

    [Fact]
    public void ARequirementCycleOfWithheldSpellsEnds() {
        List<NPCSpellEntry> inventory = [
            new() { TemplateID = 1, RequiredSpellID = 2 },
            new() { TemplateID = 2, RequiredSpellID = 3 },
            new() { TemplateID = 3, RequiredSpellID = 2 },
        ];

        var offered = InteractTrainerComponent.OfferedSpells(inventory, _ => true, id => id == 1);

        Assert.Equal(0ul, Assert.Single(offered).RequiredSpellID);
    }

    // ---- 3. Quest markers --------------------------------------------------------------------------------------

    private sealed class Silent : ReceiveActor {
        public Silent() => ReceiveAny(_ => { });
    }

    [Fact]
    public void AWizardBackWithANewSessionIsTakenBackInRange() {
        using var system = ActorSystem.Create("players-in-range-test");
        var firstSession = system.ActorOf(Props.Create(() => new Silent()), "first");
        var secondSession = system.ActorOf(Props.Create(() => new Silent()), "second");
        var range = new PlayersInRange();

        Assert.Equal(RangeChange.Entered, range.Update(7, firstSession, inRange: true));
        Assert.Equal(RangeChange.Unchanged, range.Update(7, firstSession, inRange: true));

        // The removal of the first session was lost: the wizard logs in again and walks up with a new session.
        Assert.Equal(RangeChange.Entered, range.Update(7, secondSession, inRange: true));
        Assert.Equal(secondSession, Assert.Single(range.Actors));
        Assert.False(range.Contains(firstSession));

        Assert.False(range.Remove(firstSession));
        Assert.True(range.Remove(secondSession));
        Assert.Equal(0, range.Count);

        Assert.Equal(RangeChange.Entered, range.Update(7, secondSession, inRange: true));
        Assert.Equal(RangeChange.Left, range.Update(7, secondSession, inRange: false));
        Assert.Equal(RangeChange.Unchanged, range.Update(7, secondSession, inRange: false));
    }

    // ---- 4. Library prices -------------------------------------------------------------------------------------

    private static TreasurePrices RealPrices() => TreasurePricesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "treasure-prices-2009.yaml"));

    [Fact]
    public void TheLibraryChargesThe2009PriceElseWhatTheClientShows() {
        var prices = RealPrices();

        Assert.Equal("treasure-prices-2009", prices.Id);
        Assert.Equal(59, prices.ByName.Count);
        Assert.Equal(150, prices.Find("Fire Shield TC"));
        Assert.Equal(150, prices.Find("fire shield"));
        Assert.Equal(350, prices.Find("Glacial Shield TC"));
        Assert.Equal(1600, prices.Find("Fairy TC"));
        Assert.Null(prices.Find("Blizzard TC"));
        Assert.Null(prices.Find(null));

        // Fire Shield TC: base cost 75 in r806919, times the client's library markup of 2.
        Assert.Equal(150, TreasurePrices.PriceOf(prices, "Fire Shield TC", 75, 2));
        // Glacial Shield: 2009 350, although r806919's base cost (350) shows 700.
        Assert.Equal(350, TreasurePrices.PriceOf(prices, "Glacial Shield TC", 350, 2));
        // Not listed (Blizzard, base cost 250): what the client shows.
        Assert.Equal(500, TreasurePrices.PriceOf(prices, "Blizzard TC", 250, 2));
        Assert.Equal(250, TreasurePrices.PriceOf(null, "Blizzard TC", 250, 0));
        Assert.Equal(0, TreasurePrices.ClientPrice(-5, 2));
    }

    [Fact]
    public void TheCanonicalProfileNamesTheLibraryPrices() {
        Assert.Equal("rules/treasure-prices-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.TreasurePrices);
        Assert.Equal("rules/treasure-prices-2009.yaml", ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.TreasurePrices);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.TreasurePrices);
    }

    [Fact]
    public void ATreasurePriceTableWithACardTwiceIsRejected() {
        var dir = Directory.CreateTempSubdirectory("w101c-prices-");
        try {
            var path = Path.Combine(dir.FullName, "treasure-prices-test.yaml");
            File.WriteAllText(path, """
                id: treasure-prices-test
                profiles: [late-2009]
                license_tag: own
                provenance:
                - {source: test, source_date: '2009-07-20', retrieved: '2026-09-27', covers: [prices], confidence: verified}
                cards:
                - {name: Fire Shield, price: 150}
                - {name: fire shield, price: 75}
                - {name: Fire Cat, price: 0}
                """);

            var error = Assert.Throws<ClassicDataException>(() => TreasurePricesLoader.Load(path));
            Assert.Contains("listed twice", error.Message);
            Assert.Contains("price", error.Message);
        }
        finally {
            dir.Delete(recursive: true);
        }
    }

    // ---- 5. True Friend codes ----------------------------------------------------------------------------------

    private sealed class MemoryCodes : ITrueFriendCodeStore {
        public readonly Dictionary<string, TrueFriendCode> Codes = [];

        public TrueFriendCode? Find(string code) => Codes.GetValueOrDefault(code);

        public int CountOpen(ulong creatorCharId, DateTimeOffset now)
            => Codes.Values.Count(code => code.CreatorCharId == creatorCharId && !code.IsExpired(now));

        public bool Add(TrueFriendCode code) => Codes.TryAdd(code.Code, code);

        public void Remove(string code) => Codes.Remove(code);
    }

    private static readonly DateTimeOffset Now = new(2009, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATrueFriendCodeIsTenUnambiguousCharacters() {
        var store = new MemoryCodes();
        var (code, error) = TrueFriendCodes.Create(store, 1, "Creator", Now);

        Assert.Equal(TrueFriendCodeError.None, error);
        Assert.NotNull(code);
        Assert.Equal(TrueFriendCodes.CodeLength, code.Code.Length);
        Assert.DoesNotContain(code.Code, c => "01ILOS5".Contains(c) || char.IsLower(c));
        Assert.Same(code, store.Find(code.Code));
    }

    [Fact]
    public void AWizardMayHoldOnlyAFewOpenCodes() {
        var store = new MemoryCodes();
        for (var i = 0; i < TrueFriendCodes.MaxOpenCodesPerWizard; i++) {
            Assert.Equal(TrueFriendCodeError.None, TrueFriendCodes.Create(store, 1, "Creator", Now).Error);
        }

        Assert.Equal(TrueFriendCodeError.TooManyCodes, TrueFriendCodes.Create(store, 1, "Creator", Now).Error);
        Assert.Equal(TrueFriendCodeError.None, TrueFriendCodes.Create(store, 2, "Other", Now).Error);

        // Two days later the old codes have expired.
        Assert.Equal(TrueFriendCodeError.None, TrueFriendCodes.Create(store, 1, "Creator", Now + TrueFriendCodes.Lifetime).Error);
    }

    [Fact]
    public void ACodeThatKeepsCollidingCannotBeGenerated() {
        var store = new MemoryCodes();
        Assert.Equal(TrueFriendCodeError.None, TrueFriendCodes.Create(store, 1, "A", Now, () => "SAMECODE22").Error);
        Assert.Equal(TrueFriendCodeError.UnableToGenerateCode, TrueFriendCodes.Create(store, 2, "B", Now, () => "SAMECODE22").Error);
    }

    [Fact]
    public void AFriendUsesACodeOnceWithin48Hours() {
        var store = new MemoryCodes();
        var code = TrueFriendCodes.Create(store, 1, "Creator", Now).Code!;
        Func<ulong, bool> exists = _ => true;
        Func<ulong, bool> friends = id => id == 1;

        Assert.Equal(TrueFriendCodeError.NoSuchCode, TrueFriendCodes.Use(store, 2, "NOPE", Now, exists, friends).Error);
        Assert.Equal(TrueFriendCodeError.NoSuchCode, TrueFriendCodes.Use(store, 2, "", Now, exists, friends).Error);

        // The creator's own entry and a stranger's leave the code for the friend it was made for.
        Assert.Equal(TrueFriendCodeError.NoSuchCode, TrueFriendCodes.Use(store, 1, code.Code, Now, exists, _ => true).Error);
        Assert.Equal(TrueFriendCodeError.NoSuchCode, TrueFriendCodes.Use(store, 3, code.Code, Now, exists, _ => false).Error);
        Assert.NotNull(store.Find(code.Code));

        var typed = $" {code.Code[..5].ToLowerInvariant()}-{code.Code[5..]} ";
        var used = TrueFriendCodes.Use(store, 2, typed, Now.AddHours(47), exists, friends);
        Assert.Equal(TrueFriendCodeError.None, used.Error);
        Assert.Equal(1ul, used.Code!.CreatorCharId);

        Assert.Equal(TrueFriendCodeError.NoSuchCode, TrueFriendCodes.Use(store, 2, code.Code, Now.AddHours(47), exists, friends).Error);
    }

    [Fact]
    public void AnExpiredCodeOrOneFromADeletedWizardIsRefusedAndForgotten() {
        var store = new MemoryCodes();
        var old = TrueFriendCodes.Create(store, 1, "Creator", Now).Code!;
        var orphan = TrueFriendCodes.Create(store, 4, "Deleted", Now).Code!;

        Assert.Equal(TrueFriendCodeError.NoSuchCode,
            TrueFriendCodes.Use(store, 2, old.Code, Now + TrueFriendCodes.Lifetime, _ => true, _ => true).Error);
        Assert.Null(store.Find(old.Code));

        Assert.Equal(TrueFriendCodeError.CreatorDoesNotExist,
            TrueFriendCodes.Use(store, 2, orphan.Code, Now, id => id != 4, _ => true).Error);
        Assert.Null(store.Find(orphan.Code));
    }

    [Fact]
    public void ErrorsAreTheClientsHashedErrorNames() {
        // WizardGUIManager::HandleChatCodeError compares MSG_SENDCHATCODE.Error with the KingsIsle hash of "ERROR_<name>".
        Assert.Equal(0u, TrueFriendCodes.ErrorCode(TrueFriendCodeError.None));
        Assert.Equal(399251830u, TrueFriendCodes.ErrorCode(TrueFriendCodeError.NoSuchCode));
        Assert.Equal(Imcodec.Cryptography.StringHash.Compute("ERROR_TooManyCodes"), TrueFriendCodes.ErrorCode(TrueFriendCodeError.TooManyCodes));
        Assert.Equal(5, Enum.GetValues<TrueFriendCodeError>().Skip(1).Select(TrueFriendCodes.ErrorCode).Distinct().Count());
    }

}
