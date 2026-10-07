using System;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Pet;

internal static class PetGameInitializationCodec {
    // CLASSIC: r806919's pet-window factory loads a raw, non-versionable
    // PetGameInfo object with Save | Public (5), without Bind or serializer flags.
    internal static bool TryPrepare(PetGameInfo info, out ByteString data) {
        data = default;
        if (info is null) return false;

        try {
            var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
            if (!serializer.Serialize(info, PropertyFlags.Prop_Save | PropertyFlags.Prop_Public, out var prepared)
                || prepared.Length == 0) return false;

            data = prepared;
            return true;
        }
        catch (Exception) {
            // CLASSIC: preparation happens before pet initialization or session publication.
            return false;
        }
    }
}
