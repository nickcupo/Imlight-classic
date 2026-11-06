// CLASSIC: answers the spellbook map's MSG_MAPREQUIREMENTS with the gated doodles the player meets (MapRequirements).

using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

internal sealed class MapRequirementsService(SessionActor sessionActor) : MessageService(sessionActor) { // CLASSIC

    private static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new MapRequirementsService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_MAPREQUIREMENTS))]
    private void ReceiveMapRequirements(WIZARD2_53_PROTOCOL.MSG_MAPREQUIREMENTS message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        if (!MapRequirements.TryReadRequests((byte[]) message.Data, out var requests)) {
            Logger.Warning("Map requirements from {0}: the request does not parse; no doodle is passed.", Logger.Args(wizard.CharId));
            requests = [];
        }

        var playerObj = GetActiveGameObject();
        var verdicts = MapRequirements.Judge(requests,
            requirement => RequirementDispatcher.FindHandlerForRequirement(requirement.GetType(),
                new GenericRequirementContext(new RequirementList { m_requirements = [requirement] },
                    SessionActor.ActorRef, playerObj, wizard)) is not null,
            list => RequirementDispatcher.EvaluateRequirements(list,
                new GenericRequirementContext(list, SessionActor.ActorRef, playerObj, wizard)));

        var unevaluable = verdicts.Where(v => v.Verdict == MapRequirements.Verdict.Unevaluable).Select(v => v.Id).ToList();
        if (unevaluable.Count > 0) {
            Logger.Warning("Map requirements from {0}: doodle(s) {1} have requirements the server cannot evaluate; left out.",
                Logger.Args(wizard.CharId, string.Join(",", unevaluable)));
        }

        var answer = MapRequirements.WriteAnswer(verdicts.Where(v => v.Verdict == MapRequirements.Verdict.Passed).Select(v => v.Id));
        if (answer is not null) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_MAPREQUIREMENTS { Data = answer });
        }
    }
}
