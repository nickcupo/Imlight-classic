// CLASSIC: the native arena shop validates ticket balances and the wizard's own ranked ladder.
#nullable enable

using System;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Arena;

internal static class ArenaShopSnapshot {

    // CLASSIC: r806919 ReqArenaSeasonTitle::Evaluate (0x142144790) reads WizGameStats+0xb0
    // for PvPSanctioned, then Ladder+0x1d0 for its score. A null ladder fails even Private.
    // This is derived from the ranked document on every attach; a saved GameStats cache is not authority.
    internal static Ladder OwnLadder(ulong charId, ArenaConfig config, IArenaLadderStore store) {
        if (charId == 0) throw new ArgumentOutOfRangeException(nameof(charId));
        var saved = store.Load(charId);
        if (saved is not null && saved.CharId != charId)
            throw new InvalidOperationException("The saved arena ladder belongs to another character.");

        var wins = saved?.Wins ?? 0;
        var losses = saved?.Losses ?? 0;
        return new Ladder {
            m_characterID = charId,
            m_gameNameID = ArenaRules.Hash(config.RankedTournament),
            m_gamesWon = wins,
            m_gamesLost = losses,
            m_gamesTied = 0,
            m_gamesPlayed = checked(wins + losses),
            m_score = saved?.Rating ?? config.StartRating,
        };
    }

    internal static void RefreshLadder(Wizard wizard) {
        if (!ClassicArena.Enabled || ClassicArena.Config is not { } config || wizard.GameStats is null) return;
        wizard.GameStats.m_pArenaLadder = OwnLadder(wizard.CharId, config, new ArenaLadderCollection.Raven());
    }

    // CLASSIC: request identity must match the already authenticated active wizard. NameBlob is display data,
    // never authority to look up another character or account. Unsupported (Practice/Derby/modern) ladders are not invented.
    internal static GAME_5_PROTOCOL.MSG_LADDER? Reply(Wizard wizard, ulong requestedCharacterId,
        uint tournamentNameId, ArenaConfig config, IArenaLadderStore store) {
        if (wizard.CharId == 0 || wizard.GameStats is null || requestedCharacterId != wizard.CharId
            || tournamentNameId != ArenaRules.Hash(config.RankedTournament)) return null;
        var ladder = OwnLadder(wizard.CharId, config, store);
        wizard.GameStats.m_pArenaLadder = ladder;
        // r806919 CharacterInfoWindow::MSG_LADDER (0x14071c790) deserializes a single Ladder with mask 0x18.
        return new GAME_5_PROTOCOL.MSG_LADDER {
            CharacterID = wizard.CharId,
            LadderData = ArenaMessages.Blob(ladder),
        };
    }

    internal sealed record Snapshot(int ArenaPoints, int PvpCurrency, Ladder Ladder);

    // CLASSIC: refresh the native shop's wallet from the same saved character under the shared write lane,
    // without saving, crediting tickets, or changing health/equipment. Both wallet aliases retain their saved values.
    internal static Snapshot? ReadOwn(Wizard wizard, ArenaConfig config, IArenaLadderStore ladderStore,
        Func<ulong, Wizard?> loadWizard) => WizardCollection.WithCharacterLock<Snapshot?>(wizard.CharId, () => {
        if (wizard.CharId == 0 || wizard.GameStats is null) return null;
        var saved = loadWizard(wizard.CharId);
        if (saved?.GameStats is null || saved.CharId != wizard.CharId || saved.AccountId != wizard.AccountId)
            return null;
        var ladder = OwnLadder(wizard.CharId, config, ladderStore);
        var snapshot = new Snapshot(saved.GameStats.m_currentArenaPoints, saved.GameStats.m_currentPvPCurrency, ladder);
        wizard.GameStats.m_currentArenaPoints = snapshot.ArenaPoints;
        wizard.GameStats.m_currentPvPCurrency = snapshot.PvpCurrency;
        wizard.GameStats.m_pArenaLadder = snapshot.Ladder;
        return snapshot;
    });

    internal static Snapshot? ReadOwn(Wizard wizard) => ClassicArena.Enabled && ClassicArena.Config is { } config
        ? ReadOwn(wizard, config, new ArenaLadderCollection.Raven(), WizardCollection.GetCharacterUnloaded)
        : null;
}
