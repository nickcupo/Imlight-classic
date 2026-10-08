// CLASSIC: the Great Spyre lift and the Oasis boat rides (Imlight.Classic.Quests.ClassicRides).
//
// On arrival in a ride zone: two seconds later (the client has the zone loaded and shows the ride) the ride's start
// event is posted once as a real volume enter, so the ride zone's own triggers move the platform or ship and, after
// the ride's waits, teleport the wizard on. If the wizard is still in that ride zone FallbackSeconds after arrival,
// the wizard is sent to the destination of the ride's final trigger (its ZoneTransfer record), exactly as that
// trigger would have. A later visit of the same ride re-arms both timers; an earlier visit's timers do nothing.
using System;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imlight.Classic;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal sealed class RideService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string START_TIMER = "ride-start";
    private const string FALLBACK_TIMER = "ride-fallback";
    private static long s_arrivals;
    private long _arrival;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (!ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive || GetActiveWizard() is not { } wizard
            || ClassicRides.Find(wizard.Zone) is not { } ride) {
            return;
        }

        _arrival = Interlocked.Increment(ref s_arrivals);
        Logger.Information("[RIDE] {0} boards {1}; fallback in {2} s.", Logger.Args(wizard.CharId, ride.Zone, ride.FallbackSeconds));
        Timers.StartSingleTimer(START_TIMER, new CLASSIC_FEATURES_PROTOCOL.MSG_RIDEFALLBACK {
            Zone = ride.Zone, Arrival = _arrival, Start = true,
        }, TimeSpan.FromSeconds(2));
        Timers.StartSingleTimer(FALLBACK_TIMER, new CLASSIC_FEATURES_PROTOCOL.MSG_RIDEFALLBACK {
            Zone = ride.Zone, Arrival = _arrival, Start = false,
        }, TimeSpan.FromSeconds(ride.FallbackSeconds));
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_RIDEFALLBACK))]
    private void ReceiveRideTimer(CLASSIC_FEATURES_PROTOCOL.MSG_RIDEFALLBACK message) {
        var wizard = GetActiveWizard();
        if (ClassicRides.Find(message.Zone) is not { } ride
            || !ClassicRides.FallbackApplies(ride, wizard?.Zone, message.Arrival, _arrival)) {
            return; // the ride already moved them, or this is an earlier visit's timer
        }

        if (wizard!.IsInDuel) {
            // Rides have no duels, but never move a wizard out of one.
            Timers.StartSingleTimer(FALLBACK_TIMER, message, TimeSpan.FromSeconds(3));
            return;
        }

        if (message.Start) {
            SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = ride.StartEvent,
                PlayerActor = SessionActor.ActorRef,
                PlayerGameObject = GetActiveGameObject(),
                ArrivedInside = false,
            });
            return;
        }

        var teleport = ZoneDataCollection.GetZoneData(ride.Zone)?.Teleports
            ?.FirstOrDefault(entry => string.Equals(entry.TriggerName, ride.FinalTrigger, StringComparison.Ordinal))?.Teleport;
        if (teleport is null || string.IsNullOrEmpty(teleport.m_destinationZone)) {
            Logger.Error("[RIDE] {0} has no destination for {1}; {2} stays (travel data error).",
                Logger.Args(ride.Zone, ride.FinalTrigger, wizard.CharId));
            return;
        }

        Logger.Warning("[RIDE] {0} was still on {1} after {2} s; sending them to {3}.",
            Logger.Args(wizard.CharId, ride.Zone, ride.FallbackSeconds, teleport.m_destinationZone));
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = teleport.m_destinationZone,
            DestinationLocation = teleport.m_destinationLoc,
            SendToClient = true,
            OwnerCharId = wizard.CharId,
            KeepInstance = true, // as the ride's own trigger (ResTeleportHandler)
        });
    }

}
