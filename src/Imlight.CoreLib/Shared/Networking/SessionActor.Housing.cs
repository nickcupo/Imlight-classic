using System.Threading;

namespace Imlight.CoreLib.Shared.Networking;

// CLASSIC: trusted in-process attach state; never a generated/on-wire login or attach field.
internal sealed record HousingAttachContext(ulong CharacterId, ulong OwnerId, string Zone, uint DynamicServerProcId, ulong ZoneId);

public sealed partial class SessionActor {
    private HousingAttachContext _housingAttach;
    internal HousingAttachContext HousingAttach => Volatile.Read(ref _housingAttach);
    internal void PublishHousingAttach(HousingAttachContext context) => Volatile.Write(ref _housingAttach, context);
}
