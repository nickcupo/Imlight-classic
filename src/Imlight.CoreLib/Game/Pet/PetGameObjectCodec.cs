// CLASSIC: prepare the existing invisible pet-game logic objects (and the visible pieces of the phantom-zone games)
// before an admitted pet write or publication.
using System;
using System.Collections.Generic;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

internal sealed record PreparedPetGameObject(ulong GlobalId, ByteString Data);

internal static class PetGameObjectCodec {
    internal const uint DanceTemplate = 174075;
    internal const uint CannonTemplate = 174074;
    internal const uint DropTemplate = 174076;
    internal const uint MazeTemplate = 210073;
    internal const string Dance = "PetGameDance";
    internal const string Cannon = "PetGameCannon";
    internal const string Drop = "PetGameDrop";
    internal const string Maze = "PetGameMaze";
    internal const uint BehaviorNameId = 0x640F888B;

    /// <summary>
    /// The invisible logic object (WizItemTemplate with one PetGameBehaviorTemplate naming the game) of each game. The
    /// owned catalog places 174074/174076/210073 in their phantom zones (docs/playtest/2026-10-07-pet-game-native-contracts.md).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, uint> LogicTemplates = new Dictionary<string, uint>(StringComparer.Ordinal) {
        [Dance] = DanceTemplate, [Cannon] = CannonTemplate, [Drop] = DropTemplate, [Maze] = MazeTemplate,
    };

    internal static bool TryPrepareDance(CoreObject owner, out PreparedPetGameObject prepared)
        => TryPrepareLogic(Dance, owner, owner?.m_location ?? default, out prepared);

    /// <summary>The native logic object of <paramref name="game"/>, placed at <paramref name="location"/>.</summary>
    internal static bool TryPrepareLogic(string game, CoreObject owner, Imcodec.Math.Vector3 location, out PreparedPetGameObject prepared) {
        prepared = null;
        if (owner is null || owner.m_globalID.Full == 0 || game is null || !LogicTemplates.TryGetValue(game, out var templateId)) return false;
        try {
            var template = CoreObjectFactory.GetCoreTemplate(templateId);
            if (template is not WizItemTemplate itemTemplate || itemTemplate.m_templateID != templateId
                || template.m_behaviors is not { Count: 1 }
                || template.m_behaviors[0] is not PetGameBehaviorTemplate behavior
                || !string.Equals(behavior.m_behaviorName.ToString(), "PetGameBehavior", StringComparison.Ordinal)
                || !string.Equals(behavior.m_gameName.ToString(), game, StringComparison.Ordinal)) return false;

            var info = new CoreObjectInfo { m_templateID = templateId, m_location = location,
                m_orientation = owner.m_orientation, m_fScale = 1 };
            if (CoreObjectFactory.FinalizeCoreObject(info, template) is not WizClientObjectItem logic
                || logic.m_globalID.Full == 0 || logic.m_globalID.Full == owner.m_globalID.Full) return false;
            // Match the normal zone client projection, without creating a server zone entity or renderer.
            logic.m_characterId = logic.m_globalID;
            CoreObjectFactory.InitializeCoreObjectBehaviors(logic, template);
            if (logic.m_inactiveBehaviors is not { Count: 1 }
                || logic.m_inactiveBehaviors[0] is not ClientPetGameBehavior allocated
                || allocated.m_behaviorTemplateNameID != BehaviorNameId) return false;
            return TrySerialize(logic, out prepared);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A visible game piece (a maze snack, a falling food) of template <paramref name="templateId"/> at
    /// <paramref name="location"/>, as the zone's own object publication would send it, for the owner only.
    /// </summary>
    internal static bool TryPreparePiece(uint templateId, Imcodec.Math.Vector3 location, out PreparedPetGameObject prepared) {
        prepared = null;
        try {
            var template = CoreObjectFactory.GetCoreTemplate(templateId);
            if (template is not GameObjectTemplate) return false;
            var info = new CoreObjectInfo { m_templateID = templateId, m_location = location, m_fScale = 1 };
            if (CoreObjectFactory.FinalizeCoreObject(info, template) is not { } piece || piece.m_globalID.Full == 0) return false;
            piece = CoreObjectFactory.InitializeCoreObjectBehaviors(piece, template);
            return TrySerialize(piece, out prepared);
        }
        catch (Exception) { return false; }
    }

    private static bool TrySerialize(CoreObject value, out PreparedPetGameObject prepared) {
        prepared = null;
        var serializer = ClassicCoreObjectSerializer.Create(false, SerializerFlags.None);
        if (!serializer.Serialize(value, PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit
            | PropertyFlags.Prop_AuthorityTransmit, out var data) || data.Length == 0) return false;
        prepared = new(value.m_globalID.Full, data);
        return true;
    }
}
