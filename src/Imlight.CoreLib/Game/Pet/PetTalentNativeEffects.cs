// CLASSIC: pet stat definitions are a separate native table, never canonical lookup-index rows.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

internal sealed class PetTalentNativeEffects : RootSingleResourceSingleton<PetTalentNativeEffects> {
    protected override string ResourceName => "GameEffectData/PetTalentEffects.xml";
    private IReadOnlyDictionary<string, PetBoostPlayerStatEffectTemplate> _effects;

    protected override void AfterLoad() {
        if (!new BindSerializer().Deserialize(Stream.ToArray(), out GameEffectTemplateList table)
            || table?.m_effectTemplates is null)
            throw new InvalidOperationException("Could not load native passive pet talent definitions.");
        _effects = table.m_effectTemplates.OfType<PetBoostPlayerStatEffectTemplate>()
            .ToDictionary(effect => effect.m_effectName.ToString(), StringComparer.Ordinal);
        Stream.Dispose();
    }

    internal PetBoostPlayerStatEffectTemplate Find(string name) => _effects.GetValueOrDefault(name);
}
