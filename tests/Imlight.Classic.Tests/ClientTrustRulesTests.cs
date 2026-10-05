using System.Collections.Generic;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public class ClientTrustRulesTests {

    // ---- Training ----

    private static TrainRefusal Train(bool known = false, int level = 10, int requiredLevel = 5, ulong requiredSpell = 0,
                                      HashSet<ulong>? spells = null, int points = 1, int cost = 1)
        => TrainRules.Check(known, level, requiredLevel, requiredSpell, id => spells?.Contains(id) == true, points, cost);

    [Fact]
    public void Train_cost_is_free_in_the_wizards_own_school_and_one_point_otherwise() {
        Assert.Equal(0, TrainRules.Cost("Fire", "Fire"));
        Assert.Equal(1, TrainRules.Cost("Fire", "Ice"));
        Assert.Equal(1, TrainRules.Cost("Fire", null));
    }

    [Fact]
    public void Train_allows_an_entry_the_trainer_window_shows_as_trainable() {
        Assert.Equal(TrainRefusal.None, Train());
        Assert.Equal(TrainRefusal.None, Train(level: 5, requiredLevel: 5, points: 0, cost: 0));
        Assert.Equal(TrainRefusal.None, Train(requiredSpell: 42, spells: [42]));
    }

    [Fact]
    public void Train_refuses_a_level_one_wizard_a_high_level_spell() {
        Assert.Equal(TrainRefusal.LevelTooLow, Train(level: 1, requiredLevel: 22, cost: 0));
    }

    [Fact]
    public void Train_refuses_without_the_required_spell() {
        Assert.Equal(TrainRefusal.MissingRequiredSpell, Train(requiredSpell: 42, spells: [7]));
    }

    [Fact]
    public void Train_refuses_without_training_points_and_a_known_spell() {
        Assert.Equal(TrainRefusal.NotEnoughTrainingPoints, Train(points: 0, cost: 1));
        Assert.Equal(TrainRefusal.AlreadyKnown, Train(known: true));
    }

    // ---- Service range ----

    [Fact]
    public void Service_range_accepts_a_wizard_at_the_counter_and_refuses_one_across_the_zone() {
        Assert.True(ServiceRange.IsWithin(0, 0, 0, 300, 400, 0));
        Assert.True(ServiceRange.IsWithin(0, 0, 0, ServiceRange.Radius, 0, 0));
        Assert.False(ServiceRange.IsWithin(0, 0, 0, ServiceRange.Radius + 1, 0, 0));
        Assert.False(ServiceRange.IsWithin(0, 0, 0, 8000, -6000, 100));
        Assert.False(ServiceRange.IsWithin(float.NaN, 0, 0, 0, 0, 0));
    }

    // ---- Dyes ----

    [Fact]
    public void Buy_dye_is_kept_only_on_a_dyeable_item_and_in_range() {
        Assert.True(DyeRules.IsDyeable(numPrimaryColors: 0, numSecondaryColors: 1));
        Assert.False(DyeRules.IsDyeable(numPrimaryColors: 1, numSecondaryColors: 1));
        Assert.False(DyeRules.IsDyeable(numPrimaryColors: 3, numSecondaryColors: 0));

        Assert.Equal(12, DyeRules.BuyLayer(12, 3, dyeable: true, isPet: false, petColorCount: 0));
        Assert.Equal(3, DyeRules.BuyLayer(12, 3, dyeable: false, isPet: false, petColorCount: 0));
        Assert.Equal(3, DyeRules.BuyLayer(32, 3, dyeable: true, isPet: false, petColorCount: 0));
        Assert.Equal(3, DyeRules.BuyLayer(-1, 3, dyeable: true, isPet: false, petColorCount: 0));
        Assert.Equal(3, DyeRules.BuyLayer(int.MaxValue, 3, dyeable: true, isPet: false, petColorCount: 0));
    }

    [Fact]
    public void Buy_dye_on_a_pet_is_one_of_its_template_colors() {
        Assert.Equal(2, DyeRules.BuyLayer(2, 0, dyeable: true, isPet: true, petColorCount: 4));
        Assert.Equal(0, DyeRules.BuyLayer(4, 0, dyeable: true, isPet: true, petColorCount: 4));
        Assert.Equal(0, DyeRules.BuyLayer(1, 0, dyeable: true, isPet: true, petColorCount: 1));
    }

    // ---- Treasure cards ----

    [Fact]
    public void Treasure_purchase_is_one_to_ninety_nine_copies_at_a_real_price_with_room_in_the_book() {
        Assert.True(TreasureShopRules.CanBuy(1, 150, 0));
        Assert.True(TreasureShopRules.CanBuy(TreasureShopRules.MaxQuantity, 150, 0));
        Assert.False(TreasureShopRules.CanBuy(0, 150, 0));
        Assert.False(TreasureShopRules.CanBuy(-5, 150, 0));
        Assert.False(TreasureShopRules.CanBuy(100, 150, 0));
        Assert.False(TreasureShopRules.CanBuy(int.MaxValue, 0, 0));
        Assert.False(TreasureShopRules.CanBuy(1, 0, 0));
        Assert.False(TreasureShopRules.CanBuy(1, -10, 0));
        Assert.True(TreasureShopRules.CanBuy(9, 150, TreasureShopRules.BookCapacity - 9));
        Assert.False(TreasureShopRules.CanBuy(10, 150, TreasureShopRules.BookCapacity - 9));
    }

    // ---- Character creation ----

    [Fact]
    public void Creation_takes_only_the_seven_player_schools() {
        foreach (var school in new uint[] { 72777, 2330892, 2343174, 2448141, 78318724, 83375795, 1027491821 }) {
            Assert.True(CharacterCreationRules.IsPlayerSchool(school));
        }

        Assert.False(CharacterCreationRules.IsPlayerSchool(0));           // None
        Assert.False(CharacterCreationRules.IsPlayerSchool(78483));       // Sun
        Assert.False(CharacterCreationRules.IsPlayerSchool(1429009101));  // Shadow
        Assert.False(CharacterCreationRules.IsPlayerSchool(5));
    }

    [Fact]
    public void Creation_name_keys_must_point_into_the_tables() {
        static uint Keys(int first, int middle, int last) => (uint) ((first << 16) | (middle << 8) | last);

        Assert.True(CharacterCreationRules.IsValidName(Keys(0, 0, 0), 10, 5, 5));
        Assert.True(CharacterCreationRules.IsValidName(Keys(9, 4, 4), 10, 5, 5));
        Assert.False(CharacterCreationRules.IsValidName(Keys(10, 0, 0), 10, 5, 5));
        Assert.False(CharacterCreationRules.IsValidName(Keys(1, 5, 1), 10, 5, 5));
        Assert.False(CharacterCreationRules.IsValidName(Keys(1, 1, 255), 10, 5, 5));
        Assert.False(CharacterCreationRules.IsValidName(Keys(0, 0, 0), 0, 0, 0));
    }

    // ---- .account infractions ----

    [Fact]
    public void Infractions_are_readable_by_their_owner_or_a_moderator_only() {
        Assert.True(InfractionAccess.CanView("wizard2", callerIsModerator: false, "Wizard2"));
        Assert.False(InfractionAccess.CanView("wizard2", callerIsModerator: false, "nick"));
        Assert.False(InfractionAccess.CanView(null, callerIsModerator: false, ""));
        Assert.True(InfractionAccess.CanView("mod", callerIsModerator: true, "nick"));
    }

}
