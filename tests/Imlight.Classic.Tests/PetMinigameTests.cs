// CLASSIC: the server halves of Cannon, Gobbler Drop and Maze (Imlight.Classic.Pets), without actors.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Imlight.Classic.Pets;
using Xunit;

namespace Imlight.Classic.Tests;

public class PetMinigameTests {

    /// <summary>A Random whose rolls are always the lowest (no wind, the first spot, the first food).</summary>
    private sealed class LowRandom : Random {
        public override int Next(int maxValue) => 0;
        public override int Next() => 0;
        public override int Next(int minValue, int maxValue) => minValue;
        public override double NextDouble() => 0;
    }

    // June 30 2010 (thefriendlynecromancer.blogspot.com, "How does cannon game keep score"): black, blue, red, yellow
    // hits of three scored shots and the reported points. Result index: black 0, blue 1, red 2, yellow 3.
    [Theory]
    [InlineData(3, 0, 0, 0, 0)]
    [InlineData(2, 1, 0, 0, 1)]
    [InlineData(1, 2, 0, 0, 1)]
    [InlineData(1, 0, 2, 0, 1)]
    [InlineData(0, 3, 0, 0, 1)]
    [InlineData(0, 2, 0, 1, 2)]
    [InlineData(1, 1, 0, 1, 2)]
    [InlineData(1, 0, 1, 1, 2)]
    [InlineData(0, 1, 1, 1, 2)]
    [InlineData(0, 1, 2, 0, 2)]
    [InlineData(0, 0, 3, 0, 2)]
    [InlineData(0, 1, 0, 2, 3)]
    [InlineData(0, 0, 2, 1, 3)]
    [InlineData(0, 0, 1, 2, 4)]
    [InlineData(0, 0, 0, 3, 4)]
    public void CannonPointsReproduceEveryDatedObservation(int black, int blue, int red, int yellow, int points) {
        var results = Enumerable.Repeat(0, black).Concat(Enumerable.Repeat(1, blue))
            .Concat(Enumerable.Repeat(2, red)).Concat(Enumerable.Repeat(3, yellow));
        Assert.Equal(points, PetMinigameRules.CannonPoints(results));
    }

    [Theory]
    [InlineData(0f, 3)]
    [InlineData(100f, 3)]
    [InlineData(100.5f, 2)]
    [InlineData(225f, 2)]
    [InlineData(300f, 1)]
    [InlineData(375f, 1)]
    [InlineData(500f, 0)]
    [InlineData(5000f, 0)]
    [InlineData(float.NaN, 0)]
    public void CannonRingsComeFromTheTargetsCollisionMesh(float distance, int result)
        => Assert.Equal(result, PetMinigameRules.CannonResult(distance));

    private static Vector3 Aim(CannonGame game, CannonWind wind, Vector3 point, float seconds = 3f) {
        var a = wind.Acceleration - new Vector3(0, 0, game.Gravity);
        return (point - game.Origin - 0.5f * a * seconds * seconds) / seconds;
    }

    [Fact]
    public void CannonHasAPracticeShotAndThreeScoredShotsAndScoresWhatItFlies() {
        var game = new CannonGame(new Random(7), new Vector3(13394, -84, -72), new Vector3(7183, -639, -7));
        Assert.Null(game.Fire(Vector3.One)); // Nothing is being aimed yet.
        var practice = game.BeginShot()!;
        Assert.Null(game.BeginShot()); // One shot at a time.
        Assert.True(practice.Speed is >= 0 and <= PetMinigameRules.CannonMaxWind);
        Assert.True(practice.DistanceBound > Vector2.Distance(new(13394, -84), new(7183, -639)));
        var first = game.Fire(Aim(game, practice, game.TargetCentre + new Vector3(0, 0, 2000)))!; // over the target
        Assert.True(first.Practice); Assert.Equal(1, first.Ordinal); Assert.Equal(0, first.Result);

        int[] expected = [3, 2, 1];
        float[] offsets = [10f, 180f, 300f];
        for (var i = 0; i < 3; i++) {
            var wind = game.BeginShot()!;
            var shot = game.Fire(Aim(game, wind, game.TargetCentre + new Vector3(0, 0, offsets[i])))!;
            Assert.False(shot.Practice); Assert.Equal(i + 2, shot.Ordinal);
            Assert.True(shot.CrossedTarget);
            Assert.Equal(expected[i], shot.Result);
        }

        Assert.True(game.IsOver);
        Assert.Null(game.BeginShot());
        Assert.Equal(4 + 2 + 1, game.Score);
        Assert.Equal(2, game.Points); // 7: Good (the practice miss does not count).
    }

    [Fact]
    public void CannonRefusesForgedVelocitiesAndMissesThatLandShort() {
        var game = new CannonGame(new LowRandom(), new Vector3(0, 0, 0), new Vector3(-5000, 0, 0));
        var wind = game.BeginShot()!;
        Assert.Equal(0, wind.Speed);
        Assert.Null(game.Fire(new Vector3(float.NaN, 0, 0)));
        Assert.Null(game.Fire(new Vector3(1e9f, 0, 0)));
        var shot = game.Fire(new Vector3(-100, 0, 100))!; // A dribble: lands far short.
        Assert.False(shot.CrossedTarget);
        Assert.Equal(0, shot.Result);
    }

    private static DropSettings Settings(int timeLimit = 40) => new(160, timeLimit, 10, 1f, 2f, 60f, 0.5f, 900f, -100f, 150f, 200f, 1f, [
        new DropFood(193079, 0, 140, 4, 0, 0, 0, 100, 0),
        new DropFood(227087, 1, 150, 4, 0, 0, 0, 100, 0),
        new DropFood(193089, -1, 175, -3, 7, 0, 0, 24, -2),
    ]);

    [Fact]
    public void DropFoodFallsFromTheSettingsAndIsCaughtUnderThePet() {
        var spot = new Vector3(10, 20, 52);
        var game = new DropGame(Settings(), 0, [spot], new LowRandom());
        var first = game.Advance(0.1, spot);
        var spawned = Assert.Single(first, e => e.Kind == DropEventKind.Spawned);
        Assert.Equal(193079u, spawned.Item!.Food.TemplateId); // group 0 food (the lowest roll), never group 1.
        // 900 high, 140 a second: reaches the pet's 150 after about 5.4 s.
        var caught = new List<DropEvent>();
        for (var i = 0; i < 60 && caught.Count == 0; i++) caught.AddRange(game.Advance(0.1, spot).Where(e => e.Kind == DropEventKind.Caught));
        var catch1 = Assert.Single(caught);
        Assert.Equal(4, catch1.Fullness);
        Assert.True(catch1.Scale > 1f);
        Assert.Equal(4, game.Fullness);
    }

    [Fact]
    public void DropFoodAwayFromThePetIsMissedAndTheClockEndsTheGame() {
        var game = new DropGame(Settings(timeLimit: 12), 1, [new Vector3(0, 0, 0)], new LowRandom());
        var events = new List<DropEvent>();
        for (var i = 0; i < 200 && !game.IsOver; i++) events.AddRange(game.Advance(0.1, new Vector3(500, 0, 0)));
        Assert.True(game.IsOver);
        Assert.Contains(events, e => e.Kind == DropEventKind.Missed);
        Assert.DoesNotContain(events, e => e.Kind == DropEventKind.Caught);
        Assert.Single(events, e => e.Kind == DropEventKind.TimesUp);
        Assert.All(events.Where(e => e.Item is not null), e => Assert.Equal(1, e.Item!.Food.Group));
        Assert.Equal(0, game.Points);
        Assert.Empty(game.Advance(0.1, null)); // Nothing after the end.
    }

    [Fact]
    public void DropClockBonusAddsTimeAndJumpingReachesHigher() {
        var settings = Settings() with { Foods = [new DropFood(193089, -1, 100, -3, 7, 0, 0, 1, 0)] };
        var spot = Vector3.Zero;
        var game = new DropGame(settings, 0, [spot], new LowRandom());
        game.Advance(0.1, null); // spawn
        // At 350 high (5.5 s) a standing pet (150) cannot reach it, a jumping one (350) can.
        for (var i = 0; i < 54; i++) Assert.DoesNotContain(game.Advance(0.1, spot), e => e.Kind == DropEventKind.Caught);
        game.Jump();
        var caught = game.Advance(0.1, spot).Single(e => e.Kind == DropEventKind.Caught);
        Assert.Equal(0, caught.Fullness); // Fullness never goes below zero.
        Assert.Equal(47, game.TimeLimit);
    }

    private static List<Vector3> Grid(int count) => Enumerable.Range(0, count).Select(i => new Vector3(i * 500, 0, 0)).ToList();

    [Fact]
    public void MazePlacesThreeClocksAndBonusesAndSnacksEverywhereElse() {
        var game = new MazeGame(Grid(135), new Random(3));
        Assert.Equal(135, game.Pickups.Count);
        Assert.Equal(PetMinigameRules.MazeClocks, game.Pickups.Count(p => p.Kind == MazePickupKind.Clock));
        Assert.Equal(135 - PetMinigameRules.MazeClocks - PetMinigameRules.MazeSpeedBoosts - PetMinigameRules.MazeStars, game.SnackCount);
        Assert.Equal(game.Pickups.Count, game.Pickups.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public void MazePickupsScoreAndTheClockAndStarChangeTheGame() {
        var game = new MazeGame(Grid(20), new Random(5));
        var snack = game.Pickups.First(p => p.Kind == MazePickupKind.Snack);
        var e = Assert.Single(game.PetMoved(snack.Position + new Vector3(150, 0, 0), []));
        Assert.Equal(MazeEventKind.Snack, e.Kind); Assert.Equal(1, e.Score); Assert.Same(snack, e.Pickup);
        Assert.Empty(game.PetMoved(snack.Position, [])); // Eaten once.

        var clock = game.Pickups.First(p => p.Kind == MazePickupKind.Clock);
        Assert.Equal(MazeEventKind.Clock, Assert.Single(game.PetMoved(clock.Position, [])).Kind);
        Assert.Equal(70, game.TimeLimit);

        // A ghost freezes an unprotected pet once, not again while frozen or just after.
        var ghost = (Ghost: 77UL, Position: new Vector3(-9000, 0, 0));
        var far = new Vector3(-9100, 0, 0);
        Assert.Equal(MazeEventKind.Frozen, Assert.Single(game.PetMoved(far, [ghost])).Kind);
        Assert.Empty(game.PetMoved(far, [ghost]));
        game.Advance(PetMinigameRules.MazeFreezeSeconds + PetMinigameRules.MazeGhostGraceSeconds + 0.1);
        Assert.Equal(MazeEventKind.Frozen, Assert.Single(game.PetMoved(far, [ghost])).Kind);

        game.Advance(PetMinigameRules.MazeFreezeSeconds + 0.1);
        var star = game.Pickups.First(p => p.Kind == MazePickupKind.Star);
        Assert.Equal(MazeEventKind.Star, Assert.Single(game.PetMoved(star.Position, [])).Kind);
        var eaten = Assert.Single(game.PetMoved(far, [ghost]));
        Assert.Equal(MazeEventKind.GhostEaten, eaten.Kind); Assert.Equal(77UL, eaten.Ghost);
        Assert.Empty(game.PetMoved(far, [ghost])); // Gone until it respawns.
    }

    // CLASSIC: a frozen pet cannot collect a fresh pickup from a later move report.
    [Theory]
    [InlineData(MazePickupKind.Snack, MazeEventKind.Snack)]
    [InlineData(MazePickupKind.Clock, MazeEventKind.Clock)]
    [InlineData(MazePickupKind.SpeedBoost, MazeEventKind.SpeedBoost)]
    [InlineData(MazePickupKind.Star, MazeEventKind.Star)]
    public void MazeKeepsFreshPickupsUntilTheThreeSecondFreezeExpires(MazePickupKind kind, MazeEventKind eventKind) {
        var game = new MazeGame(Grid(20), new Random(5));
        var pickup = game.Pickups.First(p => p.Kind == kind);
        var pickupCount = game.Pickups.Count;
        var ghost = (Ghost: 77UL, Position: new Vector3(-9000, 0, 0));
        var frozen = Assert.Single(game.PetMoved(ghost.Position, [ghost]));
        Assert.Equal(MazeEventKind.Frozen, frozen.Kind);
        Assert.True(game.Frozen);
        Assert.Equal(3d, PetMinigameRules.MazeFreezeSeconds);

        // Both immediately after the collision and halfway through the verified freeze,
        // ignore movement onto a previously untouched snack or bonus without consuming it.
        for (var attempt = 0; attempt < 2; attempt++) {
            Assert.Empty(game.PetMoved(pickup.Position, []));
            Assert.Contains(pickup, game.Pickups);
            Assert.Equal(pickupCount, game.Pickups.Count);
            Assert.Equal(0, game.Score);
            Assert.Equal(PetMinigameRules.MazeStartSeconds, game.TimeLimit);
            Assert.False(game.Immune);
            Assert.True(game.Frozen);
            Assert.Null(game.Advance(PetMinigameRules.MazeFreezeSeconds / 2));
        }

        Assert.Equal(3d, game.Elapsed);
        Assert.False(game.Frozen);
        var collected = Assert.Single(game.PetMoved(pickup.Position, []));
        Assert.Equal(eventKind, collected.Kind);
        Assert.Same(pickup, collected.Pickup);
        Assert.DoesNotContain(pickup, game.Pickups);
        Assert.Equal(pickupCount - 1, game.Pickups.Count);
        Assert.Equal(kind == MazePickupKind.Snack ? 1 : 0, game.Score);
        Assert.Equal(game.Score, collected.Score);
        Assert.Equal(PetMinigameRules.MazeStartSeconds + (kind == MazePickupKind.Clock ? PetMinigameRules.MazeClockSeconds : 0), game.TimeLimit);
        Assert.Equal(kind == MazePickupKind.Star, game.Immune);
        Assert.Empty(game.PetMoved(pickup.Position, [])); // The same pickup still awards only once.
    }

    [Fact]
    public void MazeEndsOnItsClockAndScoresAgainstSeventy() {
        var game = new MazeGame(Grid(80), new Random(1));
        foreach (var snack in game.Pickups.Where(p => p.Kind == MazePickupKind.Snack).Take(35).ToList()) game.PetMoved(snack.Position, []);
        Assert.Equal(35, game.Score);
        Assert.Null(game.Advance(59));
        Assert.Equal(MazeEventKind.TimesUp, game.Advance(1.5)!.Kind);
        Assert.True(game.IsOver);
        Assert.Null(game.Advance(1));
        Assert.Empty(game.PetMoved(game.Pickups.First().Position, []));
        Assert.Equal(2, game.Points); // 35 of 70.
    }

    [Fact]
    public void FullScoresGiveFourPoints() {
        Assert.Equal(4, PetMinigameRules.ScorePoints(160, 160));
        Assert.Equal(4, PetMinigameRules.ScorePoints(200, 160));
        Assert.Equal(4, PetMinigameRules.ScorePoints(PetMinigameRules.MazeFullScore, PetMinigameRules.MazeFullScore));
        Assert.Equal(0, PetMinigameRules.ScorePoints(0, 70));
    }
}
