// CLASSIC: evidence-backed October 2010 puzzle helpers. No client program patching.
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

public sealed record PuzzleZoneHelpers(string Zone, string Package, string Member,
    ImmutableArray<string> RemoveTriggers, string SourceId);
public sealed record PuzzleQuestHelpers(string Quest, ImmutableArray<string> Goals, bool NoQuestHelper, string SourceId);

/// <summary>CLASSIC: reviewed data identities to remove or override, never a generic quest-arrow switch.</summary>
public sealed record PuzzleHelpers(string Id, ImmutableArray<string> Profiles, ImmutableArray<PuzzleZoneHelpers> Zones,
    ImmutableArray<PuzzleQuestHelpers> QuestHelpers, string SourceFile) {
    public const string OctoberProfile = "october-2010-arc1";
    public const string PolicyId = "puzzles-october-2010";
    public const string RelativePath = "rules/puzzles-october-2010.yaml";
    public static PuzzleHelpers Empty { get; } = new("", [], [], [], "");
    public bool AppliesTo(string? profileId) => profileId == OctoberProfile && Profiles.Contains(profileId, StringComparer.Ordinal);
}

/// <summary>CLASSIC: strict metadata reader shared by startup and deterministic policy tests.</summary>
public static class PuzzleHelpersLoader {
    private static readonly FrozenSet<string> s_rootKeys = Keys("id", "profiles", "license_tag", "provenance", "zones", "quest_helpers");
    private static readonly FrozenSet<string> s_sourceKeys = Keys("id", "source", "source_date", "retrieved", "covers", "confidence");
    private static readonly FrozenSet<string> s_zoneKeys = Keys("zone", "package", "member", "remove_triggers", "source_id");
    private static readonly FrozenSet<string> s_questKeys = Keys("quest", "goals", "no_quest_helper", "source_id");
    private static readonly Regex s_zone = new(@"^[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_quest = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant);

    /// <summary>Other profiles never inherit these changes. A configured October file must exist and validate.</summary>
    public static PuzzleHelpers LoadForProfile(ClassicProfile profile, string classicDataRoot) {
        if (profile.Id != PuzzleHelpers.OctoberProfile || profile.Rules.PuzzleHelpers is null) {
            return PuzzleHelpers.Empty;
        }
        if (profile.Rules.PuzzleHelpers != PuzzleHelpers.RelativePath) {
            throw new ClassicDataException(new ClassicDataError("profile " + profile.Id, "rules.puzzle_helpers", null,
                $"must name {PuzzleHelpers.RelativePath}"));
        }
        return Load(Path.Combine(classicDataRoot, PuzzleHelpers.RelativePath));
    }

    public static PuzzleHelpers Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the configured puzzle helper rules do not exist"));
        }
        var d = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, d);
        if (root is not YMap map) {
            if (root is not null) d.At(root, "", "the root must be a mapping");
            throw d.ToException();
        }
        d.CheckKeys(map, "", s_rootKeys, s_rootKeys);
        var id = Text(map, "id", "", d);
        if (id != PuzzleHelpers.PolicyId || Path.GetFileNameWithoutExtension(fullPath) != PuzzleHelpers.PolicyId) {
            d.At(map, "id", "id and file name must be " + PuzzleHelpers.PolicyId);
        }
        var profiles = Strings(map, "profiles", "", d);
        if (!profiles.SequenceEqual(new[] { PuzzleHelpers.OctoberProfile }, StringComparer.Ordinal)) {
            d.At(map, "profiles", "only october-2010-arc1 is supported");
        }
        if (Text(map, "license_tag", "", d) != "own") d.At(map, "license_tag", "must be own");

        var sources = new HashSet<string>(StringComparer.Ordinal);
        var provenance = Rows(map, "provenance", d);
        for (var i = 0; i < provenance.Length; i++) {
            var key = YamlTree.Index("provenance", i);
            if (d.ReadMap(provenance[i], key) is not { } row) continue;
            d.CheckKeys(row, key, s_sourceKeys, s_sourceKeys);
            var sourceId = Text(row, "id", key, d);
            if (sourceId is not null && !sources.Add(sourceId)) d.At(row, key + ".id", "duplicate source identity");
            var url = Text(row, "source", key, d);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
                d.At(row, key + ".source", "must be an absolute HTTP(S) source URL");
            }
            var date = row.Find("source_date") is { } sd ? d.ReadDate(sd.Value, key + ".source_date", false) : null;
            var retrieved = row.Find("retrieved") is { } rd ? d.ReadDate(rd.Value, key + ".retrieved", false) : null;
            if (date > new DateOnly(2010, 10, 31)) d.At(row, key + ".source_date", "source must be dated no later than 2010-10-31");
            if (retrieved < date) d.At(row, key + ".retrieved", "retrieved date precedes source date");
            _ = Strings(row, "covers", key, d);
            if (row.Find("confidence") is { } confidence) d.ReadEnum(confidence.Value, key + ".confidence", ["verified", "corroborated"]);
        }

        var zones = ImmutableArray.CreateBuilder<PuzzleZoneHelpers>();
        var zoneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zoneRows = Rows(map, "zones", d);
        for (var i = 0; i < zoneRows.Length; i++) {
            var key = YamlTree.Index("zones", i);
            if (d.ReadMap(zoneRows[i], key) is not { } row) continue;
            d.CheckKeys(row, key, s_zoneKeys, s_zoneKeys);
            var zone = Text(row, "zone", key, d);
            var package = Text(row, "package", key, d);
            var member = Text(row, "member", key, d);
            var targets = Strings(row, "remove_triggers", key, d);
            var source = Source(row, key, sources, d);
            if (zone is not null) {
                if (!s_zone.IsMatch(zone)) d.At(row, key + ".zone", "must be a zone path without traversal");
                if (!zoneIds.Add(zone)) d.At(row, key + ".zone", "duplicate zone identity");
                if (package != zone.Replace('/', '-') + ".wad") d.At(row, key + ".package", "package must match the zone path");
            }
            if (member != "triggers.xml") d.At(row, key + ".member", "only triggers.xml is supported");
            if (zone is not null && package is not null && member is not null && source is not null) {
                zones.Add(new(zone, package, member, targets, source));
            }
        }

        var quests = ImmutableArray.CreateBuilder<PuzzleQuestHelpers>();
        var questIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questRows = Rows(map, "quest_helpers", d);
        for (var i = 0; i < questRows.Length; i++) {
            var key = YamlTree.Index("quest_helpers", i);
            if (d.ReadMap(questRows[i], key) is not { } row) continue;
            d.CheckKeys(row, key, s_questKeys, s_questKeys);
            var quest = Text(row, "quest", key, d);
            var goals = Strings(row, "goals", key, d);
            var noHelper = row.Find("no_quest_helper") is { } nh ? d.ReadBool(nh.Value, key + ".no_quest_helper") : null;
            var source = Source(row, key, sources, d);
            if (quest is not null) {
                if (!s_quest.IsMatch(quest)) d.At(row, key + ".quest", "invalid quest identity");
                if (!questIds.Add(quest)) d.At(row, key + ".quest", "duplicate quest identity");
            }
            if (noHelper != true) d.At(row, key + ".no_quest_helper", "must be true");
            if (quest is not null && source is not null) quests.Add(new(quest, goals, true, source));
        }
        if (d.HasErrors) throw d.ToException();
        return new(id!, profiles, zones.ToImmutable(), quests.ToImmutable(), display);
    }

    private static FrozenSet<string> Keys(params string[] keys) => keys.ToFrozenSet(StringComparer.Ordinal);
    private static string? Text(YMap map, string name, string key, YamlDiagnostics d) {
        if (map.Find(name) is not { } entry) return null;
        var value = d.ReadString(entry.Value, YamlTree.Join(key, name));
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value != value.Trim())) {
            d.At(entry.Value, YamlTree.Join(key, name), "must be a nonempty string without surrounding whitespace");
        }
        return value;
    }
    private static ImmutableArray<YNode> Rows(YMap map, string name, YamlDiagnostics d) {
        if (map.Find(name) is not { } entry || d.ReadList(entry.Value, name) is not { } list) return [];
        if (list.Items.IsEmpty) d.At(list, name, "needs at least one entry");
        return list.Items;
    }
    private static ImmutableArray<string> Strings(YMap map, string name, string key, YamlDiagnostics d) {
        var path = YamlTree.Join(key, name);
        if (map.Find(name) is not { } entry || d.ReadList(entry.Value, path) is not { } list) return [];
        if (list.Items.IsEmpty) d.At(list, path, "needs at least one entry");
        var values = ImmutableArray.CreateBuilder<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Items.Length; i++) {
            var value = d.ReadString(list.Items[i], YamlTree.Index(path, i));
            if (value is null) continue;
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || !unique.Add(value)) {
                d.At(list.Items[i], YamlTree.Index(path, i), "must be a unique nonempty string without surrounding whitespace");
            }
            values.Add(value);
        }
        return values.ToImmutable();
    }
    private static string? Source(YMap map, string key, HashSet<string> sources, YamlDiagnostics d) {
        var source = Text(map, "source_id", key, d);
        if (source is not null && !sources.Contains(source)) d.At(map, key + ".source_id", "unknown provenance source identity");
        return source;
    }
}
