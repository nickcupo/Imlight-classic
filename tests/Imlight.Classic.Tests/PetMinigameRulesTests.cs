// CLASSIC: the server's Cannon/Drop/Maze constants equal classic-data/rules/pet-minigames-2010.yaml, where each value's
// confidence and dated sources are recorded (unverified values included).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Imlight.Classic.Pets;
using Xunit;
using YamlDotNet.Serialization;

namespace Imlight.Classic.Tests;

public class PetMinigameRulesTests {

    private static Dictionary<object, object> Games() {
        var path = Path.Combine(ClassicDataFixture.Root, "rules", "pet-minigames-2010.yaml");
        if (!File.Exists(path)) {
            Assert.Skip("classic-data/rules/pet-minigames-2010.yaml is not in this classic-data yet (data branch claude/pet-games-data)");
        }

        var document = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        return (Dictionary<object, object>) document["games"];
    }

    private static object Value(Dictionary<object, object> games, string game, string name)
        => ((Dictionary<object, object>) ((Dictionary<object, object>) games[game])[name])["value"];

    private static double Number(object value) => double.Parse((string) value, CultureInfo.InvariantCulture);

    private static double[] Numbers(object value) => ((List<object>) value).Select(Number).ToArray();

    [Fact]
    public void TheServersConstantsAreTheDocumentedOnes() {
        var g = Games();
        Assert.Equal(PetMinigameRules.CannonShots, Number(Value(g, "cannon", "shots")));
        Assert.Equal(PetMinigameRules.CannonScoredShots, Number(Value(g, "cannon", "scored_shots")));
        Assert.Equal(PetMinigameRules.CannonRingRadii.Select(r => (double) r), Numbers(Value(g, "cannon", "ring_radii")));
        Assert.Equal(PetMinigameRules.CannonTargetCentreHeight, Number(Value(g, "cannon", "target_centre_height")), 2);
        Assert.Equal(PetMinigameRules.CannonResultValues.Select(v => (double) v), Numbers(Value(g, "cannon", "result_values")));
        Assert.Equal(PetMinigameRules.CannonPointThresholds.Select(v => (double) v), Numbers(Value(g, "cannon", "point_thresholds")));
        Assert.Equal(PetMinigameRules.CannonGravity, Number(Value(g, "cannon", "gravity")));
        Assert.Equal(PetMinigameRules.CannonMaxWind, Number(Value(g, "cannon", "max_wind")));
        Assert.Equal(PetMinigameRules.CannonWindAcceleration, Number(Value(g, "cannon", "wind_acceleration")));
        Assert.Equal(PetMinigameRules.CannonDistanceMargin, Number(Value(g, "cannon", "distance_margin")));
        Assert.Equal(PetMinigameRules.DropJumpSeconds, Number(Value(g, "drop", "jump_seconds")));
        Assert.Equal(PetMinigameRules.MazeStartSeconds, Number(Value(g, "maze", "start_seconds")));
        Assert.Equal(PetMinigameRules.MazeClockSeconds, Number(Value(g, "maze", "clock_seconds")));
        Assert.Equal(PetMinigameRules.MazeClocks, Number(Value(g, "maze", "clocks")));
        Assert.Equal(PetMinigameRules.MazeSpeedBoosts, Number(Value(g, "maze", "speed_boosts")));
        Assert.Equal(PetMinigameRules.MazeStars, Number(Value(g, "maze", "stars")));
        Assert.Equal(PetMinigameRules.MazeImmunitySeconds, Number(Value(g, "maze", "immunity_seconds")));
        Assert.Equal(PetMinigameRules.MazeFreezeSeconds, Number(Value(g, "maze", "freeze_seconds")));
        Assert.Equal(PetMinigameRules.MazeGhostGraceSeconds, Number(Value(g, "maze", "ghost_grace_seconds")));
        Assert.Equal(PetMinigameRules.MazeGhostRespawnSeconds, Number(Value(g, "maze", "ghost_respawn_seconds")));
        Assert.Equal(PetMinigameRules.MazeFullScore, Number(Value(g, "maze", "full_score")));
        Assert.Equal(PetMinigameRules.MazeGhostScore, Number(Value(g, "maze", "ghost_score")));
        Assert.Equal(PetMinigameRules.MazePickupRadius, Number(Value(g, "maze", "pickup_radius")));
        Assert.Equal(PetMinigameRules.MazeGhostRadius, Number(Value(g, "maze", "ghost_radius")));
    }
}
