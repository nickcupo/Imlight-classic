/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * CLASSIC ARENA (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the 2009 arena's switch and data (classic-data/pvp/arena-*.yaml
 * of the running profile, [Classic] ArenaMatches and the profile's
 * pvp_arena feature), the arena hall as a shared zone, and the server side
 * of the matchmaker (ServerArenaWorld: online wizards, sessions, the
 * ladder in RavenDB).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imlight.Classic;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic.Arena;

/// <summary>CLASSIC: the 2009 arena's data and switch.</summary>
public static class ClassicArena {

    private static ArenaConfig? s_config;

    /// <summary>The loaded arena, or null.</summary>
    public static ArenaConfig? Config => s_config;

    /// <summary>True while the arena matches are on: the data, [Classic] ArenaMatches and the profile's pvp_arena.</summary>
    public static bool Enabled => s_config is not null && ClassicSettings.ArenaMatches
        && (!ClassicRuntime.IsActive || ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PvpArena));

    public static void Initialize(string? classicDataRoot, string profileId) {
        if (s_config is not null) {
            // CLASSIC: every GameServer resource load replaces SpiralDB's inventories. Keep the parsed arena data,
            // but restore its vendors after each load, including another realm or an actor restart.
            RefreshTicketVendorInventories();
            return;
        }

        if (classicDataRoot is null || !Directory.Exists(Path.Combine(classicDataRoot, "pvp"))) {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(classicDataRoot, "pvp"), "arena-*.yaml").Order()) {
            try {
                var config = ArenaLoader.Load(path);
                if (config.Profiles.Contains(profileId, StringComparer.Ordinal)) {
                    s_config = config;
                    break;
                }
            }
            catch (ClassicDataException ex) {
                Logger.Error("Classic arena: {Path} is invalid, so the arena guards are off: {Error}", Logger.Args(path, ex.Message));

                return;
            }
        }

        if (s_config is null) {
            return;
        }

        Logger.Information("Classic arena: guards {0} (Practice) and {1} (Ranked), {2} arenas ({3}).",
            Logger.Args(s_config.PracticeKiosk, s_config.RankedKiosk, s_config.Arenas.Length, s_config.SourceFile));

        RefreshTicketVendorInventories();

        AdminDashboard.AddSection("Arena matches", () => ArenaMatchmaker.Instance?.Snapshot()
            .Select(m => new { m.Id, kind = m.Kind.ToString(), phase = m.Phase, size = $"{m.TeamSize}v{m.TeamSize}", seats = $"{m.Side0} v {m.Side1}" })
            .ToList<object>() ?? []);
    }

    // CLASSIC: idempotently replace, never append, the ticket vendors' stock from the dated arena configuration.
    private static void RefreshTicketVendorInventories() {
        foreach (var vendor in s_config!.TicketVendors) {
            Imlight.CoreLib.WizardData.SpiralDB.RegisterNpcInventory(InventoryOf(vendor));
        }

        if (s_config.TicketVendors.Length > 0) {
            Logger.Information("Classic arena: {0} Arena Ticket vendors, {1} items.",
                Logger.Args(s_config.TicketVendors.Length, s_config.TicketVendors.Sum(v => v.Items.Length)));
        }
    }

    private static NPCInventory InventoryOf(ArenaVendor vendor) => new() {
        TemplateID = vendor.Npc,
        Inventory = [.. vendor.Items.Select(item => new Imcodec.Types.GID(item.Template))],
    };

    /// <summary>CLASSIC: a ticket shop's stock directly from the active arena data, also if SpiralDB was reloaded.</summary>
    internal static NPCInventory? TicketInventory(uint npcTemplate)
        => TicketVendor(npcTemplate) is { } vendor ? InventoryOf(vendor) : null;

    /// <summary>For tests: use this data.</summary>
    internal static void UseForTests(ArenaConfig? config) => s_config = config;

    /// <summary>The matchmaker, started on first use while the arena is on.</summary>
    internal static ArenaMatchmaker? Matchmaker(ActorSystem system) {
        lock (s_clockGate) {
            if (s_stopping || !Enabled) {
                return null;
            }

            if (ArenaMatchmaker.Instance is { } running) {
                return running;
            }

            var made = ArenaMatchmaker.Start(s_config!, new ServerArenaWorld(system));
            StartClock(made);
            return made;
        }
    }

    private static readonly object s_clockGate = new();
    private static ArenaClock? s_clock;
    private static Task? s_clockStopTask;
    private static bool s_stopping;

    private static void StartClock(ArenaMatchmaker matchmaker) {
        if (s_clock is not null) {
            return;
        }

        s_clock = new ArenaClock(() => {
            try {
                matchmaker.Tick(DateTime.UtcNow);
            }
            catch (Exception ex) {
                Logger.Error("Arena clock: {0}", Logger.Args(ex.Message));
            }
        }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>CLASSIC: stop new arena use and await every callback before the matchmaker/database drain.</summary>
    internal static Task StopClockAsync() {
        lock (s_clockGate) {
            s_stopping = true;
            return s_clockStopTask ??= s_clock?.StopAsync() ?? Task.CompletedTask;
        }
    }

    /// <summary>CLASSIC: cancel interrupted matches and finish accepted results while Raven is still available.</summary>
    internal static async Task QuiesceAsync() {
        await StopClockAsync().ConfigureAwait(false);
        if (ArenaMatchmaker.Instance is { } matchmaker) {
            await matchmaker.QuiesceAsync().ConfigureAwait(false);
        }
        // CLASSIC: Finish's Tell is not a saved human award. Keep their services alive until each save replies.
        await ServerArenaWorld.DrainOutcomesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="zone"/> is the arena hall (the guards' zone): one shared zone, never a per-player copy,
    /// so wizards meet at the guards (the 2014 Wizard City Arena's hard limit, 12, would make it an instance).
    /// </summary>
    public static bool IsHall(string? zone)
        => Enabled && zone is not null && string.Equals(zone, s_config!.HallZone, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tag of the duel circle the server places in each arena.</summary>
    public const string CircleTag = "Arena Circle";

    /// <summary>The arena circle to place in <paramref name="zone"/> (none when it is not an arena or the arena is off).</summary>
    public static System.Collections.Generic.IEnumerable<Imcodec.ObjectProperty.TypeCache.CombatSigilObjectInfo> CircleInfosFor(string? zone) {
        if (!Enabled || zone is null) {
            yield break;
        }

        foreach (var arena in s_config!.Arenas.Where(a => string.Equals(a.Zone, zone, StringComparison.OrdinalIgnoreCase))) {
            yield return new Imcodec.ObjectProperty.TypeCache.CombatSigilObjectInfo {
                m_templateID = s_config.CircleTemplate,
                m_location = new Imcodec.Math.Vector3(arena.X, arena.Y, arena.Z),
                m_orientation = new Imcodec.Math.Vector3(0, 0, arena.Yaw),
                m_fScale = 0,
                m_zoneTag = CircleTag,
                m_zoneTag2 = CircleTag,
                m_sigilType = s_config.CircleSigilType,
                m_radius = s_config.CircleRadius,
                m_firstTeamToAct = -1,
                m_loadingType = Imcodec.ObjectProperty.TypeCache.LoadingType.DYNAMIC_SERVER,
                m_activateEvents = ["StartZone"],
            };
        }
    }

    /// <summary>True when <paramref name="zone"/> is one of the arenas matches go to.</summary>
    public static bool IsArenaZone(string? zone)
        => Enabled && zone is not null && s_config!.Arenas.Any(a => string.Equals(a.Zone, zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the circle tagged <paramref name="tag"/> in <paramref name="zone"/> is an arena's match circle.</summary>
    public static bool IsArenaCircle(string? zone, string? tag)
        => Enabled && zone is not null && string.Equals(tag, CircleTag, StringComparison.Ordinal)
           && s_config!.Arenas.Any(a => string.Equals(a.Zone, zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Arena Ticket vendor whose template is <paramref name="npcTemplate"/>, or null (also when the arena is off).</summary>
    public static ArenaVendor? TicketVendor(uint npcTemplate)
        => Enabled ? s_config!.TicketVendors.FirstOrDefault(v => v.Npc == npcTemplate) : null;

    /// <summary>A ticket vendor's item, or null.</summary>
    public static ArenaVendorItem? TicketItem(uint npcTemplate, uint itemTemplate)
        => TicketVendor(npcTemplate)?.Items.FirstOrDefault(i => i.Template == itemTemplate);

    /// <summary>The guard kind of a template, or null when it is not a guard.</summary>
    public static ArenaKind? KioskKind(uint templateId)
        => s_config is null ? null
            : templateId == s_config.PracticeKiosk ? ArenaKind.Practice
            : templateId == s_config.RankedKiosk ? ArenaKind.Ranked : null;

}

/// <summary>CLASSIC: Timer.DisposeAsync is the barrier for callbacks already running on the thread pool.</summary>
internal sealed class ArenaClock(Action tick, TimeSpan dueTime, TimeSpan period) {
    private readonly object _gate = new();
    private readonly Timer _timer = new(_ => tick(), null, dueTime, period);
    private Task? _stopped;

    internal Task StopAsync() {
        lock (_gate) {
            return _stopped ??= _timer.DisposeAsync().AsTask();
        }
    }
}

/// <summary>CLASSIC: in-process receipts for human outcomes; no retry can duplicate an uncertain save.</summary>
internal sealed class ArenaOutcomeReceipts {
    private readonly object _gate = new();
    private readonly HashSet<ArenaOutcomeReceipt> _pending = [];
    private readonly List<Task> _failures = [];

    internal ArenaOutcomeReceipt Begin(ulong characterId) {
        var receipt = new ArenaOutcomeReceipt(this, characterId);
        lock (_gate) { _pending.Add(receipt); }
        return receipt;
    }

    internal void Complete(ArenaOutcomeReceipt receipt, Exception? error) {
        lock (_gate) {
            _pending.Remove(receipt);
            if (error is not null) _failures.Add(receipt.Completion);
        }
    }

    internal Task DrainAsync() {
        lock (_gate) {
            return Task.WhenAll(_pending.Select(receipt => receipt.Completion)
                .Concat(_failures).ToArray());
        }
    }
}

internal sealed class ArenaOutcomeReceipt(ArenaOutcomeReceipts owner, ulong characterId) {
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _claimed;
    internal ulong CharacterId { get; } = characterId;
    internal Task Completion => _completion.Task;

    internal bool ApplyAward(Func<bool> save) {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return false;
        try {
            if (!save()) throw new InvalidOperationException("The arena ticket outcome was not persisted.");
            owner.Complete(this, null);
            _completion.TrySetResult();
            return true;
        }
        catch (Exception ex) {
            owner.Complete(this, ex);
            _completion.TrySetException(ex);
            throw;
        }
    }

    internal bool FailUnstarted(Exception error) {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return false;
        owner.Complete(this, error);
        _completion.TrySetException(error);
        return true;
    }
}

/// <summary>CLASSIC: watch the same incarnation that receives the award; recover only work it never claimed.</summary>
internal static class ArenaOutcomeDelivery {
    internal static async Task DeliverAsync(IActorRef target, ArenaOutcomeReceipt receipt, ArenaOutcome outcome,
        Func<bool> recover) {
        using var watchCancellation = new CancellationTokenSource();
        try {
            var terminated = target.WatchAsync(watchCancellation.Token);
            target.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME { Outcome = outcome, Receipt = receipt }, ActorRefs.Nobody);
            var finished = await Task.WhenAny(receipt.Completion, terminated).ConfigureAwait(false);
            if (finished == terminated) {
                if (!await terminated.ConfigureAwait(false))
                    throw new InvalidOperationException("The arena outcome target termination was not confirmed.");
                // CLASSIC: Terminated follows all child handlers. A claimed/uncertain save must never be retried.
                receipt.ApplyAward(recover);
            }
            await receipt.Completion.ConfigureAwait(false);
        }
        catch (Exception ex) {
            receipt.FailUnstarted(ex); // CLASSIC: a failed watch/dispatch cannot silently leave an unclaimed receipt.
            throw;
        }
        finally { watchCancellation.Cancel(); }
    }
}

/// <summary>The live server behind the matchmaker.</summary>
internal sealed class ServerArenaWorld(ActorSystem system) : IArenaWorld, IArenaAmbientWorld, IArenaFriendlyWorld {

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Wizard> s_live = new();
    private static readonly ArenaOutcomeReceipts s_outcomes = new();

    internal static Task DrainOutcomesAsync() => s_outcomes.DrainAsync();

    /// <summary>ArenaService keeps the live wizard of each player who used the arena.</summary>
    public static void Register(Wizard wizard) => s_live[wizard.CharId] = wizard;

    public static void Forget(ulong charId) => s_live.TryRemove(charId, out _);

    public IArenaLadderStore Ladder { get; } = new ArenaLadderCollection.Raven();
    public bool AmbientEnabled => ArenaAmbientParticipants.Enabled;
    public bool FriendlyEnabled => AmbientEnabled;
    public ArenaPlayer? PreviewFriendly(int level, int school, ArenaPvpSkill skill) => ArenaAmbientParticipants.Preview(level, school, skill);
    public ArenaPlayer? ReserveFriendly(int level, int school, ArenaPvpSkill skill) => ArenaAmbientParticipants.Reserve(system, level, school, skill);
    public bool IsAmbient(ulong charId) => ArenaAmbientParticipants.IsIdentity(charId);
    public ArenaPlayer? ReserveAmbient(int level, int preferredSchool) => ArenaAmbientParticipants.Reserve(system, level, preferredSchool);
    public void ReleaseAmbient(ulong charId) => ArenaAmbientParticipants.Release(charId);

    public ArenaPlayer? Player(ulong charId) {
        if (IsAmbient(charId)) return ArenaAmbientParticipants.Wizard(charId) is { } ambient
            ? PlayerOf(ambient.Wizard) with { Ambient = true } : null;
        if (OnlinePlayerCollection.GetOnlinePlayer(charId) is null || !s_live.TryGetValue(charId, out var wizard)) {
            return null;
        }

        return PlayerOf(wizard);
    }

    /// <summary>The arena's view of a wizard.</summary>
    public static ArenaPlayer PlayerOf(Wizard wizard) {
        var name = wizard.PlayerNameBehavior;
        var school = wizard.MagicSchoolBehavior?.MagicSchool.ToString() ?? "";

        // The client's own CharacterID (PvPClientManager reads it from the same field as MSG_GETLADDER's) is the character
        // id of the character list, not the game object id.
        return new ArenaPlayer(wizard.CharId, wizard.CharId,
            DataManipulation.SpacedHexStringToBytes(name.GetWizardNameAsByteHexString()), name.GetWizardName(),
            wizard.MagicSchoolBehavior?.Level ?? 1, school, (short) (name.Gender == Imcodec.ObjectProperty.TypeCache.eGender.Female ? 1 : 0));
    }

    public bool AreFriends(ulong charId, ulong otherCharId)
        => BuddyRelationshipCollection.GetRelationshipsForPlayer(charId).Any(r =>
            (r.FirstPlayerId == otherCharId || r.SecondPlayerId == otherCharId) && !r.Blocked && !r.IsBrokenUp);

    public void Send(ulong charId, IMessage message) => Tell(charId, message);

    // CLASSIC (owner, 2026-10-08): non-modal server messages are "!" alerts in r806919; only failures reach the player.
    public void Inform(ulong charId, string text) => Logger.Information("Arena notice to {0} (log only): {1}", Logger.Args(charId, text));

    public void InformFailure(ulong charId, string text) => Tell(charId, ClassicChat.Line(text));

    public void Travel(ulong charId, string zone, string location, ulong runId) {
        if (IsAmbient(charId)) ArenaAmbientParticipants.Travel(charId, zone, location, runId);
        else Tell(charId, new CLASSIC_FEATURES_PROTOCOL.MSG_ARENATRAVEL { Zone = zone, Location = location, RunId = runId });
    }

    public void Deliver(ulong charId, ArenaOutcome outcome) {
        if (IsAmbient(charId)) return; // CLASSIC: their ladder was saved; never write NPC tickets into player documents.
        if (OutcomeTarget(OnlinePlayerCollection.GetOnlinePlayer(charId), charId) is { } target) {
            var receipt = s_outcomes.Begin(charId);
            _ = ObserveDeliveryAsync(ArenaOutcomeDelivery.DeliverAsync(target, receipt, outcome,
                () => SaveOfflineOutcome(charId, outcome.Tickets)), charId);
            return;
        }

        // CLASSIC: an offline or stale session snapshot cannot authorize delivery to a different incarnation.
        if (!SaveOfflineOutcome(charId, outcome.Tickets))
            throw new InvalidOperationException("The offline arena ticket outcome was not persisted.");
    }

    internal static IActorRef? OutcomeTarget(Imlight.CoreLib.WizardData.Models.Misc.OnlinePlayer? online, ulong charId) {
        if (online is null || online.CharacterId != charId || string.IsNullOrEmpty(online.ActorPath)
            || AccountSessions.HolderOf(online.AccountId) is not SessionActor session) return null;
        var target = session.ActorRef;
        if (target is null || target.IsNobody() || target is IInternalActorRef { IsTerminated: true }
            || !string.Equals(target.Path.ToString(), online.ActorPath, StringComparison.Ordinal)) return null;
        if (!ActiveWizardDirectory.TryGet(target, out var wizard, out _) || wizard.CharId != charId
            || wizard.Account?.AccountId != online.AccountId) return null;
        return target;
    }

    internal static bool SaveOfflineOutcome(ulong charId, int tickets)
        => tickets >= 0 && (tickets == 0 || WizardCollection.ChangeArenaTickets(charId, tickets));

    private static async Task ObserveDeliveryAsync(Task delivery, ulong charId) {
        try { await delivery.ConfigureAwait(false); }
        catch (Exception ex) {
            Logger.Error("Arena: outcome delivery/recovery for {0} failed; no uncertain award was retried: {1}",
                Logger.Args(charId, ex));
        }
    }

    public ulong NewRunId() => GroupInstances.NewRunId(DateTime.UtcNow);

    public string? ZoneOf(ulong charId) => IsAmbient(charId) ? ArenaAmbientParticipants.Wizard(charId)?.Zone
        : OnlinePlayerCollection.GetOnlinePlayer(charId)?.CurrentZone;

    private void Tell(ulong charId, object message) {
        if (OnlinePlayerCollection.GetOnlinePlayer(charId)?.ActorPath is { Length: > 0 } path) {
            // No sender: a session hands a server message from one of its own services to the OTHER services only, and
            // the matchmaker often runs on the very wizard's ArenaService (their Go to Arena started the trip).
            system.ActorSelection(path).Tell(message, ActorRefs.Nobody);
        }
    }

}
