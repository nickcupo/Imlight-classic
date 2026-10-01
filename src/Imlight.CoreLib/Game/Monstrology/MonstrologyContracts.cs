using System;
using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.ObjectProperty;
using System.IO;

namespace Imlight.CoreLib.Game.Monstrology;

internal static class MonstrologyContracts {
    // Actual generated object, not JSON and not an assertion of the stock STR wrapper.
    internal static CollectedEssenceTrackingList Tracking(ulong owner, ExtractionReceipt receipt) {
        if (owner == 0 || receipt.Creature == 0 || receipt.Animus <= 0)
            throw new ArgumentException("Invalid authoritative extraction receipt");
        return new CollectedEssenceTrackingList {
            m_essenceTrackingList = new() {
                new CollectedEssenceTrackingData {
                    m_ownerGID = owner, m_templateID = receipt.Creature, m_essencesCollected = receipt.Animus
                }
            },
            m_collectedEssenceCount = receipt.Animus,
            m_failedToCollectReason = 0
        };
    }

    internal static byte[] EncodeEssence(ulong owner, ExtractionReceipt receipt) {
        var serializer = new ObjectSerializer(Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(Tracking(owner, receipt), 5, out var data))
            throw new InvalidDataException("Could not serialize generated essence tracking");
        if (data.Length > ushort.MaxValue) throw new InvalidDataException("Essence exceeds stock STR capacity");
        return data;
    }

    // Caller must be a trusted successful-extraction adapter; never expose this as client input.
    // Eligibility/rate/config interpretation is not inferred from a generic combat win.
    internal static MonstrologyResult CommitExtraction(MonstrologyRepository repository, bool enabled, MonstrologySessionPolicy sessionPolicy,
        ulong authenticatedOwner, ExtractionAward decision, IReadOnlyList<int> validatedThresholds) {
        if (sessionPolicy == null || !sessionPolicy.Allows(enabled) || authenticatedOwner == 0 || decision.OwnerId != authenticatedOwner)
            return MonstrologyResult.Rejected;
        return sessionPolicy.WithPermission(enabled, () => repository.Transact(authenticatedOwner,
            state => MonstrologyRules.Award(state, decision, validatedThresholds)));
    }
}
