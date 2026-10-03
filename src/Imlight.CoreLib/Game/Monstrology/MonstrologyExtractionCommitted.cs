using Imlight.CoreLib.Shared.Networking;
namespace Imlight.CoreLib.Game.Monstrology;

// Internal actor notification only; not a wire payload or an award request.
// Combat publishes this AFTER CommitExtraction succeeds; service verifies the persisted receipt.
internal sealed class MonstrologyExtractionCommitted : IServerMessage {
    public byte ServiceID => 106;
    public byte MessageOrder => 250;
    public ulong OwnerId { get; init; }
    public string OperationId { get; init; }
}
