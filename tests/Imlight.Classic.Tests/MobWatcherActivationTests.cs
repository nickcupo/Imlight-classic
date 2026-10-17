/*
 * CLASSIC: a mob-watcher trigger that also listens for ActivateFrom_<trigger> (Celestia's Calypso's Cavern,
 * CL-CRAB-C04-001's giver) is not waiting on a kill when another trigger wakes it by name.
 */

using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MobWatcherActivationTests {

    [Fact]
    public void OnlyAnAuthoredActivationSkipsTheKillCheck() {
        Assert.True(KilledMonster.IsAuthoredActivation("ActivateFrom_QuestCheckCL-CRAB-C04-001Trigger"));
        Assert.False(KilledMonster.IsAuthoredActivation("Monster_Killed"));
        Assert.False(KilledMonster.IsAuthoredActivation("EnterZone"));
        Assert.False(KilledMonster.IsAuthoredActivation(null));
    }

    [Fact]
    public void TheScopeCarriesTheEventNameAndRestoresIt() {
        Assert.Null(KilledMonsterScope.EventName);
        using (KilledMonsterScope.Enter(null, "ActivateFrom_X")) {
            Assert.Equal("ActivateFrom_X", KilledMonsterScope.EventName);
            using (KilledMonsterScope.Enter([1UL], "Monster_Killed")) {
                Assert.Equal("Monster_Killed", KilledMonsterScope.EventName);
            }

            Assert.Equal("ActivateFrom_X", KilledMonsterScope.EventName);
        }

        Assert.Null(KilledMonsterScope.EventName);
    }

}
