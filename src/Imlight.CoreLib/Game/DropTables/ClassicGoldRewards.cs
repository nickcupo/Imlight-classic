// CLASSIC: generic loot reports only fresh acknowledged gold, without normalizing legacy holdings.
using System;
using System.Threading;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.DropTables;

internal sealed record GoldRewardReceipt(int Acquired, int Balance, int Pouch,
    WIZARD_12_PROTOCOL.MSG_UPDATEGOLD Update, bool Changed);

// Fixtures may refuse native preparation; the fresh read, arithmetic, save and live publication stay real.
internal sealed class GoldRewardDependencies {
    internal Func<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD, bool> Prepare;
}

internal static class ClassicGoldRewards {
    internal static readonly AsyncLocal<GoldRewardDependencies> TestScope = new();

    internal static bool TryGrant(Wizard live, int requested, out GoldRewardReceipt receipt,
        Action<GoldRewardReceipt> publish = null) {
        receipt = null;
        if (!Usable(live) || requested == 0) return false;
        var character = live.CharId;
        var account = live.AccountId;
        // Keep the same lane through enqueueing the detached update, including authoritative no-save reads.
        // CommitCharacterMutation re-enters this character's lane; it never acquires a different lock.
        receipt = WizardCollection.WithCharacterLock(character, () => {
            GoldRewardReceipt prepared = null, read = null;
            Wizard readWizard = null;
            var committed = WizardCollection.CommitCharacterMutation(character, (_, saved) => {
                if (!Valid(live, saved, character, account)) return false;
                var before = saved.GameStats.m_currentGold;
                var pouch = saved.GameStats.m_baseGoldPouch;
                if (before < 0 || pouch < 0) return false;
                // Positive loot fills only available headroom. A valid old balance above the pouch is
                // preserved; a reachable signed negative roll remains an exact sufficient-funds debit.
                var delta = requested > 0 ? Math.Min((long)requested, Math.Max(0L, (long)pouch - before)) : requested;
                var balance = (long)before + delta;
                if (balance < 0 || balance > int.MaxValue) return false;
                var update = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = (int)balance, MaxGold = pouch };
                try {
                    if (MessageEncoder.Encode(update).Length == 0 || TestScope.Value?.Prepare?.Invoke(update) == false) return false;
                }
                catch (Exception) { return false; } // Nothing has been saved or published.
                if (!Valid(live, saved, character, account)) return false;
                prepared = new((int)Math.Max(0L, delta), (int)balance, pouch, update, delta != 0);
                if (delta == 0) {
                    read = prepared; // Authoritative zero-headroom read, with no needless save or live-alias write.
                    readWizard = saved;
                    return false;
                }
                saved.GameStats.m_currentGold = (int)balance;
                return true;
            }, saved => {
                if (!Valid(live, saved, character, account)) throw new InvalidOperationException("Gold reward publication lost its live wizard.");
                live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
                publish?.Invoke(prepared); // Save ACK and live publication precede enqueue, under the original lane.
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            if (committed) return prepared;
            if (read is null || !Valid(live, readWizard, character, account)) return null;
            try { publish?.Invoke(read); }
            catch {
                // Even a read-only native publication failure leaves this connection's view untrusted.
                WizardCollection.MarkInventorySnapshotUncertain(live);
                throw;
            }
            return read;
        });
        return receipt is not null;
    }

    private static bool Usable(Wizard live)
        => live is not null && live.CharId != 0 && live.GameStats is not null
            && !WizardCollection.IsInventorySnapshotUncertain(live);

    private static bool Valid(Wizard live, Wizard saved, ulong character, ulong account)
        => Usable(live) && live.CharId == character && live.AccountId == account
            && saved?.CharId == character && saved.AccountId == account && saved.GameStats is not null;
}
