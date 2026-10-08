// CLASSIC: prepare the existing invisible Dance logic before an admitted pet write or publication.
using System;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

internal sealed record PreparedPetGameObject(ulong GlobalId, ByteString Data);

internal static class PetGameObjectCodec {
    internal const uint DanceTemplate = 174075;
    internal const string Dance = "PetGameDance";
    internal const uint BehaviorNameId = 0x640F888B;

    internal static bool TryPrepareDance(CoreObject owner, out PreparedPetGameObject prepared) {
        prepared = null;
        if (owner is null || owner.m_globalID.Full == 0) return false;
        try {
            var template = CoreObjectFactory.GetCoreTemplate(DanceTemplate);
            if (template is not WizItemTemplate itemTemplate || itemTemplate.m_templateID != DanceTemplate
                || template.m_behaviors is not { Count: 1 }
                || template.m_behaviors[0] is not PetGameBehaviorTemplate behavior
                || !string.Equals(behavior.m_behaviorName.ToString(), "PetGameBehavior", StringComparison.Ordinal)
                || !string.Equals(behavior.m_gameName.ToString(), Dance, StringComparison.Ordinal)) return false;

            var info = new CoreObjectInfo { m_templateID = DanceTemplate, m_location = owner.m_location,
                m_orientation = owner.m_orientation, m_fScale = 1 };
            if (CoreObjectFactory.FinalizeCoreObject(info, template) is not WizClientObjectItem logic
                || logic.m_globalID.Full == 0 || logic.m_globalID.Full == owner.m_globalID.Full) return false;
            // Match the normal zone client projection, without creating a server zone entity or renderer.
            logic.m_characterId = logic.m_globalID;
            CoreObjectFactory.InitializeCoreObjectBehaviors(logic, template);
            if (logic.m_inactiveBehaviors is not { Count: 1 }
                || logic.m_inactiveBehaviors[0] is not ClientPetGameBehavior allocated
                || allocated.m_behaviorTemplateNameID != BehaviorNameId) return false;
            var serializer = ClassicCoreObjectSerializer.Create(false, SerializerFlags.None);
            if (!serializer.Serialize(logic, PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit
                | PropertyFlags.Prop_AuthorityTransmit, out var data) || data.Length == 0) return false;
            prepared = new(logic.m_globalID.Full, data);
            return true;
        }
        catch (Exception) { return false; }
    }
}
