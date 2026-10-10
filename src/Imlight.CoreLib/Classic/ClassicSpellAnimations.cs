// CLASSIC: the older spell animations' stage lengths, so the round timer matches what the client plays.
//
// classic-data/rules/spell-animations-2014.json lists the spell cinematics that r806919 replaced with later remakes
// (Orthrus's lost second attack, the AoE rebuilds of Storm Lord, Fire Dragon, Meteor Strike, ...). For the profiles
// it names, tools/mac/classic_spell_anims.py installs the 2014 client's cinematic in the player's Root.wad; the
// server's own Root.wad still has r806919's, so SpellCinematics asks here first and times the cast from the 2014
// stages instead (the same summon, act and hanging lengths the client plays). Every other profile, and every
// cinematic not listed, keeps the server's Root.wad template.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

internal static class ClassicSpellAnimations {

    internal const string RelativePath = "rules/spell-animations-2014.json";

    private static IReadOnlyDictionary<string, CinematicTemplate>? s_templates;
    private static string? s_profile;

    /// <summary>The loaded templates by cinematic name (tests).</summary>
    internal static IReadOnlyDictionary<string, CinematicTemplate>? Templates => s_templates;

    /// <summary>
    /// Loads the listed cinematics when <paramref name="profile"/> is one the data names; otherwise clears them.
    /// </summary>
    internal static void Initialize(string? root, string profile) {
        s_templates = null;
        s_profile = profile;
        if (root is null) return;
        var path = Path.Combine(root, RelativePath);
        if (!File.Exists(path)) return;
        var (profiles, templates) = Load(path);
        if (!profiles.Contains(profile)) return;
        s_templates = templates;
        Logger.Information("Classic spell animations: {0} cinematics timed from the 2014 client's stages.",
            Logger.Args(templates.Count));
    }

    internal static (IReadOnlySet<string> Profiles, IReadOnlyDictionary<string, CinematicTemplate> Templates) Load(string filename) {
        using var stream = File.OpenRead(filename);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Spell animation data exceeds its bound.");
        using var data = JsonDocument.Parse(stream);
        var document = data.RootElement;
        if (document.GetProperty("format").GetInt32() != 1)
            throw new InvalidDataException("Unexpected spell animation data format.");
        var profiles = document.GetProperty("profiles").EnumerateArray().Select(p => p.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        if (profiles.Count == 0 || profiles.Contains("") || profiles.Contains("dev-unrestricted"))
            throw new InvalidDataException("Spell animation data names no usable profile.");
        var templates = new Dictionary<string, CinematicTemplate>(StringComparer.Ordinal);
        foreach (var row in document.GetProperty("cinematics").EnumerateArray()) {
            var name = row.GetProperty("cinematic").GetString();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A spell animation row has no cinematic name.");
            var stages = new List<CinematicStageTemplate>();
            foreach (var stage in row.GetProperty("stages").EnumerateArray()) {
                var duration = stage.GetProperty("duration").GetSingle();
                if (!float.IsFinite(duration) || duration is < 0 or > 60)
                    throw new InvalidDataException($"Spell animation {name} has a stage length out of range.");
                stages.Add(Stage(stage.GetProperty("type").GetString(), stage.GetProperty("name").GetString() ?? "", duration));
            }
            if (stages.Count is 0 or > 64 || !templates.TryAdd(name, new CinematicTemplate { m_name = name, m_stages = stages, m_actors = [] }))
                throw new InvalidDataException($"Spell animation {name} has no stages or is listed twice.");
        }
        if (templates.Count is 0 or > 256) throw new InvalidDataException("Spell animation data has no rows or too many.");
        return (profiles, templates);
    }

    private static CinematicStageTemplate Stage(string? type, string name, float duration) => type switch {
        "Summon" => new SummonCinematicStageTemplate { m_name = name, m_duration = duration },
        "Act" => new ActCinematicStageTemplate { m_name = name, m_duration = duration },
        "AddHanging" => new AddHangingCinematicStageTemplate { m_name = name, m_duration = duration },
        "Release" => new ReleaseCinematicStageTemplate { m_name = name, m_duration = duration },
        "Give" => new GiveCinematicStageTemplate { m_name = name, m_duration = duration },
        "Take" => new TakeCinematicStageTemplate { m_name = name, m_duration = duration },
        "Stage" => new CinematicStageTemplate { m_name = name, m_duration = duration },
        _ => throw new InvalidDataException($"Unknown spell animation stage type {type}."),
    };

    /// <summary>
    /// The 2014 cinematic the active profile's clients play for <paramref name="name"/>, when the data lists it.
    /// </summary>
    internal static bool TryGet(string? name, out CinematicTemplate template) {
        template = null!;
        if (name is null || s_templates is null || !ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive
            || ClassicRuntime.Rules.Profile.Id != s_profile) {
            return false;
        }

        if (!s_templates.TryGetValue(name, out var found)) return false;
        template = found;

        return true;
    }

}
