// CLASSIC: the round waits for the caster's cast plus each cinematic stage the client plays, once.
// The r806919 stage lists below are the client's own (Root.wad Cinematics/<name>.xml, decoded 2026-10-11); the 2014
// ones come from classic-data/rules/spell-animations-2014.json, as the restricted profiles play them.
using System;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CastWaitTimeTests : IDisposable {

    private const float Cast = 5.0f;   // CombatResolver.SPELL_CAST_TIME

    public CastWaitTimeTests() {
        var config = Path.Combine(Path.GetTempPath(), "w101c-castwait-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-castwait.log")}\n");
        ConfigurationManager.Initialize(config);
    }

    public void Dispose() {
        ClassicSpellAnimations.Initialize(null, "late-2009");
        ClassicRuntime.ResetForTests();
    }

    private static CinematicStageTemplate S(string name, float d) => new() { m_name = name, m_duration = d };
    private static CinematicStageTemplate Summon(float d) => new SummonCinematicStageTemplate { m_name = "Summon", m_duration = d };
    private static CinematicStageTemplate Act(string name, float d) => new ActCinematicStageTemplate { m_name = name, m_duration = d };
    private static CinematicStageTemplate Hanging(string name, float d) => new AddHangingCinematicStageTemplate { m_name = name, m_duration = d };
    private static CinematicStageTemplate Release(float d) => new ReleaseCinematicStageTemplate { m_name = "Release", m_duration = d };
    private static CinematicStageTemplate Take(float d) => new TakeCinematicStageTemplate { m_name = "Take", m_duration = d };
    private static CinematicStageTemplate Give(float d) => new GiveCinematicStageTemplate { m_name = "Give", m_duration = d };

    private static CinematicTemplate T(string name, params CinematicStageTemplate[] stages)
        => new() { m_name = name, m_stages = stages.ToList(), m_actors = [] };

    // r806919's AoE layout: Summon 5.4, StartAOEAct 0, Act 0, StopAOEAct 9.13, AddHanging2 (1 s), Release 1.
    private static readonly CinematicTemplate RaR806919 = T("Ra", Summon(5.4f), S("StartAOEAct", 0f), S("Act", 0f),
        S("StopAOEAct", 9.13f), Hanging("AddHanging2", 1.0f), Release(1.0f));
    private static readonly CinematicTemplate WraithR806919 = T("Wraith", Summon(5.2f), Take(8.367f), Give(3.233f), Release(3.3f));
    private static readonly CinematicTemplate VampireR806919 = T("Vampire", Summon(7.333f), Take(2.033f), Give(4.133f));
    private static readonly CinematicTemplate GhoulR806919 = T("Ghoul", Summon(4.533f), Take(4.033f), Give(1.633f));
    private static readonly CinematicTemplate ScarecrowR806919 = T("Scarecrow", Summon(4.6f), S("StartAOETake", 0f), S("Take", 0f),
        S("StopAOETake", 2.13f), S("StartAOEGive", 0f), S("Give", 0f), S("StopAOEGive", 4.267f), Release(1.37f));

    [Fact]
    public void RaInTheAoeLayoutCountsItsSummonOnce() {
        // 16.53 s of stages (the summon once) after the cast; the old sum waited 26.93 s.
        Assert.Equal(Cast + 16.53f, SpellCinematics.GetCastWaitTime(RaR806919, Cast), 2);
    }

    [Fact]
    public void WraithTakeGiveReleaseCountsItsSummonOnce() {
        // 20.1 s of stages; the old sum waited 30.3 s.
        Assert.Equal(Cast + 20.1f, SpellCinematics.GetCastWaitTime(WraithR806919, Cast), 2);
    }

    [Fact]
    public void VampireTakeGiveCountsItsSummonOnce() {
        Assert.Equal(Cast + 13.499f, SpellCinematics.GetCastWaitTime(VampireR806919, Cast), 2);
    }

    [Fact]
    public void GhoulTakeGiveCountsItsSummonOnce() {
        Assert.Equal(Cast + 10.199f, SpellCinematics.GetCastWaitTime(GhoulR806919, Cast), 2);
    }

    [Fact]
    public void ScarecrowAoeTakeGiveCountsItsSummonOnce() {
        Assert.Equal(Cast + 12.367f, SpellCinematics.GetCastWaitTime(ScarecrowR806919, Cast), 2);
    }

    [Fact]
    public void ActLayoutKeepsSummonCastAndLongestAct() {
        // Kraken (same in both clients): Summon 4.3 + cast + Act 3.3, as before.
        Assert.Equal(4.3f + Cast + 3.3f, SpellCinematics.GetCastWaitTime(T("Kraken", Summon(4.3f), Act("Act", 3.3f)), Cast), 3);
        // A creature's act variants: the client plays one, timed by the longest.
        Assert.Equal(2.0f + Cast + 2.867f,
            SpellCinematics.GetCastWaitTime(T("Fire Cat", Summon(2.0f), Act("Act1", 2.867f), Act("Act2", 2.867f), Act("Act3", 2.867f)), Cast), 3);
    }

    [Fact]
    public void AnAoeStageBesideATimedActIsPlayedToo() {
        // r806919 Storm Lord: StopAOEAct1 5.8 s and Act2 3.3 s (the stun) both play.
        var stormLord = T("Storm Lord", Summon(4.967f), S("StartAOEAct1", 0f), S("Act1", 0f), S("StopAOEAct1", 5.8f),
            S("Act2", 3.3f), Hanging("AddHanging2", 0f), S("Incoming", 1f), Release(1f));
        Assert.Equal(4.967f + Cast + 3.3f + 5.8f, SpellCinematics.GetCastWaitTime(stormLord, Cast), 3);
    }

    [Fact]
    public void ACastingStageIsTheCastAndIsNotCountedTwice() {
        var wand = T("Wand", S("Casting", 2.0f), S("Bolt", 1.5f));
        Assert.Equal(3.5f, SpellCinematics.GetCastWaitTime(wand, Cast), 3);
        Assert.Equal(Cast, SpellCinematics.GetCastWaitTime((CinematicTemplate) null!, Cast), 3);
        Assert.Equal(Cast, SpellCinematics.GetCastWaitTime((string) null!, Cast), 3);
    }

    private static void Use(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
        ClassicSpellAnimations.Initialize(ClassicDataFixture.Root, profile);
    }

    [Theory]
    [InlineData("arc1-2009h1")]
    public void Restored2014CinematicsAreTimedFromTheirOwnStages(string profile) {
        Use(profile);
        // Ra (2014): Summon 5.4 + Act 9.133, an Act layout: summon + cast + act (no Weakness stage, no double count).
        Assert.Equal(5.4f + Cast + 9.133f, SpellCinematics.GetCastWaitTime("Ra", Cast), 2);
        // Ghoul (2014): Summon 4.533, Take 4.033, Give 1.633: cast + 10.2 s; the old sum waited 19.73 s.
        Assert.Equal(Cast + 10.199f, SpellCinematics.GetCastWaitTime("Ghoul", Cast), 2);
        // Scarecrow (2014): Summon 4.6, Take 2.133, Give 2.033 (r806919 lengthened it to 4.3 s), Release 1.37.
        Assert.Equal(Cast + 10.136f, SpellCinematics.GetCastWaitTime("Scarecrow", Cast), 2);
        // Orthrus (2014): both heads attack; Act layout, unchanged: summon + cast + the longest act.
        Assert.Equal(7.233f + Cast + 7.9f, SpellCinematics.GetCastWaitTime("Orthrus", Cast), 2);
        // Storm Lord (2014): the original 5.0 s attack; its 0 s "Act2" and the release are not the AoE layout.
        Assert.Equal(4.967f + Cast + 5.0f, SpellCinematics.GetCastWaitTime("Storm Lord", Cast), 2);
    }

}
