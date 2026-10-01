using Akka.Actor;
namespace Imlight.CoreLib.Shared.Networking;
internal sealed record ZoneAttachContext(string Zone, IActorRef Actor, long Generation, ulong Owner = 0);
