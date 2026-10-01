using System;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Monstrology;

internal sealed record MonstrologyLevel(byte Level, int ExperienceValue, string Name);
internal sealed record MonstrologyMob(string World, int CollectionResistance, bool Boss,
    int SummonAnimus, int SummonGold, int GuestAnimus, int GuestGold, int KillAnimus, int KillGold,
    uint GuestTemplate, uint AlternateTemplate, uint CollectedTemplate);

internal static class MonstrologyMetadata {
    // Literal per-level costs; MonstrologyProgression converts them using the owned client sum loops.
    internal static MonstrologyLevel[] ReadLevels(byte[] bindData, out byte maximumLevel) {
        if (!new BindSerializer().Deserialize<MonsterMagicXPConfig>(bindData, 1, out var config) || config == null)
            throw new ArgumentException("Invalid installed Monstrology XP config");
        maximumLevel = config.m_maxLevel;
        var levels = config.m_levelInfo.Select(level =>
            new MonstrologyLevel(level.m_level, level.m_xpToLevel, level.m_levelName)).ToArray();
        if (maximumLevel == 0 || levels.Length == 0 || levels.Any(x => x.ExperienceValue < 0)
            || levels.Select(x => x.Level).Distinct().Count() != levels.Length)
            throw new ArgumentException("Invalid installed Monstrology levels");
        return levels;
    }
    internal static MonstrologyMob ReadMob(MobMonsterMagicBehaviorTemplate metadata) {
        ArgumentNullException.ThrowIfNull(metadata);
        var costs = new[] { metadata.m_essencesPerSummonTC, metadata.m_goldPerSummonTC,
            metadata.m_essencesPerHouseGuest, metadata.m_goldPerHouseGuest,
            metadata.m_essencesPerKillTC, metadata.m_goldPerKillTC };
        if (costs.Any(x => x < 0)) throw new ArgumentException("Invalid Monstrology creation costs");
        return new(metadata.m_worldName, metadata.m_collectionResistance, metadata.m_isBoss,
            metadata.m_essencesPerSummonTC, metadata.m_goldPerSummonTC,
            metadata.m_essencesPerHouseGuest, metadata.m_goldPerHouseGuest,
            metadata.m_essencesPerKillTC, metadata.m_goldPerKillTC,
            metadata.m_houseGuestTemplateID, metadata.m_alternateMobTemplateID, metadata.m_collectedAsTemplateID);
    }
}
