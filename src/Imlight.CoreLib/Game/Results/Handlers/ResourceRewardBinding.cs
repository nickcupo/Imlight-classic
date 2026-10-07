// CLASSIC: result resource writes belong to the queried wizard's existing authenticated player session.
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal static class ResourceRewardBinding {
    internal static bool TryAccount(Wizard wizard, IResultContext context, out ulong accountId) {
        accountId = 0;
        var player = context?.GetPlayerRef();
        if (player is null || player.IsNobody() || PlayerQuery.IsGone(player)
            || player is IInternalActorRef { IsTerminated: true }
            || wizard?.Account is not { AccountId: > 0 } account || account.AccountId != wizard.AccountId
            || account.CharacterIds?.Contains(wizard.CharId) != true || account.SessionActor is not { } session
            || session.IsDisposed || !Equals(session.ActorRef, player)) return false;
        if (context.GetPlayerObj() is { } supplied && (supplied is not WizClientObject native
            || native.m_characterId.Full != wizard.CharId || native.m_globalID.Full != wizard.GameObjectID
            || native.m_permID.Full != wizard.GameObjectID)) return false;
        accountId = account.AccountId; return true;
    }
}
