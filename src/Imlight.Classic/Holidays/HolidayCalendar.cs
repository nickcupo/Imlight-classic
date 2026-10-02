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
 * CLASSIC HOLIDAY CALENDAR
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 holiday events (classic-data/holidays/holidays-*.yaml) on the
 * real calendar: which events run on a date, and for each its client
 * registry entry, vendors and their stock, zones and boss drops.
 *
 * USAGE EXAMPLE:
 * var calendar = HolidayCalendarLoader.Load(path);
 * foreach (var holiday in calendar.ActiveOn(DateOnly.FromDateTime(DateTime.Now))) { ... }
 *
 * NOTE:
 * Month-day windows are inclusive; an end before the start wraps into the
 * next year (Christmas into January). Easter windows count days from
 * Western Easter Sunday (the anonymous Gregorian computus).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Rules;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Holidays;

/// <summary>One item a holiday vendor sells, at its 2009 price.</summary>
public sealed record HolidayItem(string Name, ulong Template, int? Crowns, int? Gold);

/// <summary>A holiday vendor: the NPC the client's zone data places, and what it sells.</summary>
public sealed record HolidayVendor(string Name, ulong Npc, ImmutableArray<HolidayItem> Items);

/// <summary>What one boss drops during its event.</summary>
public sealed record HolidayDrop(string Mob, ulong Template, ImmutableArray<ulong> Items, ImmutableArray<ulong> TreasureCards);

/// <summary>One holiday event.</summary>
public sealed record HolidayEvent(
    string Id,
    string Name,
    string Registry,
    (int Month, int Day)? Start,
    (int Month, int Day)? End,
    (int From, int To)? EasterOffsets,
    string Confidence,
    ImmutableArray<string> Zones,
    ImmutableArray<HolidayVendor> Vendors,
    ImmutableArray<HolidayDrop> Drops) {

    /// <summary>True when the event runs on <paramref name="date"/>.</summary>
    public bool IsActiveOn(DateOnly date) {
        if (EasterOffsets is { } offsets) {
            // An Easter window may reach into the next or previous year's Easter only in theory; check this year's.
            var easter = HolidayCalendar.WesternEaster(date.Year);

            return date >= easter.AddDays(offsets.From) && date <= easter.AddDays(offsets.To);
        }

        if (Start is not { } start || End is not { } end) {
            return false;
        }

        var key = (date.Month * 100) + date.Day;
        var from = (start.Month * 100) + start.Day;
        var to = (end.Month * 100) + end.Day;

        return from <= to ? key >= from && key <= to : key >= from || key <= to;
    }

    /// <summary>The run of the event that includes or follows <paramref name="date"/> (for the dashboard).</summary>
    public (DateOnly From, DateOnly To) NextRun(DateOnly date) {
        if (EasterOffsets is { } offsets) {
            foreach (var year in new[] { date.Year, date.Year + 1 }) {
                var easter = HolidayCalendar.WesternEaster(year);
                var run = (easter.AddDays(offsets.From), easter.AddDays(offsets.To));
                if (run.Item2 >= date) {
                    return run;
                }
            }
        }

        var (start, end) = (Start!.Value, End!.Value);
        foreach (var year in new[] { date.Year - 1, date.Year, date.Year + 1 }) {
            var from = SafeDate(year, start);
            var to = SafeDate((end.Month * 100) + end.Day < (start.Month * 100) + start.Day ? year + 1 : year, end);
            if (to >= date) {
                return (from, to);
            }
        }

        return (date, date);
    }

    private static DateOnly SafeDate(int year, (int Month, int Day) monthDay)
        => new(year, monthDay.Month, Math.Min(monthDay.Day, DateTime.DaysInMonth(year, monthDay.Month)));

}

/// <summary>
/// The holiday events of one classic-data file.
/// </summary>
public sealed class HolidayCalendar {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required ImmutableArray<HolidayEvent> Events { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>The events running on <paramref name="date"/>.</summary>
    public IEnumerable<HolidayEvent> ActiveOn(DateOnly date) => Events.Where(holiday => holiday.IsActiveOn(date));

    /// <summary>Every registry entry the calendar manages.</summary>
    public IEnumerable<string> Registries => Events.Select(holiday => holiday.Registry).Distinct(StringComparer.Ordinal);

    /// <summary>The event whose zones include <paramref name="zone"/>, if any.</summary>
    public HolidayEvent? EventForZone(string zone)
        => Events.FirstOrDefault(holiday => holiday.Zones.Any(prefix => ZoneHasPrefix(zone, prefix)));

    internal static bool ZoneHasPrefix(string zone, string prefix) {
        if (string.IsNullOrEmpty(zone)) {
            return false;
        }

        var zoneParts = zone.Split('/');
        var prefixParts = prefix.Split('/');

        return zoneParts.Length >= prefixParts.Length
            && prefixParts.Select((part, i) => string.Equals(part, zoneParts[i], StringComparison.OrdinalIgnoreCase)).All(x => x);
    }

    /// <summary>Western (Gregorian) Easter Sunday of <paramref name="year"/>, by the anonymous Gregorian algorithm.</summary>
    public static DateOnly WesternEaster(int year) {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(year, month, day);
    }

}

/// <summary>
/// Loads and validates a holiday calendar.
/// </summary>
public static class HolidayCalendarLoader {

    private static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "events");
    private static readonly FrozenSet<string> s_eventKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "name", "registry", "start", "end", "easter", "confidence", "source", "zones", "vendors", "drops", "notes");
    private static readonly FrozenSet<string> s_vendorKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "npc", "items");
    private static readonly FrozenSet<string> s_itemKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "template", "crowns", "gold");
    private static readonly FrozenSet<string> s_dropKeys = FrozenSet.Create(StringComparer.Ordinal, "mob", "template", "items", "source");
    private static readonly FrozenSet<string> s_dropItemKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "template", "kind");
    private static readonly FrozenSet<string> s_easterKeys = FrozenSet.Create(StringComparer.Ordinal, "from", "to");
    private static readonly string[] s_registries =
        ["Halloween", "Christmas", "Easter", "StPatricks", "Vallentines", "Thanksgiving", "WizardDay", "Krampus"];
    private static readonly string[] s_confidence = ["verified", "corroborated", "inferred", "placeholder"];
    private static readonly Regex s_id = new(@"^holidays-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_monthDay = new(@"^(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z", RegexOptions.CultureInvariant);

    /// <summary>Loads the calendar at <paramref name="path"/>.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static HolidayCalendar Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the holiday calendar does not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is null) {
            throw diagnostics.ToException();
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "events"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be holidays-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var events = ImmutableArray.CreateBuilder<HolidayEvent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (map.Find("events") is { } eventsEntry && diagnostics.ReadList(eventsEntry.Value, "events") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("events", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } item) {
                    continue;
                }

                if (ReadEvent(item, keyPath, diagnostics) is { } holiday) {
                    if (!ids.Add(holiday.Id)) {
                        diagnostics.At(item, keyPath, $"event '{holiday.Id}' is listed twice");
                    }

                    events.Add(holiday);
                }
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new HolidayCalendar { Id = id!, Profiles = profiles, Events = events.ToImmutable(), SourceFile = display };
    }

    private static HolidayEvent? ReadEvent(YMap item, string keyPath, YamlDiagnostics diagnostics) {
        diagnostics.CheckKeys(item, keyPath, s_eventKeys, ["id", "name", "registry", "confidence", "source"]);
        string? Str(string key) => item.Find(key) is { } entry ? diagnostics.ReadString(entry.Value, YamlTree.Join(keyPath, key)) : null;
        var id = Str("id");
        var name = Str("name");
        var registry = item.Find("registry") is { } r ? diagnostics.ReadEnum(r.Value, YamlTree.Join(keyPath, "registry"), s_registries) : null;
        var confidence = item.Find("confidence") is { } c ? diagnostics.ReadEnum(c.Value, YamlTree.Join(keyPath, "confidence"), s_confidence) : null;
        if (item.Find("source") is { } s) {
            _ = diagnostics.ReadInt(s.Value, YamlTree.Join(keyPath, "source"), 0);
        }

        (int, int)? MonthDay(string key) {
            if (Str(key) is not { } text) {
                return null;
            }

            if (!s_monthDay.IsMatch(text)) {
                diagnostics.At(item.Find(key)!.Value, YamlTree.Join(keyPath, key), $"'{text}' must be MM-DD");

                return null;
            }

            var (month, day) = (int.Parse(text[..2]), int.Parse(text[3..]));
            if (day > DateTime.DaysInMonth(2008, month)) {
                diagnostics.At(item.Find(key)!.Value, YamlTree.Join(keyPath, key), $"'{text}' is not a real date");

                return null;
            }

            return (month, day);
        }

        var start = MonthDay("start");
        var end = MonthDay("end");
        (int, int)? easter = null;
        if (item.Find("easter") is { } easterEntry && diagnostics.ReadMap(easterEntry.Value, YamlTree.Join(keyPath, "easter")) is { } easterMap) {
            var easterPath = YamlTree.Join(keyPath, "easter");
            diagnostics.CheckKeys(easterMap, easterPath, s_easterKeys, ["from", "to"]);
            var from = easterMap.Find("from") is { } f ? diagnostics.ReadInt(f.Value, YamlTree.Join(easterPath, "from"), -60, 60) : null;
            var to = easterMap.Find("to") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(easterPath, "to"), -60, 60) : null;
            if (from is not null && to is not null) {
                if (from > to) {
                    diagnostics.At(easterMap, easterPath, "from is after to");
                }

                easter = (from.Value, to.Value);
            }
        }

        if ((start is null || end is null) == (easter is null)) {
            diagnostics.At(item, keyPath, "needs either start and end, or easter");
        }

        var zones = ImmutableArray.CreateBuilder<string>();
        if (item.Find("zones") is { } zonesEntry && diagnostics.ReadList(zonesEntry.Value, YamlTree.Join(keyPath, "zones")) is { } zoneList) {
            for (var z = 0; z < zoneList.Items.Length; z++) {
                if (diagnostics.ReadString(zoneList.Items[z], YamlTree.Index(YamlTree.Join(keyPath, "zones"), z)) is { } zone) {
                    zones.Add(zone);
                }
            }
        }

        var vendors = ImmutableArray.CreateBuilder<HolidayVendor>();
        if (item.Find("vendors") is { } vendorsEntry && diagnostics.ReadList(vendorsEntry.Value, YamlTree.Join(keyPath, "vendors")) is { } vendorList) {
            for (var v = 0; v < vendorList.Items.Length; v++) {
                var vendorPath = YamlTree.Index(YamlTree.Join(keyPath, "vendors"), v);
                if (diagnostics.ReadMap(vendorList.Items[v], vendorPath) is not { } vendor) {
                    continue;
                }

                diagnostics.CheckKeys(vendor, vendorPath, s_vendorKeys, ["name", "npc", "items"]);
                var vendorName = vendor.Find("name") is { } vn ? diagnostics.ReadString(vn.Value, YamlTree.Join(vendorPath, "name")) : null;
                var npc = vendor.Find("npc") is { } np ? diagnostics.ReadInt(np.Value, YamlTree.Join(vendorPath, "npc"), 1) : null;
                var items = ImmutableArray.CreateBuilder<HolidayItem>();
                var seen = new HashSet<int>();
                if (vendor.Find("items") is { } itemsEntry && diagnostics.ReadList(itemsEntry.Value, YamlTree.Join(vendorPath, "items")) is { } itemList) {
                    for (var j = 0; j < itemList.Items.Length; j++) {
                        var itemPath = YamlTree.Index(YamlTree.Join(vendorPath, "items"), j);
                        if (diagnostics.ReadMap(itemList.Items[j], itemPath) is not { } entry) {
                            continue;
                        }

                        diagnostics.CheckKeys(entry, itemPath, s_itemKeys, ["name", "template"]);
                        var itemName = entry.Find("name") is { } inm ? diagnostics.ReadString(inm.Value, YamlTree.Join(itemPath, "name")) : null;
                        var template = entry.Find("template") is { } it ? diagnostics.ReadInt(it.Value, YamlTree.Join(itemPath, "template"), 1) : null;
                        var crowns = entry.Find("crowns") is { } cr ? diagnostics.ReadInt(cr.Value, YamlTree.Join(itemPath, "crowns"), 1) : null;
                        var gold = entry.Find("gold") is { } go ? diagnostics.ReadInt(go.Value, YamlTree.Join(itemPath, "gold"), 1) : null;
                        if (crowns is null && gold is null) {
                            diagnostics.At(entry, itemPath, "needs crowns or gold");
                        }

                        if (template is not null && !seen.Add(template.Value)) {
                            diagnostics.At(entry, itemPath, $"template {template} is listed twice");
                        }

                        if (itemName is not null && template is not null) {
                            items.Add(new HolidayItem(itemName, (ulong) template.Value, crowns, gold));
                        }
                    }
                }

                if (vendorName is not null && npc is not null) {
                    vendors.Add(new HolidayVendor(vendorName, (ulong) npc.Value, items.ToImmutable()));
                }
            }
        }

        var drops = ImmutableArray.CreateBuilder<HolidayDrop>();
        if (item.Find("drops") is { } dropsEntry && diagnostics.ReadList(dropsEntry.Value, YamlTree.Join(keyPath, "drops")) is { } dropList) {
            for (var d = 0; d < dropList.Items.Length; d++) {
                var dropPath = YamlTree.Index(YamlTree.Join(keyPath, "drops"), d);
                if (diagnostics.ReadMap(dropList.Items[d], dropPath) is not { } drop) {
                    continue;
                }

                diagnostics.CheckKeys(drop, dropPath, s_dropKeys, ["mob", "template", "items", "source"]);
                var mob = drop.Find("mob") is { } mb ? diagnostics.ReadString(mb.Value, YamlTree.Join(dropPath, "mob")) : null;
                var template = drop.Find("template") is { } dt ? diagnostics.ReadInt(dt.Value, YamlTree.Join(dropPath, "template"), 1) : null;
                if (drop.Find("source") is { } ds) {
                    _ = diagnostics.ReadInt(ds.Value, YamlTree.Join(dropPath, "source"), 0);
                }

                var gear = ImmutableArray.CreateBuilder<ulong>();
                var cards = ImmutableArray.CreateBuilder<ulong>();
                if (drop.Find("items") is { } dropItems && diagnostics.ReadList(dropItems.Value, YamlTree.Join(dropPath, "items")) is { } dropItemList) {
                    for (var j = 0; j < dropItemList.Items.Length; j++) {
                        var itemPath = YamlTree.Index(YamlTree.Join(dropPath, "items"), j);
                        if (diagnostics.ReadMap(dropItemList.Items[j], itemPath) is not { } entry) {
                            continue;
                        }

                        diagnostics.CheckKeys(entry, itemPath, s_dropItemKeys, ["name", "template"]);
                        var itemTemplate = entry.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(itemPath, "template"), 1) : null;
                        var kind = entry.Find("kind") is { } k
                            ? diagnostics.ReadEnum(k.Value, YamlTree.Join(itemPath, "kind"), ["item", "treasure_card"])
                            : "item";
                        if (itemTemplate is not null) {
                            (kind == "treasure_card" ? cards : gear).Add((ulong) itemTemplate.Value);
                        }
                    }
                }

                if (mob is not null && template is not null) {
                    drops.Add(new HolidayDrop(mob, (ulong) template.Value, gear.ToImmutable(), cards.ToImmutable()));
                }
            }
        }

        if (id is null || name is null || registry is null || confidence is null) {
            return null;
        }

        return new HolidayEvent(id, name, registry, start, end, easter, confidence, zones.ToImmutable(), vendors.ToImmutable(),
            drops.ToImmutable());
    }

}
