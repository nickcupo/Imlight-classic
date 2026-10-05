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
 * SERVER ADMINISTRATION
 * ========================================================================
 *
 * PURPOSE:
 * What the owner does to a running server, from the .server command or the
 * admin dashboard: broadcast a message to every wizard, schedule a safe
 * restart or backup (warnings, then a wait for fights to end, capped), list
 * who is online and where, and list the backups.
 *
 * NOTE:
 * A restart closes every session (so locations are saved) and exits with
 * code 75; the systemd unit restarts the server on it. A backup writes
 * <ControlDirectory>/backup-request, which w101c-backup.path turns into a
 * run of w101c-backup.service (it stops and starts the server itself);
 * the server never runs privileged commands. Without systemd (the Mac
 * rigs) a restart just exits and a backup request waits for a host.
 * The nightly backup (backup.sh --scheduled, as root) writes
 * <ControlDirectory>/backup-schedule when players are connected; the
 * server takes it (deletes it, which tells the script it was seen) and
 * schedules a safe backup with the warning minutes the file holds.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Admin;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic.Admin;

/// <summary>An online wizard, as the dashboard lists them.</summary>
public sealed record OnlineWizard(string Account, string Wizard, ulong CharacterId, string Zone, string ZoneName, bool InDuel);

/// <summary>A backup archive.</summary>
public sealed record BackupFile(string Name, long Bytes, DateTime WrittenUtc);

/// <summary>The scheduled restart or backup, if any.</summary>
public sealed record RestartStatus(RestartKind Kind, DateTime StartedUtc, DateTime DeadlineUtc, DateTime HardCapUtc,
    string? Reason, string RequestedBy, string State);

/// <summary>
/// Broadcasts, safe restarts and backups, and the server's live state for the admin dashboard.
/// </summary>
public static class ServerAdmin {

    /// <summary>The exit code of a safe restart; the systemd unit restarts the server on it.</summary>
    public const int RestartExitCode = 75;

    private static readonly object s_lock = new();
    private static readonly ConcurrentDictionary<ulong, (string Account, string Wizard)> s_names = new();
    private static ActorSystem? s_system;
    private static Timer? s_timer;
    private static RestartPlan? s_plan;
    private static string s_requestedBy = "";
    private static string s_state = "";
    private static DateTime s_nextSchedulePoll;

    /// <summary>The file the nightly backup writes to ask for a safe backup.</summary>
    public const string ScheduleRequestFile = "backup-schedule";

    /// <summary>When this server process started.</summary>
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>True once a restart's countdown is over: no new fight starts.</summary>
    public static bool BlockNewDuels { get; private set; }

    /// <summary>For tests and runs without the dashboard: overrides how the process ends.</summary>
    internal static Action<int> Exit { get; set; } = Environment.Exit;

    public static string BackupDirectory
        => Setting("Classic.BackupDirectory") ?? "/var/lib/w101c/backups";

    public static string ControlDirectory
        => ControlDirectoryOverride ?? Setting("Classic.ControlDirectory") ?? "/var/lib/w101c/control";

    /// <summary>For tests: where backup requests go instead of the configured directory.</summary>
    internal static string? ControlDirectoryOverride { get; set; }

    /// <summary>Called once the actor system exists.</summary>
    public static void Initialize(ActorSystem system) {
        s_system = system;
        s_timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Sends <paramref name="text"/> to every wizard in the world. Returns how many it went to.</summary>
    public static int Broadcast(string text, bool modal = false) {
        if (s_system is null || string.IsNullOrWhiteSpace(text)) {
            return 0;
        }

        var sent = 0;
        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers()) {
            if (string.IsNullOrEmpty(player.ActorPath)) {
                continue;
            }

            s_system.ActorSelection(player.ActorPath).Tell(ClassicChat.Notice(text, modal)); // chat line or popup
            sent++;
        }

        Logger.Information("[ADMIN] Broadcast to {Count} wizard(s): {Text}", Logger.Args(sent, text));

        return sent;
    }

    /// <summary>Schedules a safe restart or backup. Replaces one already scheduled.</summary>
    public static RestartStatus Schedule(RestartKind kind, TimeSpan warning, string? reason, string requestedBy) {
        lock (s_lock) {
            var maxWait = TimeSpan.FromMinutes(ClassicSettings.RestartMaxWaitMinutes);
            s_plan = new RestartPlan(DateTime.UtcNow, warning, maxWait, kind, reason);
            s_requestedBy = requestedBy;
            s_state = "counting down";
            BlockNewDuels = false;
            Logger.Information("[ADMIN] {By} scheduled a {Kind} in {Warning} (waits for fights at most {MaxWait} more).",
                Logger.Args(requestedBy, kind, warning, maxWait));
        }

        Tick();

        return Status()!;
    }

    /// <summary>Cancels a scheduled restart or backup. False when none was scheduled or it already started.</summary>
    public static bool Cancel(string requestedBy) {
        lock (s_lock) {
            if (s_plan is null || s_state == "restarting") {
                return false;
            }

            var kind = s_plan.Kind;
            s_plan = null;
            BlockNewDuels = false;
            s_state = "";
            Logger.Information("[ADMIN] {By} cancelled the {Kind}.", Logger.Args(requestedBy, kind));
        }

        Broadcast("The scheduled server restart is cancelled.");

        return true;
    }

    public static RestartStatus? Status() {
        lock (s_lock) {
            return s_plan is null
                ? null
                : new RestartStatus(s_plan.Kind, s_plan.StartedUtc, s_plan.DeadlineUtc, s_plan.HardCapUtc, s_plan.Reason,
                    s_requestedBy, s_state);
        }
    }

    /// <summary>Who is online and where.</summary>
    public static IReadOnlyList<OnlineWizard> OnlineWizards() {
        var inDuel = ActiveDuels.Snapshot().SelectMany(duel => duel.CharacterIds).ToHashSet();
        var list = new List<OnlineWizard>();
        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers().Where(player => player.CharacterId != 0)) {
            var (account, wizard) = s_names.GetOrAdd(player.CharacterId, id => LookUpNames(id, player.AccountId));
            list.Add(new OnlineWizard(account, wizard, player.CharacterId, player.CurrentZone ?? "",
                player.CurrentZoneDisplayName ?? "", inDuel.Contains(player.CharacterId)));
        }

        return [.. list.OrderBy(entry => entry.Wizard, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The backup archives, newest first.</summary>
    public static IReadOnlyList<BackupFile> Backups() {
        try {
            var directory = new DirectoryInfo(BackupDirectory);
            if (!directory.Exists) {
                return [];
            }

            return [.. directory.EnumerateFiles("w101c-*.tar.gz")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new BackupFile(file.Name, file.Length, file.LastWriteTimeUtc))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("[ADMIN] Could not list backups in {Dir}: {Error}", Logger.Args(BackupDirectory, ex.Message));

            return [];
        }
    }

    /// <summary>
    /// Takes the nightly backup's request from <paramref name="controlDirectory"/>, if there is one: deletes it and
    /// returns the warning it asks for (the minutes in the file, 0-60; 5 when empty or unreadable).
    /// </summary>
    internal static bool TakeScheduleRequest(string controlDirectory, out TimeSpan warning) {
        warning = TimeSpan.FromMinutes(5);
        var path = Path.Combine(controlDirectory, ScheduleRequestFile);
        if (!File.Exists(path)) {
            return false;
        }

        try {
            var text = File.ReadAllText(path).Trim();
            File.Delete(path);
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                    out var minutes) && minutes <= 60) {
                warning = TimeSpan.FromMinutes(minutes);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("[ADMIN] Could not take the nightly backup request {Path}: {Error}", Logger.Args(path, ex.Message));

            return false;
        }
    }

    private static void PollScheduleRequest() {
        var now = DateTime.UtcNow;
        if (now < s_nextSchedulePoll) {
            return;
        }

        s_nextSchedulePoll = now.AddSeconds(5);
        if (!TakeScheduleRequest(ControlDirectory, out var warning)) {
            return;
        }

        if (Status() is { } existing && IsPending(existing.State)) { // CLASSIC: a finished or failed one may be replaced
            Logger.Information("[ADMIN] The nightly backup asked for a backup, but a {Kind} is already scheduled; "
                               + "the nightly timer tries again later.", Logger.Args(existing.Kind));

            return;
        }

        if (OnlinePlayerCollection.GetOnlinePlayers().All(player => player.CharacterId == 0)) {
            warning = TimeSpan.Zero; // no wizard in the world: nothing to warn
        }

        Schedule(RestartKind.Backup, warning, "nightly backup", "nightly backup timer");
    }

    // CLASSIC: how many times a restart or backup was carried out (tests).
    internal static int Executions { get; private set; }

    // CLASSIC: a plan still counting down or waiting for fights; anything else (restarting, backup requested, backup
    // failed) is carried out already and must not run again.
    private static bool IsPending(string state)
        => state is "counting down" or "waiting for fights to end";

    internal static void Tick() {
        try {
            PollScheduleRequest();
        }
        catch (Exception ex) {
            Logger.Error("[ADMIN] Nightly backup request failed: {Error}", Logger.Args(ex));
        }

        RestartPlan? plan;
        lock (s_lock) {
            plan = s_plan;

            // CLASSIC: a backup leaves its plan in place (the dashboard shows "backup requested" or the failure); without
            // this the 1 s timer carried it out again every ~7 s, kicking everyone each time, for as long as the process
            // lived (forever when the backup request could not be written, or with no backup unit, as on the Mac rigs).
            if (plan is not null && !IsPending(s_state)) {
                return;
            }
        }

        if (plan is null) {
            return;
        }

        try {
            var now = DateTime.UtcNow;
            foreach (var warning in plan.DueWarnings(now)) {
                Broadcast(warning);
            }

            var decision = plan.Decide(now, ActiveDuels.WizardsInDuels);
            if (decision == RestartDecision.Countdown) {
                return;
            }

            BlockNewDuels = true;
            if (decision == RestartDecision.WaitForFights) {
                lock (s_lock) {
                    s_state = "waiting for fights to end";
                }

                if (plan.WaitMessage() is { } wait) {
                    Broadcast(wait);
                }

                return;
            }

            lock (s_lock) {
                if (!ReferenceEquals(s_plan, plan) || !IsPending(s_state)) {
                    return;
                }

                s_state = "restarting";
            }

            Execute(plan);
        }
        catch (Exception ex) {
            Logger.Error("[ADMIN] Restart tick failed: {Error}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// CLASSIC: the process is told to stop (SIGTERM from systemctl stop/restart, as every deploy does; SIGINT): tell
    /// every wizard, close the sessions (which saves their places and leaves trades and duels the way a logout does)
    /// and shut the database down, as a safe restart's last step. Without it the process just died: wizards were
    /// dropped mid-sentence, logged back in where they last changed zones, and RavenDB was not shut down. Returns
    /// false when a safe restart is already carrying itself out.
    /// </summary>
    public static bool StopForSignal(string signal) {
        lock (s_lock) {
            if (s_state == "restarting") {
                return false;
            }

            s_state = "restarting";
        }

        BlockNewDuels = true;
        Logger.Information("[ADMIN] {Signal}: closing every session before the process exits.", Logger.Args(signal));
        var told = Broadcast("The server is restarting now. Please log back in in a minute.", modal: true);
        if (told > 0) {
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }

        var closed = CloseSessions();
        Logger.Information("[ADMIN] Closed {Count} session(s) for {Signal}.", Logger.Args(closed, signal));
        if (closed > 0) {
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }

        WizardData.Implementations.EmbeddedDatabaseManager.Shutdown(TimeSpan.FromSeconds(30));
        Serilog.Log.CloseAndFlush();

        return true;
    }

    private static void Execute(RestartPlan plan) {
        Executions++;
        Broadcast(plan.Kind == RestartKind.Backup
            ? "The server is going down for a backup now. Please log back in in a minute or two."
            : "The server is restarting now. Please log back in in a minute.", modal: true);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var closed = CloseSessions();
        Logger.Information("[ADMIN] Closed {Count} session(s) for the {Kind}.", Logger.Args(closed, plan.Kind));
        Thread.Sleep(TimeSpan.FromSeconds(5));

        if (plan.Kind == RestartKind.Backup) {
            if (!RequestBackup(out var error)) {
                Logger.Error("[ADMIN] The backup request could not be written: {Error}", Logger.Args(error));
                lock (s_lock) {
                    s_state = "backup request failed: " + error;
                    BlockNewDuels = false;
                }

                return;
            }

            lock (s_lock) {
                s_state = "backup requested; w101c-backup.path stops and restarts the server";
            }

            return;
        }

        WizardData.Implementations.EmbeddedDatabaseManager.Shutdown(TimeSpan.FromSeconds(30));
        Logger.Information("[ADMIN] Exiting with code {Code} for a safe restart.", Logger.Args(RestartExitCode));
        Serilog.Log.CloseAndFlush();
        Exit(RestartExitCode);
    }

    /// <summary>Writes the backup request the w101c-backup.path unit watches for.</summary>
    public static bool RequestBackup(out string error) {
        try {
            Directory.CreateDirectory(ControlDirectory);
            var path = Path.Combine(ControlDirectory, "backup-request");
            File.WriteAllText(path, DateTime.UtcNow.ToString("O") + "\n");
            error = "";
            Logger.Information("[ADMIN] Backup requested ({Path}).", Logger.Args(path));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            error = ex.Message;

            return false;
        }
    }

    private static int CloseSessions() {
        if (s_system is null) {
            return 0;
        }

        var closed = 0;
        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers()) {
            if (string.IsNullOrEmpty(player.ActorPath)) {
                continue;
            }

            s_system.ActorSelection(player.ActorPath).Tell("Close");
            closed++;
        }

        return closed;
    }

    private static (string Account, string Wizard) LookUpNames(ulong characterId, ulong accountId) {
        try {
            var account = AccountCollection.GetAccount(accountId)?.Username ?? $"account {accountId}";
            var wizard = WizardCollection.GetCharacterUnloaded(characterId)?.PlayerNameBehavior?.GetWizardName()
                ?? $"wizard {characterId}";

            return (account, wizard);
        }
        catch (Exception) {
            return ($"account {accountId}", $"wizard {characterId}");
        }
    }

    private static string? Setting(string key) {
        try {
            return ConfigurationManager.GetSetting(key) is { Length: > 0 } text ? text : null;
        }
        catch (Exception) {
            return null;
        }
    }

}
