using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: the spellbook guide to Briskbreeze Tower's boss cheats (the guide block of
// classic-data/creatures/boss-cheats-2009.yaml; TowerGuide, BriskbreezeTower, QuestService.TowerGuide.cs).
public sealed class TowerGuideTests {

    private static string RealPath => Path.Combine(ClassicDataFixture.Root, "creatures", "boss-cheats-2009.yaml");

    private static BossCheats Real() => BossCheatsLoader.Load(RealPath);

    // Loads the real file after an edit, from a temporary classic-data tree (same file name, so the id still matches).
    private static BossCheats LoadEdited(Func<string, string> edit) {
        var original = File.ReadAllText(RealPath);
        var edited = edit(original);
        Assert.NotEqual(original, edited);
        using var data = new TempClassicData();
        var dir = Path.Combine(data.Root, "creatures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "boss-cheats-2009.yaml");
        File.WriteAllText(path, edited, new UTF8Encoding(false));

        return BossCheatsLoader.Load(path);
    }

    private static Wizard WizardWith(params string[] entries) {
        var wizard = new Wizard { CharId = 0xA3B1E00000000777, QuestBehavior = new ServerQuestBehavior() };
        foreach (var entry in entries) {
            wizard.QuestBehavior.SetRegistryValue(entry, 1);
        }

        return wizard;
    }

    // --- content: the guide matches the cheats (drift) ---

    [Fact]
    public void TheRealGuideMatchesTheCheats() {
        var cheats = Real();
        Assert.NotNull(cheats.Guide);
        Assert.Empty(BossCheatGuideCheck.Problems(cheats.Bosses, cheats.Guide!));
        Assert.Equal("Briskbreeze Tower Tricks", cheats.Guide!.Title);
        Assert.Equal(cheats.Bosses.Select(b => b.Template).Order(), cheats.Guide.Bosses.Select(b => b.Template).Order());
    }

    [Fact]
    public void ThePagesReadIntroThenEachBossByFloorAndAreClientSafe() {
        var cheats = Real();
        var pages = cheats.Guide!.PagesInOrder(cheats.Bosses).ToList();
        Assert.Equal(cheats.Guide.Intro[0], pages[0]);
        var angrus = pages.FindIndex(p => p.Contains("Angrus Hollowsoul", StringComparison.Ordinal));
        var orrick = pages.FindIndex(p => p.Contains("Orrick Nightglider", StringComparison.Ordinal));
        Assert.True(angrus > 0 && orrick > angrus, "floor 5 comes before floor 10");
        Assert.Equal(2, pages.Count(p => p.StartsWith("Tip: ", StringComparison.Ordinal)));
        Assert.All(pages, p => Assert.Null(BossCheatGuideCheck.TextProblem(p, BossCheatGuideCheck.MaxPage)));
        Assert.All(pages, p => Assert.DoesNotContain(p, c => "${}<>[]#|%_\"\\".Contains(c)));

        // The owner's list: each trick is in the guide.
        var all = string.Join(" ", pages);
        foreach (var fact in new[] { "Meteor Strike", "Earthquake", "Tower Shield", "Interrupt", "Cleanse Ward", "3 Stompers",
                     "4000", "2000", "Exploding Ember", "10,000", "4 pips", "another" }) {
            Assert.Contains(fact, all, StringComparison.Ordinal);
        }
    }

    [Theory]
    // A cheat changes and the text does not: the Stompers come at 3500.
    [InlineData("    health_below: 4000\n    source: http://web.archive.org", "    health_below: 3500\n    source: http://web.archive.org", "3500")]
    // The text gives a number that is no cheat value.
    [InlineData("10,000 Fire damage", "12,000 Fire damage", "12000")]
    // A cheat the text never names.
    [InlineData("he casts Cleanse Ward on it at once", "he breaks it at once", "Cleanse Ward")]
    // Markup the client would read.
    [InlineData("Tip: use blades, not traps.", "Tip: use $blades$, not traps.", "letters, digits")]
    public void AGuideThatDriftsFromTheCheatsIsRefused(string from, string to, string expected) {
        var error = Assert.Throws<ClassicDataException>(() => LoadEdited(text => {
            Assert.Contains(from, text, StringComparison.Ordinal);

            return text.Replace(from, to, StringComparison.Ordinal);
        }));
        Assert.Contains("guide", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ABossWithoutGuidePagesIsRefused() {
        var error = Assert.Throws<ClassicDataException>(() => LoadEdited(text => {
            var start = text.IndexOf("  - template: 164775\n    shown_as:", StringComparison.Ordinal);
            var end = text.IndexOf("  - template: 164776\n    shown_as:", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);

            return text.Remove(start, end - start);
        }));
        Assert.Contains("has cheats but no guide pages", error.Message);
    }

    // The spell numbers the guide gives are the client's own (r806919 templates), when the local extract is here.
    [Fact]
    public void TheGuidesSpellNumbersAreTheClientTemplatesNumbers() {
        var index = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "w101c-private", "extract", "r806919", "spells", "decoded", "spell_index_r806919.jsonl");
        if (!File.Exists(index)) {
            Assert.Skip($"no local spell index at {index}");
        }

        var cheats = Real();
        var wanted = cheats.Guide!.Spells.ToDictionary(s => s.Spell, StringComparer.Ordinal);
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(index)) {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("path").GetString() is { } path && Path.GetFileNameWithoutExtension(path) is { } name
                && wanted.ContainsKey(name) && path == $"Spells/{name}.xml") {
                found[name] = doc.RootElement.Clone();
            }
        }

        foreach (var spell in cheats.Guide.Spells) {
            Assert.True(found.TryGetValue(spell.Spell, out var t), $"{spell.Spell} is not a client spell");
            Assert.Equal(spell.Name, t.GetProperty("display").GetString());
            Assert.Equal(spell.School, t.GetProperty("school").GetString());
            // The client's boss spells carry their cost as the rank (Meteor StrikeBOSS02: 4); the free casts cost nothing.
            if (spell.Pips > 0) {
                Assert.Equal(spell.Pips, t.GetProperty("rank").GetInt32());
            }

            var damage = t.GetProperty("effects").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "kDamage")
                .Select(e => (int?) e.GetProperty("param").GetInt32()).FirstOrDefault();
            Assert.Equal(spell.Damage, damage);
        }
    }

    // --- the unlock hook and the profile gate ---

    [Fact]
    public void TheTowerUnlocksWithTheSigilsRegistryEntry() {
        Assert.Equal("QT-WC-GNT-C01-001", BriskbreezeTower.UnlockEntry);
        Assert.False(BriskbreezeTower.IsUnlocked(null));
        Assert.False(BriskbreezeTower.IsUnlocked(WizardWith()));
        Assert.False(BriskbreezeTower.IsUnlocked(WizardWith("QT-WC-GNT-C01-002", "WC-GNT-C01-001_Complete")));
        Assert.True(BriskbreezeTower.IsUnlocked(WizardWith(BriskbreezeTower.UnlockEntry)));
    }

    [Fact]
    public void TheGuideShowsOnlyAfterTheUnlockAndOnlyWithCheats() {
        var cheats = Real();
        var locked = WizardWith();
        var unlocked = WizardWith(BriskbreezeTower.UnlockEntry);

        Assert.False(TowerGuide.ShouldShow(locked, cheats));
        Assert.True(TowerGuide.ShouldShow(unlocked, cheats));

        // A profile without the cheats (arc1-2009h1, dev-unrestricted) loads no table: no guide, even unlocked.
        Assert.False(TowerGuide.ShouldShow(unlocked, BossCheats.Empty));
        // A cheat table without a guide block shows nothing either.
        Assert.False(TowerGuide.ShouldShow(unlocked, BossCheats.ForTests(cheats.DungeonZone, [.. cheats.Bosses])));
    }

    [Fact]
    public void OnlyTheLate2009ProfileHasTheCheatsAndSoTheGuide() {
        Assert.Equal("creatures/boss-cheats-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.BossCheats);
        Assert.Null(ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.BossCheats);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.BossCheats);
    }

    // --- what the client gets, and that real quests are untouched ---

    [Fact]
    public void TheQuestLogEntryHasNoArrowAndNeverCompetesWithRealQuests() {
        var cheats = Real();
        var quest = TowerGuide.Quest(cheats.Guide!, isNew: true);
        Assert.Equal(TowerGuide.QuestId, quest.QuestID);
        Assert.Equal(TowerGuide.QuestNameId, quest.QuestNameID);
        Assert.Equal("Briskbreeze Tower Tricks", (string) quest.QuestTitle);
        Assert.Equal(1, quest.NoQuestHelper);
        Assert.Equal(1, quest.SkipQHAutoSelect);
        Assert.Equal(0, quest.Mainline);
        Assert.Equal(0, quest.ReadyToTurnIn);
        Assert.Equal(1, quest.New);
        Assert.Equal(0, TowerGuide.Quest(cheats.Guide!, isNew: false).New);

        var goals = TowerGuide.Goals(cheats);
        Assert.Equal(["Floor 5: Angrus Hollowsoul", "Floor 10: Orrick Nightglider"], goals.Select(g => (string) g.GoalTitle));
        Assert.All(goals, g => {
            Assert.Equal(TowerGuide.QuestId, g.QuestID);
            Assert.Equal(1, g.NoQuestHelper);
            Assert.Equal("", (string) g.GoalDestinationZone);
            Assert.Equal(0, g.UseTally);
            Assert.Equal("Wizard City|Briskbreeze Tower", (string) g.GoalLocation);
        });
        Assert.Equal(goals.Count, goals.Select(g => g.GoalID).Distinct().Count());
        Assert.DoesNotContain(TowerGuide.QuestId, goals.Select(g => g.GoalID));

        // Building and showing the entry adds nothing to the wizard's quests or registry.
        var wizard = WizardWith(BriskbreezeTower.UnlockEntry);
        Assert.True(TowerGuide.ShouldShow(wizard, cheats));
        Assert.Empty(wizard.QuestBehavior.CurrentQuestInstances);
        Assert.Empty(wizard.QuestBehavior.CurrentQuestIDs);
        Assert.Equal([BriskbreezeTower.UnlockEntry], wizard.QuestBehavior.Registry.Keys);
    }

    [Fact]
    public void TheQuestionMarkButtonGetsEveryPageInOrder() {
        var cheats = Real();
        var message = TowerGuide.DialogMessage(cheats);
        Assert.NotNull(message);
        Assert.Equal(TowerGuide.QuestNameId, message!.QuestNameID);

        Assert.True(new ObjectSerializer(Versionable: false).Deserialize<ActorDialog>(message.ActorDialog, 16, out var dialog));
        Assert.Equal(cheats.Guide!.PagesInOrder(cheats.Bosses), dialog!.m_dialogEntries.Select(e => e.m_dialog));
    }

}
