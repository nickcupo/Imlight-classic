using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// CLASSIC: who may pick up a health or mana wisp. Not a wizard in a duel: the wisp's MSG_UPDATEHEALTH / mana update
/// changed the wizard's health between rounds (qa-s90, KT_ChampHall: a KT_WispHealth floating past a duel circle took a
/// fighting wizard from 1807 to 2002, and the next MSG_COMBATHEALTH followed), healing outside the duel's own rules.
/// In combat health and mana change only by the duel (spells, potions in the duel). The wisp stays for later.
/// </summary>
internal static class WispPickup {

    internal static bool Allowed(Wizard wizard) => wizard is not null && !wizard.IsInDuel;
}
