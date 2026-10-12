// CLASSIC: Eloise Merryweather's seamstress (October 2009): stitch the stats of one clothing item onto the
// appearance of another for Crowns.
//
// October 2009 update notes (wiki October_2009_Update_Notes oldid 48297): "Eloise Merryweather, who can be found near
// Zeke in all worlds of the Spiral ... For a small Crowns fee, she will allow you to transfer the Stats of one item onto
// the Appearance of another"; both items leave the backpack and one item with the appearance and colors of one and the
// stats and name of the other is created. The price, 100 Crowns, is wiki Eloise Merryweather oldid 108031
// (2010-10-01). Her NPCs carry CrownServicesBehavior; the client's own seamstress window is opened with
// MSG_SEAMSTRESSOPEN and answers with MSG_STITCHITEMS (ShopService.HandleStitchItems).
#nullable enable
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractSeamstressComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    /// <summary>Crowns per stitch (wiki Eloise Merryweather oldid 108031, 2010-10-01).</summary>
    internal const int StitchCrowns = 100;

    private const string DefaultTitle = "GUI_SeamstressCrowns";

    public string ServiceName     => "SeamstressService";
    public string? NpcIcon        => null;
    public string? NpcNameKey     => null;
    public string? NpcTextKey     => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_SeamstressCrowns"; // "Stitch Items" (client r806919)

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObject
        // Eloise's CrownServicesBehavior is a server-only class the client type list does not have, so she is known by
        // her templates' names: WC/KT/MB/MS/DS/GH-CROWN-FURNITURE (164322-164326, 164452), one by each world's Zeke.
        && gameObject.m_objectName.ToString().EndsWith("-CROWN-FURNITURE", System.StringComparison.Ordinal)
        && ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Seamstress);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new SeamstressOption {
                m_displayKey = DisplayKey,
                m_iconKey = NpcIcon,
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        const string title = DefaultTitle; // CLASSIC: the template's own shop title is not decoded by this server.
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_SEAMSTRESSOPEN {
            GlobalID = Entity.ActiveGameObject.m_globalID,
            ShopTitle = title,
            Credits = playerCharacter.Account?.Crowns ?? 0,
            MergeCost = StitchCrowns,
            WebFailure = 0,
        });
        Broadcast(new GAME_5_PROTOCOL.MSG_WIZBANG { WizBangID = (uint) WizBang, GameObjectID = playerObject.m_globalID });
        Broadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE { State = StringHash.Compute(StateName), GameObjectID = playerObject.m_globalID });
    }

    private void Broadcast(Imcodec.MessageLayer.IMessage message)
        => Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message = message, Selfless = false });

}
