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
 * CLASSIC HOLIDAY EVENTS (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * Runs the 2009 holiday events of classic-data/holidays on the real date
 * ([Classic] HolidayEvents, HolidayDateOverride): sets each running event's
 * global registry entry to 1 (the client's zone data then shows its
 * decorations, vendors and tower sigils to wizards entering the zone),
 * stocks the holiday vendors, keeps the event's zones closed out of
 * season, tells shops which holiday items to show, and adds boss drops.
 *
 * NOTE:
 * Checked every minute. A zone's objects follow the registry when a wizard
 * enters the zone, so an event that starts while a wizard stands in the
 * Commons shows on their next zone change. Dates use the server's local
 * time zone.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Imcodec.Types;
using Imlight.Classic;
using Imlight.Classic.Holidays;
using Imlight.Common;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The holiday events running now.
/// </summary>
public static class ClassicHolidays {

    private static HolidayCalendar? s_calendar;
    private static Timer? s_timer;
    private static IReadOnlyList<HolidayEvent> s_active = [];
    private static readonly Dictionary<ulong, HolidayItem> s_itemPrices = [];

    /// <summary>The loaded calendar, or null without one.</summary>
    public static HolidayCalendar? Calendar => s_calendar;

    /// <summary>The events running now.</summary>
    public static IReadOnlyList<HolidayEvent> Active => s_active;

    /// <summary>The registry entries of the events running now (the shop window's active holiday list).</summary>
    public static List<string> ActiveRegistries => [.. s_active.Select(holiday => holiday.Registry)];

    /// <summary>Loads the calendar from <paramref name="classicDataRoot"/>/holidays and starts the clock.</summary>
    public static void Initialize(string? classicDataRoot, string profileId) {
        if (s_calendar is not null) {
            return; // every realm's game server loads SpiralDB; once is enough
        }

        if (classicDataRoot is null || !Directory.Exists(Path.Combine(classicDataRoot, "holidays"))) {
            Logger.Information("Classic holidays: no classic-data/holidays; no holiday events.");

            return;
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(classicDataRoot, "holidays"), "holidays-*.yaml").Order()) {
            try {
                var calendar = HolidayCalendarLoader.Load(path);
                if (!calendar.Profiles.Contains(profileId, StringComparer.Ordinal)) {
                    continue;
                }

                s_calendar = calendar;
                break;
            }
            catch (ClassicDataException ex) {
                Logger.Error("Classic holidays: {Path} is invalid, so there are no holiday events: {Error}",
                    Logger.Args(path, ex.Message));

                return;
            }
        }

        if (s_calendar is null) {
            Logger.Information("Classic holidays: no holiday calendar for profile {Profile}.", Logger.Args(profileId));

            return;
        }

        RegisterVendors(s_calendar);
        Refresh();
        s_timer = new Timer(_ => Refresh(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        AdminDashboard.AddSection("Holiday events", DashboardRows);
        Logger.Information("Classic holidays: {Count} events from {File}; running now: {Active}.",
            Logger.Args(s_calendar.Events.Length, s_calendar.SourceFile,
                s_active.Count == 0 ? "none" : string.Join(", ", s_active.Select(holiday => holiday.Name))));
    }

    /// <summary>The date the events follow: today, or [Classic] HolidayDateOverride.</summary>
    public static DateOnly Today {
        get {
            var forced = ClassicSettings.HolidayDateOverride;
            if (forced > 0 && DateOnly.TryParseExact(forced.ToString(CultureInfo.InvariantCulture), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) {
                return date;
            }

            return DateOnly.FromDateTime(DateTime.Now);
        }
    }

    /// <summary>Re-reads the date and switches events on and off.</summary>
    public static void Refresh() {
        if (s_calendar is not { } calendar) {
            return;
        }

        try {
            var active = ClassicSettings.HolidayEvents ? calendar.ActiveOn(Today).ToList() : [];
            var before = s_active.Select(holiday => holiday.Id).ToHashSet();
            s_active = active;
            var values = calendar.Registries.ToDictionary(name => name, name => active.Any(holiday => holiday.Registry == name) ? 1f : 0f);
            SpiralDB.SetGlobalRegistryValues(values);

            foreach (var started in active.Where(holiday => !before.Contains(holiday.Id))) {
                Logger.Information("Classic holidays: {Event} has begun.", Logger.Args(started.Name));
            }

            foreach (var ended in before.Where(id => active.All(holiday => holiday.Id != id))) {
                Logger.Information("Classic holidays: {Event} is over.", Logger.Args(ended));
            }
        }
        catch (Exception ex) {
            Logger.Error("Classic holidays: refresh failed: {Error}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// True when <paramref name="zone"/> belongs to a holiday event that is not running now (it stays closed).
    /// </summary>
    public static bool IsZoneOutOfSeason(string zone, out string eventName) {
        eventName = "";
        if (s_calendar?.EventForZone(zone) is not { } holiday) {
            return false;
        }

        eventName = holiday.Name;

        return s_active.All(active => active.Id != holiday.Id);
    }

    /// <summary>The holiday price of an item a holiday vendor sells, if any.</summary>
    public static HolidayItem? PriceOf(ulong templateId) {
        lock (s_itemPrices) {
            return s_itemPrices.GetValueOrDefault(templateId);
        }
    }

    /// <summary>The extra drops of <paramref name="mobTemplateId"/> while its event runs.</summary>
    public static HolidayDrop? DropsFor(ulong mobTemplateId)
        => s_active.SelectMany(holiday => holiday.Drops).FirstOrDefault(drop => drop.Template == mobTemplateId);

    private static void RegisterVendors(HolidayCalendar calendar) {
        foreach (var vendor in calendar.Events.SelectMany(holiday => holiday.Vendors)) {
            SpiralDB.RegisterNpcInventory(new NPCInventory {
                TemplateID = vendor.Npc,
                Inventory = [.. vendor.Items.Select(item => new GID(item.Template))],
            });
            lock (s_itemPrices) {
                foreach (var item in vendor.Items) {
                    s_itemPrices[item.Template] = item;
                }
            }
        }

        Logger.Information("Classic holidays: stocked {Count} holiday vendors.",
            Logger.Args(calendar.Events.Sum(holiday => holiday.Vendors.Length)));
    }

    private static object DashboardRows() {
        var today = Today;

        return s_calendar?.Events.Select(holiday => {
            var (from, to) = holiday.NextRun(today);

            return new {
                @event = holiday.Name,
                running = s_active.Any(active => active.Id == holiday.Id) ? "yes" : "",
                dates = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}",
                vendors = string.Join(", ", holiday.Vendors.Select(vendor => vendor.Name)),
                items = holiday.Vendors.Sum(vendor => vendor.Items.Length),
                zones = string.Join(", ", holiday.Zones),
                confidence = holiday.Confidence,
            };
        }).ToList() ?? (object) "no holiday calendar";
    }

}
