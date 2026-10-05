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
 * CLASSIC BOSS CHEATS
 * ========================================================================
 *
 * PURPOSE:
 * The scripted boss "cheats" of 2009, by creature template id. Before the
 * 2010-05-25 cutoff only Briskbreeze Tower (the Gauntlet of Woe, October 2009)
 * had bosses that "do not follow the normal rules of combat": more than one
 * spell a round, interrupting a wizard's spell, destroying traps, spells for
 * no pips and scripted summons. The profile rule rules.boss_cheats names the
 * file (classic-data/creatures/boss-cheats-2009.yaml); a profile without the
 * rule has no cheating bosses.
 *
 * USAGE EXAMPLE:
 * var cheats = BossCheatsLoader.Load(path);
 * if (cheats.TryGet(creatureTemplateId, out var boss)) { ... boss.CastsPerRound ... }
 *
 * NOTE:
 * Every boss must stand in the file's dungeon zone (dungeon.zone), so a story
 * boss cannot be given cheats by mistake. Spells are client spell template
 * names (Spells/<name>.xml without the folder and extension).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// The kind of wizard spell that sets off a boss's interrupt.
/// </summary>
public enum CheatTrigger {
    /// <summary>Any spell a wizard casts.</summary>
    Any,
    /// <summary>A spell that hits the boss itself with damage (2010-01: "every time you hit him").</summary>
    Hit,
    /// <summary>A heal on the wizards' side.</summary>
    Heal,
    /// <summary>A shield or absorb on the wizards' side.</summary>
    Shield,
    /// <summary>A blade on the wizards' side.</summary>
    Blade,
}

/// <summary>
/// A health window: the cheat works while the boss's health is below <see cref="Below"/> and at least
/// <see cref="AtLeast"/> (either may be absent).
/// </summary>
public readonly record struct HealthWindow(int? Below, int? AtLeast) {

    /// <summary>True when <paramref name="health"/> is inside the window.</summary>
    public bool Contains(int health) => (Below is not { } below || health < below) && (AtLeast is not { } atLeast || health >= atLeast);

}

/// <summary>
/// A spell a boss casts for no pips, in some health window.
/// </summary>
public sealed record BossFreeSpell(string Spell, HealthWindow Health);

/// <summary>
/// A boss's interrupt: an out-of-turn cast in answer to a wizard's spell. The wizard's spell is not cancelled; the
/// boss's cast follows it at once, before the next caster in the round.
/// </summary>
/// <param name="On">The kinds of wizard spell it answers.</param>
/// <param name="Spells">The spells it answers with (cast for no pips).</param>
/// <param name="Message">The client string key the client shows with the cast (WC-ActorDialog_00000771 is "Interrupt!").</param>
/// <param name="PerRound">How many interrupts a round at most.</param>
/// <param name="Health">The health window in which it interrupts.</param>
/// <param name="Source">Where the behaviour is described.</param>
public sealed record BossInterrupt(ImmutableArray<CheatTrigger> On, ImmutableArray<string> Spells, string? Message, int PerRound,
    HealthWindow Health, string Source);

/// <summary>
/// A boss destroying the traps wizards put on its side.
/// </summary>
/// <param name="Spell">The spell it casts, out of turn and for no pips (Cleanse Ward removes the newest trap).</param>
/// <param name="OnPlaced">Casts it right after a wizard's trap lands on its side.</param>
/// <param name="EveryRound">Also casts it in its own turn each round while a trap is on it, in this health window (null: never).</param>
/// <param name="Message">The client string key shown with the cast, if any.</param>
/// <param name="Source">Where the behaviour is described.</param>
public sealed record BossTrapBreak(string Spell, bool OnPlaced, HealthWindow? EveryRound, string? Message, string Source);

/// <summary>
/// Creatures a boss brings into the fight.
/// </summary>
/// <param name="Creature">The creature's client template id.</param>
/// <param name="Name">The creature's name.</param>
/// <param name="Count">How many it brings at once.</param>
/// <param name="Round">The round in which it summons them (1 = the first round), or null.</param>
/// <param name="HealthBelow">Summons them when a cast takes its health below this, or null.</param>
/// <param name="Resummon">Summons them again, a round after the last of them was defeated.</param>
/// <param name="Source">Where the summon is described.</param>
public sealed record BossSummon(uint Creature, string Name, int Count, int? Round, int? HealthBelow, bool Resummon, string Source);

/// <summary>
/// The cheats of one boss.
/// </summary>
public sealed record BossCheat {
    public required uint Template { get; init; }
    public required string Name { get; init; }
    public required int Floor { get; init; }
    public required string Zone { get; init; }
    /// <summary>Spells it casts in its own turn each round (its ordinary card plus extra casts).</summary>
    public required int CastsPerRound { get; init; }
    /// <summary>Spells it casts for no pips; its extra casts are drawn from those of its current health.</summary>
    public required ImmutableArray<BossFreeSpell> FreeSpells { get; init; }
    public BossInterrupt? Interrupt { get; init; }
    public BossTrapBreak? DestroyTraps { get; init; }
    public required ImmutableArray<BossSummon> Summons { get; init; }
    public required string Source { get; init; }
    public required DateOnly SourceDate { get; init; }

    /// <summary>The free spells it may cast at <paramref name="health"/>.</summary>
    public IEnumerable<string> FreeSpellsAt(int health) => FreeSpells.Where(s => s.Health.Contains(health)).Select(s => s.Spell);
}

/// <summary>
/// The cheating bosses the classic server knows.
/// </summary>
public sealed class BossCheats {

    /// <summary>
    /// A table with no cheating bosses.
    /// </summary>
    public static BossCheats Empty { get; } = new() {
        Id = "",
        Profiles = [],
        DungeonZone = "",
        Bosses = [],
        SourceFile = "",
    };

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    /// <summary>The zone every boss stands in (a prefix: WizardCity/Gauntlets/WC_Gauntlet_01).</summary>
    public required string DungeonZone { get; init; }
    public required ImmutableArray<BossCheat> Bosses { get; init; }
    public required string SourceFile { get; init; }

    internal FrozenDictionary<uint, BossCheat> _byTemplate { get; init; } = FrozenDictionary<uint, BossCheat>.Empty;

    /// <summary>
    /// The number of cheating boss templates.
    /// </summary>
    public int Count => _byTemplate.Count;

    /// <summary>
    /// The cheats of <paramref name="template"/>, if it is a cheating boss.
    /// </summary>
    public bool TryGet(uint template, out BossCheat boss) => _byTemplate.TryGetValue(template, out boss!);

    /// <summary>
    /// Test hook: a table built in code.
    /// </summary>
    public static BossCheats ForTests(string dungeonZone, params BossCheat[] bosses) => new() {
        Id = "boss-cheats-test",
        Profiles = [],
        DungeonZone = dungeonZone,
        Bosses = [.. bosses],
        SourceFile = "",
        _byTemplate = bosses.ToFrozenDictionary(b => b.Template),
    };

}

/// <summary>
/// Loads and validates the boss-cheat table.
/// </summary>
public static class BossCheatsLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kind", "version", "id", "title", "profiles", "license_tag", "provenance", "notes", "dungeon", "floors", "bosses");
    internal static readonly FrozenSet<string> s_dungeonKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "zone", "entry", "source", "source_date", "notes");
    internal static readonly FrozenSet<string> s_bossKeys = FrozenSet.Create(StringComparer.Ordinal,
        "template", "name", "template_name", "floor", "zone", "school", "health", "casts_per_round",
        "free_spells", "interrupt", "destroy_traps", "summons", "source", "source_date", "notes");
    internal static readonly FrozenSet<string> s_freeKeys = FrozenSet.Create(StringComparer.Ordinal,
        "spell", "health_below", "health_at_least", "source", "notes");
    internal static readonly FrozenSet<string> s_interruptKeys = FrozenSet.Create(StringComparer.Ordinal,
        "triggers", "spells", "message", "per_round", "health_below", "health_at_least", "source", "notes");
    internal static readonly FrozenSet<string> s_trapKeys = FrozenSet.Create(StringComparer.Ordinal,
        "spell", "on_placed", "every_round", "message", "source", "notes");
    internal static readonly FrozenSet<string> s_windowKeys = FrozenSet.Create(StringComparer.Ordinal,
        "health_below", "health_at_least", "notes");
    internal static readonly FrozenSet<string> s_summonKeys = FrozenSet.Create(StringComparer.Ordinal,
        "creature", "name", "count", "round", "health_below", "resummon", "source", "notes");

    private static readonly Regex s_id = new(@"^boss-cheats(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);
    private static readonly string[] s_triggers = ["any", "hit", "heal", "shield", "blade"];

    /// <summary>
    /// Loads the table at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static BossCheats Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the boss-cheat table does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["kind", "version", "id", "profiles", "license_tag", "dungeon", "bosses"]);
        if (map.Find("kind") is { } kindEntry && diagnostics.ReadString(kindEntry.Value, "kind") is { } kind && kind != "boss-cheats") {
            diagnostics.At(kindEntry.Value, "kind", $"kind '{kind}' must be boss-cheats");
        }

        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be boss-cheats[-name] and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);

        string? dungeonZone = null;
        if (map.Find("dungeon") is { } dungeonEntry && diagnostics.ReadMap(dungeonEntry.Value, "dungeon") is { } dungeon) {
            diagnostics.CheckKeys(dungeon, "dungeon", s_dungeonKeys, ["name", "zone", "source", "source_date"]);
            dungeonZone = dungeon.Find("zone") is { } z ? diagnostics.ReadString(z.Value, "dungeon.zone") : null;
            if (dungeonZone is not null && (dungeonZone.Length == 0 || dungeonZone.EndsWith('/'))) {
                diagnostics.At(dungeon.Find("zone")!.Value, "dungeon.zone", "the dungeon zone must be a zone path without a trailing '/'");
                dungeonZone = null;
            }
        }

        var bosses = ImmutableArray.CreateBuilder<BossCheat>();
        var seen = new HashSet<uint>();
        if (map.Find("bosses") is { } bossesEntry && diagnostics.ReadList(bossesEntry.Value, "bosses") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("bosses", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } entry) {
                    continue;
                }

                if (ReadBoss(entry, keyPath, dungeonZone, diagnostics) is not { } boss) {
                    continue;
                }

                if (!seen.Add(boss.Template)) {
                    diagnostics.At(entry.Find("template")!.Value, YamlTree.Join(keyPath, "template"), $"template {boss.Template} is listed twice");
                    continue;
                }

                bosses.Add(boss);
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        var built = bosses.ToImmutable();

        return new BossCheats {
            Id = id!,
            Profiles = profiles,
            DungeonZone = dungeonZone!,
            Bosses = built,
            SourceFile = display,
            _byTemplate = built.ToFrozenDictionary(b => b.Template),
        };
    }

    private static BossCheat? ReadBoss(YMap entry, string keyPath, string? dungeonZone, YamlDiagnostics diagnostics) {
        diagnostics.CheckKeys(entry, keyPath, s_bossKeys, ["template", "name", "floor", "zone", "source", "source_date"]);
        var template = entry.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(keyPath, "template"), 1) : null;
        var name = entry.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "name")) : null;
        var floor = entry.Find("floor") is { } f ? diagnostics.ReadInt(f.Value, YamlTree.Join(keyPath, "floor"), 1, 10) : null;
        var zone = entry.Find("zone") is { } z ? diagnostics.ReadString(z.Value, YamlTree.Join(keyPath, "zone")) : null;
        var source = entry.Find("source") is { } s ? diagnostics.ReadString(s.Value, YamlTree.Join(keyPath, "source")) : null;
        var date = entry.Find("source_date") is { } d ? diagnostics.ReadDate(d.Value, YamlTree.Join(keyPath, "source_date"), false) : null;
        var casts = entry.Find("casts_per_round") is { } c ? diagnostics.ReadInt(c.Value, YamlTree.Join(keyPath, "casts_per_round"), 1, 3) : 1;

        if (zone is not null && dungeonZone is not null
            && !zone.StartsWith(dungeonZone + "/", StringComparison.OrdinalIgnoreCase)) {
            diagnostics.At(entry.Find("zone")!.Value, YamlTree.Join(keyPath, "zone"),
                $"zone '{zone}' is not in the dungeon '{dungeonZone}'; only the dungeon's bosses may cheat");
        }

        var freeSpells = ImmutableArray.CreateBuilder<BossFreeSpell>();
        if (entry.Find("free_spells") is { } fe && diagnostics.ReadList(fe.Value, YamlTree.Join(keyPath, "free_spells")) is { } freeList) {
            for (var j = 0; j < freeList.Items.Length; j++) {
                var path = YamlTree.Index(YamlTree.Join(keyPath, "free_spells"), j);
                if (diagnostics.ReadMap(freeList.Items[j], path) is not { } fm) {
                    continue;
                }

                diagnostics.CheckKeys(fm, path, s_freeKeys, ["spell", "source"]);
                var spell = fm.Find("spell") is { } sp ? diagnostics.ReadString(sp.Value, YamlTree.Join(path, "spell")) : null;
                if (spell is null) {
                    continue;
                }

                if (freeSpells.Any(x => string.Equals(x.Spell, spell, StringComparison.OrdinalIgnoreCase))) {
                    diagnostics.At(fm, path, $"spell '{spell}' is listed twice");
                    continue;
                }

                freeSpells.Add(new BossFreeSpell(spell, ReadWindow(fm, path, diagnostics)));
            }
        }

        if (casts > 1 && freeSpells.Count == 0) {
            diagnostics.At(entry, keyPath, "a boss with more than one cast a round needs free_spells to draw its extra casts from");
        }

        BossInterrupt? interrupt = null;
        if (entry.Find("interrupt") is { } ie && diagnostics.ReadMap(ie.Value, YamlTree.Join(keyPath, "interrupt")) is { } im) {
            var path = YamlTree.Join(keyPath, "interrupt");
            diagnostics.CheckKeys(im, path, s_interruptKeys, ["triggers", "spells", "per_round", "source"]);
            var on = ImmutableArray.CreateBuilder<CheatTrigger>();
            if (im.Find("triggers") is { } onEntry && diagnostics.ReadList(onEntry.Value, YamlTree.Join(path, "triggers")) is { } onList) {
                for (var j = 0; j < onList.Items.Length; j++) {
                    if (diagnostics.ReadEnum(onList.Items[j], YamlTree.Index(YamlTree.Join(path, "triggers"), j), s_triggers) is { } trigger) {
                        on.Add(Enum.Parse<CheatTrigger>(trigger, ignoreCase: true));
                    }
                }

                if (onList.Items.IsEmpty) {
                    diagnostics.At(onEntry.Value, YamlTree.Join(path, "triggers"), "an interrupt needs at least one trigger");
                }
            }

            var spells = im.Find("spells") is { } se ? ReadNames(se.Value, YamlTree.Join(path, "spells"), diagnostics) : [];
            if (im.Find("spells") is not null && spells.IsEmpty) {
                diagnostics.At(im, path, "an interrupt needs at least one spell");
            }

            var message = im.Find("message") is { } me ? diagnostics.ReadString(me.Value, YamlTree.Join(path, "message")) : null;
            var perRound = im.Find("per_round") is { } pr ? diagnostics.ReadInt(pr.Value, YamlTree.Join(path, "per_round"), 1, 8) : null;
            var isource = im.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(path, "source")) : null;
            var window = ReadWindow(im, path, diagnostics);
            if (perRound is not null && isource is not null && !spells.IsEmpty) {
                interrupt = new BossInterrupt(on.ToImmutable(), spells, message, perRound.Value, window, isource);
            }
        }

        BossTrapBreak? traps = null;
        if (entry.Find("destroy_traps") is { } te && diagnostics.ReadMap(te.Value, YamlTree.Join(keyPath, "destroy_traps")) is { } tm) {
            var path = YamlTree.Join(keyPath, "destroy_traps");
            diagnostics.CheckKeys(tm, path, s_trapKeys, ["spell", "on_placed", "source"]);
            var spell = tm.Find("spell") is { } sp ? diagnostics.ReadString(sp.Value, YamlTree.Join(path, "spell")) : null;
            var onPlaced = tm.Find("on_placed") is { } op ? diagnostics.ReadBool(op.Value, YamlTree.Join(path, "on_placed")) : null;
            HealthWindow? everyRound = null;
            if (tm.Find("every_round") is { } er && diagnostics.ReadMap(er.Value, YamlTree.Join(path, "every_round")) is { } erm) {
                diagnostics.CheckKeys(erm, YamlTree.Join(path, "every_round"), s_windowKeys, []);
                everyRound = ReadWindow(erm, YamlTree.Join(path, "every_round"), diagnostics);
            }

            var message = tm.Find("message") is { } me ? diagnostics.ReadString(me.Value, YamlTree.Join(path, "message")) : null;
            var tsource = tm.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(path, "source")) : null;
            if (spell is not null && onPlaced is not null && tsource is not null) {
                traps = new BossTrapBreak(spell, onPlaced.Value, everyRound, message, tsource);
            }
        }

        var summons = ImmutableArray.CreateBuilder<BossSummon>();
        if (entry.Find("summons") is { } sume && diagnostics.ReadList(sume.Value, YamlTree.Join(keyPath, "summons")) is { } sumList) {
            for (var j = 0; j < sumList.Items.Length; j++) {
                var path = YamlTree.Index(YamlTree.Join(keyPath, "summons"), j);
                if (diagnostics.ReadMap(sumList.Items[j], path) is not { } sm) {
                    continue;
                }

                diagnostics.CheckKeys(sm, path, s_summonKeys, ["creature", "name", "count", "source"]);
                var creature = sm.Find("creature") is { } cr ? diagnostics.ReadInt(cr.Value, YamlTree.Join(path, "creature"), 1) : null;
                var sname = sm.Find("name") is { } nm ? diagnostics.ReadString(nm.Value, YamlTree.Join(path, "name")) : null;
                var count = sm.Find("count") is { } ct ? diagnostics.ReadInt(ct.Value, YamlTree.Join(path, "count"), 1, 3) : null;
                var round = sm.Find("round") is { } rd ? diagnostics.ReadInt(rd.Value, YamlTree.Join(path, "round"), 1, 99) : null;
                var below = sm.Find("health_below") is { } hb ? diagnostics.ReadInt(hb.Value, YamlTree.Join(path, "health_below"), 1) : null;
                var resummon = sm.Find("resummon") is { } rs ? diagnostics.ReadBool(rs.Value, YamlTree.Join(path, "resummon")) : false;
                var ssource = sm.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(path, "source")) : null;
                if ((round is null) == (below is null)) {
                    diagnostics.At(sm, path, "a summon needs exactly one of round and health_below");
                    continue;
                }

                if (creature is not null && sname is not null && count is not null && ssource is not null && resummon is not null) {
                    summons.Add(new BossSummon((uint) creature.Value, sname, count.Value, round, below, resummon.Value, ssource));
                }
            }
        }

        if (template is null || name is null || floor is null || zone is null || source is null || date is null || casts is null) {
            return null;
        }

        return new BossCheat {
            Template = (uint) template.Value,
            Name = name,
            Floor = floor.Value,
            Zone = zone,
            CastsPerRound = casts.Value,
            FreeSpells = freeSpells.ToImmutable(),
            Interrupt = interrupt,
            DestroyTraps = traps,
            Summons = summons.ToImmutable(),
            Source = source,
            SourceDate = date.Value,
        };
    }

    private static HealthWindow ReadWindow(YMap map, string keyPath, YamlDiagnostics diagnostics) {
        var below = map.Find("health_below") is { } b ? diagnostics.ReadInt(b.Value, YamlTree.Join(keyPath, "health_below"), 1) : null;
        var atLeast = map.Find("health_at_least") is { } a ? diagnostics.ReadInt(a.Value, YamlTree.Join(keyPath, "health_at_least"), 0) : null;
        if (below is not null && atLeast is not null && atLeast >= below) {
            diagnostics.At(map, keyPath, $"health_at_least {atLeast} must be below health_below {below}");
        }

        return new HealthWindow(below, atLeast);
    }

    private static ImmutableArray<string> ReadNames(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        var names = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            if (diagnostics.ReadString(list.Items[i], YamlTree.Index(keyPath, i)) is not { } name) {
                continue;
            }

            if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                diagnostics.At(list.Items[i], YamlTree.Index(keyPath, i), $"spell '{name}' is listed twice");
                continue;
            }

            names.Add(name);
        }

        return names.ToImmutable();
    }

}
