using System.IO;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class LaterObjectsTests {

    private static LaterObjects Real() => LaterObjectsLoader.Load(Path.Combine(ClassicDataFixture.Root, "zones", "later-objects.yaml"));

    [Fact]
    public void TheCanonicalProfileNamesTheList() {
        Assert.Equal("zones/later-objects.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.LaterObjects);
        Assert.Equal("zones/later-objects.yaml", ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.LaterObjects);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.LaterObjects);
    }

    [Fact]
    public void AbnerFishingAndTheOldOryanAreHiddenEverywhere() {
        var later = Real();
        Assert.True(later.Hides(1546036, "WizardCity/WC_Hub"));                       // OMNI-HUB-NPC01 Abner K. Doodle
        Assert.True(later.Hides(1546036, null));                                      // '*' needs no zone
        Assert.True(later.Hides(1206837, "WizardCity/WC_Hub"));                       // WC-Pelican-NPC01 Lucky Hookline, the fishing vendor
        Assert.True(later.Hides(1346055, "WizardCity/WC_Hub"));                       // Tome of Fishing
        Assert.True(later.Hides(1560124, "WizardCity/WC_Streets/WC_Unicorn"));        // Archmastery Tome
        Assert.True(later.Hides(1451483, "WizardCity/WC_Streets/WC_Unicorn"));        // the 2019 Private O'Ryan
        Assert.True(later.Hides(1451483, "WIZARDCITY/wc_streets/wc_unicorn"));        // zone names compare without case
    }

    [Fact]
    public void ClassicNpcsPetsAndMonstrologyAreKept() {
        var later = Real();
        Assert.False(later.Hides(38119, "WizardCity/WC_Streets/WC_Unicorn"));        // Private Connelly
        Assert.False(later.Hides(38216, "WizardCity/WC_Hub"));                       // Private Stillson, a Commons NPC of 2009
        Assert.False(later.Hides(1439022, "WizardCity/WC_Streets/Interiors/WC_PET_Park"));   // Hatchmaking Kiosk (pets stay)
        Assert.False(later.Hides(1439026, "WizardCity/WC_Streets/Interiors/WC_PET_Park"));   // Hatchmaking Tome
        Assert.False(later.Hides(1421672, "WizardCity/WC_Streets/WC_Colossus"));     // Monstrologist Burke (Monstrology stays)
        Assert.False(later.Hides(1335354, "WizardCity/WC_Ravenwood"));               // Monstromnibus
    }

    [Fact]
    public void EveryEntryHasAReasonAndEvidence() {
        var later = Real();
        Assert.True(later.Objects.Length > 300);
        Assert.All(later.Objects, o => {
            Assert.False(string.IsNullOrWhiteSpace(o.Reason));
            Assert.False(string.IsNullOrWhiteSpace(o.Evidence));
        });
    }

    [Fact]
    public void AZoneEntryHidesOnlyThatZoneAndTheOnesBelowIt() {
        var path = Write("""
            kind: later-objects
            version: 1
            id: later-objects-test
            profiles: [late-2009]
            license_tag: own
            objects:
            - {template: 10, name: A, zone: WizardCity/WC_Hub, reason: r, evidence: e}
            - {template: 11, name: B, zone: 'WizardCity/Interiors/WC_Park_*', reason: r, evidence: e}
            - {template: 12, name: C, zone: '*', reason: r, evidence: e}
            """);
        var later = LaterObjectsLoader.Load(path);
        Assert.True(later.Hides(10, "WizardCity/WC_Hub"));
        Assert.False(later.Hides(10, "WizardCity/WC_Ravenwood"));
        Assert.False(later.Hides(10, null));
        Assert.True(later.Hides(11, "WizardCity/Interiors/WC_Park_Petshop"));
        Assert.False(later.Hides(11, "WizardCity/Interiors/WC_Hatchery"));
        Assert.True(later.Hides(12, "Krokotopia/KT_Hub"));
        Assert.False(later.Hides(13, "Krokotopia/KT_Hub"));
        Assert.Equal(3, later.TemplateCount);
    }

    [Fact]
    public void AMissingFileIsAnErrorButEmptyHidesNothing() {
        Assert.Throws<ClassicDataException>(() => LaterObjectsLoader.Load(Path.Combine(Path.GetTempPath(), "w101c-no-such-later-objects.yaml")));
        Assert.False(LaterObjects.Empty.Hides(1546036, "WizardCity/WC_Hub"));
    }

    [Fact]
    public void ARepeatedTemplateForTheSameZoneIsRejected() {
        var path = Write("""
            kind: later-objects
            version: 1
            id: later-objects-test
            profiles: [late-2009]
            license_tag: own
            objects:
            - {template: 10, name: A, zone: '*', reason: r, evidence: e}
            - {template: 10, name: A again, zone: '*', reason: r, evidence: e}
            """);
        var error = Assert.Throws<ClassicDataException>(() => LaterObjectsLoader.Load(path));
        Assert.Contains("listed twice", error.Message);
    }

    [Fact]
    public void ABadZoneOrMissingEvidenceIsRejected() {
        var path = Write("""
            kind: later-objects
            version: 1
            id: later-objects-test
            profiles: [late-2009]
            license_tag: own
            objects:
            - {template: 10, name: A, zone: 'Wizard City!', reason: r, evidence: e}
            - {template: 11, name: B, zone: '*', reason: r}
            """);
        var error = Assert.Throws<ClassicDataException>(() => LaterObjectsLoader.Load(path));
        Assert.Contains("not a valid zone pattern", error.Message);
        Assert.Contains("evidence", error.Message);
    }

    private static string Write(string yaml) {
        var dir = Directory.CreateTempSubdirectory("w101c-later-");
        var path = Path.Combine(dir.FullName, "later-objects-test.yaml");
        File.WriteAllText(path, yaml);

        return path;
    }

}
