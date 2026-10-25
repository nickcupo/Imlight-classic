#nullable enable

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// CLASSIC: who stands on one two-player Hatchery sigil, in the order they stepped on (at most two). Pure, for tests.
/// </summary>
internal sealed class HatcheryPad {

    private readonly List<(IActorRef Actor, ulong CharId)> _on = [];

    public int Count => _on.Count;

    /// <summary>A wizard is (or is not) on the pad. Returns the pair, side 0 first, the moment two wizards stand on it;
    /// the pad is then empty again for the next pair.</summary>
    public (IActorRef Actor, ulong CharId)[]? Update(IActorRef actor, ulong charId, bool on) {
        var index = _on.FindIndex(x => x.CharId == charId);
        if (!on) {
            if (index >= 0) _on.RemoveAt(index);
            return null;
        }
        if (index >= 0) { _on[index] = (actor, charId); return null; }  // a new session of the same wizard
        if (_on.Count >= 2) return null;
        _on.Add((actor, charId));
        if (_on.Count < 2) return null;
        var pair = _on.ToArray();
        _on.Clear();
        return pair;
    }

    public void Remove(IActorRef actor) => _on.RemoveAll(x => Equals(x.Actor, actor));
}

/// <summary>
/// CLASSIC: the Pet Hatchery's two-player sigils (WizardCity/Interiors/WC_Hatchery, template 112077 "Teleport Pet
/// Morphing", MinigameSigilBehavior, sigil type PetMorphSigil2Actor, a two-player battle circle). Two wizards who stand
/// on one go together to their own copy of ThePhantomZoneWorld/PetGameMorph, where the morph game starts (PetGameService:
/// MSG_PETGAMEINIT with Track = side, which r806919's WizardGraphicalClient::HandlePetGameInit turns into the
/// PetGameMorph window; the client never sends a JOIN for it). The one-player sigils (266075, REQ HasMorphSlot) are a
/// later (Celestia-era id) feature and stay inert. Only while the profile's pet hatching is on.
/// </summary>
internal sealed class HatcherySigilComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory {

    internal const uint TwoPlayerSigil = 112077;
    /// <summary>How close to the circle's centre counts as standing on it (the two-player circle's art is ~250 wide).</summary>
    internal const float Radius = 150f;

    private readonly HatcheryPad _pad = new();
    private bool _active;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go && go.m_templateID == TwoPlayerSigil;

    /// <summary>True when the Hatchery's sigils work: an active classic profile with pet hatching on.</summary>
    internal static bool Enabled
        => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsHatching);

    public override void OnStart() {
        _active = Enabled;
        Logger.Debug("Hatchery sigil {0} in {1}: {2}.", Logger.Args(Entity.ActiveGameObject?.m_globalID.Full, Zone?.ZonePath,
            _active ? "active" : "off (pet hatching is off)"));
    }

    internal static bool Within(Vector3 player, Vector3 pad)
        => new Vector3(player.X - pad.X, player.Y - pad.Y, 0).LengthSquared() <= Radius * Radius;

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) => _pad.Remove(playerActor);

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (!_active || playerWizard is null || Classic.Ambient.AmbientWizards.IsAmbient(playerActor)) return;
        var pair = _pad.Update(playerActor, playerWizard.CharId, Within(playerObj.m_location, Entity.ActiveGameObject.m_location));
        if (pair is null) return;

        var now = System.DateTime.UtcNow;
        var run = GroupInstances.NewRunId(now);
        HatcheryRuns.Register(run, [.. pair.Select(p => p.CharId)], now);
        Logger.Information("Hatchery sigil {0}: {1} and {2} go to their hatching (run {3}).",
            Logger.Args(Entity.ActiveGameObject.m_globalID.Full, pair[0].CharId, pair[1].CharId, run));
        foreach (var (actor, _) in pair) {
            actor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_HATCHERYTRIP { RunId = run });
        }
    }
}
