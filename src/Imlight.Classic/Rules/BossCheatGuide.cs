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
 * CLASSIC BOSS CHEAT GUIDE
 * ========================================================================
 *
 * PURPOSE:
 * The player-facing guide to Briskbreeze Tower's boss cheats (the guide
 * block of classic-data/creatures/boss-cheats-2009.yaml), and the check that
 * holds its text to the cheat data so the two cannot drift: every number a
 * boss's text gives must be one of that boss's cheat values (or a number of
 * a spell it, or a creature it summons, casts), and every cheat must be
 * named. The server shows it in the spellbook's Quests tab once the tower is
 * unlocked (Imlight.CoreLib.Classic.TowerGuide).
 *
 * USAGE EXAMPLE:
 * var problems = BossCheatGuideCheck.Problems(cheats.Bosses, cheats.Guide!);
 *
 * NOTE:
 * The 2009 client shows the text as-is; $ { } < > [ ] # | % _ are markup or
 * string-key characters to it, so the text is plain.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// What a cheat spell does, as its client template gives it.
/// </summary>
/// <param name="Spell">The client spell template name.</param>
/// <param name="Name">The name on its card.</param>
/// <param name="School">Its school.</param>
/// <param name="Damage">Its damage, if it deals any.</param>
/// <param name="Pips">Its pip cost.</param>
/// <param name="CastBy">The summoned creature whose spell it is, or null for the boss's own.</param>
public sealed record BossGuideSpell(string Spell, string Name, string School, int? Damage, int Pips, uint? CastBy);

/// <summary>
/// One boss's pages of the guide.
/// </summary>
/// <param name="Template">The boss's client template id.</param>
/// <param name="ShownAs">The name over its head in the client.</param>
/// <param name="Pages">What it does, a page each.</param>
/// <param name="Tip">One line of advice, starting "Tip: ".</param>
public sealed record BossGuideBoss(uint Template, string ShownAs, ImmutableArray<string> Pages, string Tip);

/// <summary>
/// The guide to a dungeon's boss cheats.
/// </summary>
public sealed record BossCheatGuide(string Title, string Location, ImmutableArray<string> Intro,
    ImmutableArray<BossGuideSpell> Spells, ImmutableArray<BossGuideBoss> Bosses) {

    /// <summary>
    /// The pages in reading order: the intro, then each boss by floor (its pages, then its tip).
    /// </summary>
    public IEnumerable<string> PagesInOrder(IEnumerable<BossCheat> bosses) {
        foreach (var page in Intro) {
            yield return page;
        }

        var floors = bosses.ToDictionary(b => b.Template, b => b.Floor);
        foreach (var boss in Bosses.OrderBy(b => floors.GetValueOrDefault(b.Template, int.MaxValue))) {
            foreach (var page in boss.Pages) {
                yield return page;
            }

            yield return boss.Tip;
        }
    }

}

/// <summary>
/// Holds the guide's text to the cheats.
/// </summary>
public static class BossCheatGuideCheck {

    /// <summary>The longest page (the client's own dialogue pages run to about 170 characters).</summary>
    public const int MaxPage = 200;

    /// <summary>The longest title (the quest page's title line shrinks to fit).</summary>
    public const int MaxTitle = 40;

    private static readonly Regex s_plain = new(@"^[A-Za-z0-9 .,:;'!?()\-]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_location = new(@"^[A-Za-z0-9 .,'\-]+\|[A-Za-z0-9 .,'\-]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_number = new(@"\d[\d,]*\d|\d", RegexOptions.CultureInvariant);

    /// <summary>
    /// Why a piece of player text is not plain enough for the client, or null.
    /// </summary>
    public static string? TextProblem(string text, int max) {
        if (text.Length == 0 || text.Length > max) {
            return $"'{Short(text)}' is {text.Length} characters; it must be 1 to {max}";
        }

        return s_plain.IsMatch(text) ? null
            : $"'{Short(text)}' may only use letters, digits, spaces and . , : ; ' ! ? ( ) - (the client reads $ {{ }} < > [ ] # | % _ as markup)";
    }

    /// <summary>
    /// Every way <paramref name="guide"/> disagrees with <paramref name="bosses"/>; empty when it matches.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyCollection<BossCheat> bosses, BossCheatGuide guide) {
        var problems = new List<string>();
        void Text(string where, string text, int max) {
            if (TextProblem(text, max) is { } problem) {
                problems.Add($"{where}: {problem}");
            }
        }

        Text("title", guide.Title, MaxTitle);
        if (!s_location.IsMatch(guide.Location)) {
            problems.Add($"location '{guide.Location}' must be World|Zone in plain text");
        }

        for (var i = 0; i < guide.Intro.Length; i++) {
            Text($"intro[{i}]", guide.Intro[i], MaxPage);
        }

        var spells = guide.Spells.ToDictionary(s => s.Spell, StringComparer.OrdinalIgnoreCase);
        var summoned = bosses.SelectMany(b => b.Summons).Select(s => s.Creature).ToHashSet();
        foreach (var spell in guide.Spells) {
            Text($"spells {spell.Spell}: name", spell.Name, MaxTitle);
            if (spell.CastBy is { } creature && !summoned.Contains(creature)) {
                problems.Add($"spells {spell.Spell}: cast_by {creature} is not a creature a boss summons");
            }
        }

        var described = guide.Bosses.Select(b => b.Template).ToHashSet();
        foreach (var boss in bosses.Where(b => !described.Contains(b.Template))) {
            problems.Add($"boss {boss.Template} ({boss.Name}) has cheats but no guide pages");
        }

        var usedSpells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in guide.Bosses) {
            var where = $"guide boss {page.Template} ({page.ShownAs})";
            var boss = bosses.FirstOrDefault(b => b.Template == page.Template);
            if (boss is null) {
                problems.Add($"{where}: not a cheating boss");
                continue;
            }

            Text($"{where}: shown_as", page.ShownAs, MaxTitle);
            for (var i = 0; i < page.Pages.Length; i++) {
                Text($"{where}: pages[{i}]", page.Pages[i], MaxPage);
            }

            Text($"{where}: tip", page.Tip, MaxPage);
            if (!page.Tip.StartsWith("Tip: ", StringComparison.Ordinal)) {
                problems.Add($"{where}: the tip must start with 'Tip: '");
            }

            var text = string.Join(" ", page.Pages.Append(page.Tip));
            var allowed = new HashSet<int>();
            void Need(int number, string what) {
                allowed.Add(number);
                if (!Numbers(text).Contains(number)) {
                    problems.Add($"{where}: the text never gives {what} ({number})");
                }
            }

            void Name(string name, string what) {
                if (!text.Contains(name, StringComparison.Ordinal)) {
                    problems.Add($"{where}: the text never names {what} ('{name}')");
                }
            }

            void Window(HealthWindow window, string what) {
                if (window.Below is { } below) {
                    Need(below, $"{what}: below");
                }

                if (window.AtLeast is { } atLeast) {
                    Need(atLeast, $"{what}: down to");
                }
            }

            BossGuideSpell? Spell(string name, string what) {
                usedSpells.Add(name);
                if (!spells.TryGetValue(name, out var spell)) {
                    problems.Add($"{where}: {what} '{name}' has no guide.spells entry");

                    return null;
                }

                Name(spell.Name, what);
                if (spell.Damage is { } damage) {
                    Need(damage, $"{spell.Name}'s damage");
                }

                if (spell.Pips > 0) {
                    Need(spell.Pips, $"{spell.Name}'s pips");
                }

                return spell;
            }

            Name(page.ShownAs, "the boss");
            Need(boss.Floor, "the floor");
            if (boss.Health is { } health) {
                Need(health, "its health");
            }

            if (boss.CastsPerRound > 1) {
                Need(boss.CastsPerRound, "its casts a round");
            }

            foreach (var free in boss.FreeSpells) {
                Spell(free.Spell, "free spell");
                Window(free.Health, $"free spell {free.Spell}");
            }

            if (boss.Interrupt is { } interrupt) {
                Name("Interrupt", "the interrupt");
                foreach (var spell in interrupt.Spells) {
                    Spell(spell, "interrupt spell");
                }

                Window(interrupt.Health, "the interrupt");
            }

            if (boss.DestroyTraps is { } traps) {
                Spell(traps.Spell, "trap-breaking spell");
                Name("trap", "trap breaking");
                if (traps.EveryRound is { } every) {
                    Name("every round", "the every-round trap break");
                    Window(every, "the every-round trap break");
                }
            }

            foreach (var summon in boss.Summons) {
                Name(summon.Name, "a summon");
                if (summon.Count > 1) {
                    Need(summon.Count, $"how many {summon.Name}s");
                }

                if (summon.Round is { } round) {
                    Need(round, $"the round of the {summon.Name}");
                }

                if (summon.HealthBelow is { } below) {
                    Need(below, $"the health that calls the {summon.Name}");
                }

                if (summon.Resummon && !text.Contains("another", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("again", StringComparison.OrdinalIgnoreCase)) {
                    problems.Add($"{where}: the text never says the {summon.Name} comes back ('another' or 'again')");
                }

                foreach (var spell in guide.Spells.Where(s => s.CastBy == summon.Creature)) {
                    Spell(spell.Spell, $"the {summon.Name}'s spell");
                }
            }

            foreach (var number in Numbers(text).Where(n => !allowed.Contains(n))) {
                problems.Add($"{where}: the text gives {number}, which is none of this boss's cheat values");
            }
        }

        foreach (var spell in guide.Spells.Where(s => !usedSpells.Contains(s.Spell))) {
            problems.Add($"spells {spell.Spell}: no boss or summon casts it");
        }

        return problems;
    }

    /// <summary>
    /// The numbers a text gives ("10,000" is 10000).
    /// </summary>
    public static IReadOnlySet<int> Numbers(string text)
        => s_number.Matches(text).Select(m => int.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToHashSet();

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "...";

}

/// <summary>
/// Reads the guide block of a boss-cheat file.
/// </summary>
internal static class BossCheatGuideLoader {

    internal static readonly FrozenSet<string> s_guideKeys = FrozenSet.Create(StringComparer.Ordinal,
        "title", "location", "intro", "spells", "bosses", "notes");
    internal static readonly FrozenSet<string> s_spellKeys = FrozenSet.Create(StringComparer.Ordinal,
        "spell", "name", "school", "damage", "pips", "cast_by", "notes");
    internal static readonly FrozenSet<string> s_bossKeys = FrozenSet.Create(StringComparer.Ordinal,
        "template", "shown_as", "pages", "tip", "notes");

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    internal static BossCheatGuide? Read(YNode node, ImmutableArray<BossCheat> bosses, YamlDiagnostics diagnostics) {
        const string keyPath = "guide";
        if (diagnostics.ReadMap(node, keyPath) is not { } map) {
            return null;
        }

        var before = diagnostics.HasErrors;
        diagnostics.CheckKeys(map, keyPath, s_guideKeys, ["title", "location", "intro", "spells", "bosses"]);
        var title = map.Find("title") is { } t ? diagnostics.ReadString(t.Value, "guide.title") : null;
        var location = map.Find("location") is { } l ? diagnostics.ReadString(l.Value, "guide.location") : null;
        var intro = map.Find("intro") is { } i ? Texts(i.Value, "guide.intro", diagnostics) : [];

        var spells = ImmutableArray.CreateBuilder<BossGuideSpell>();
        if (map.Find("spells") is { } se && diagnostics.ReadList(se.Value, "guide.spells") is { } spellList) {
            for (var j = 0; j < spellList.Items.Length; j++) {
                var path = YamlTree.Index("guide.spells", j);
                if (diagnostics.ReadMap(spellList.Items[j], path) is not { } sm) {
                    continue;
                }

                diagnostics.CheckKeys(sm, path, s_spellKeys, ["spell", "name", "school", "pips"]);
                var spell = sm.Find("spell") is { } sp ? diagnostics.ReadString(sp.Value, YamlTree.Join(path, "spell")) : null;
                var name = sm.Find("name") is { } nm ? diagnostics.ReadString(nm.Value, YamlTree.Join(path, "name")) : null;
                var school = sm.Find("school") is { } sc ? diagnostics.ReadEnum(sc.Value, YamlTree.Join(path, "school"), s_schools) : null;
                var damage = sm.Find("damage") is { } dm ? diagnostics.ReadInt(dm.Value, YamlTree.Join(path, "damage"), 1) : null;
                var pips = sm.Find("pips") is { } pp ? diagnostics.ReadInt(pp.Value, YamlTree.Join(path, "pips"), 0, 14) : null;
                var castBy = sm.Find("cast_by") is { } cb ? diagnostics.ReadInt(cb.Value, YamlTree.Join(path, "cast_by"), 1) : null;
                if (spell is null || name is null || school is null || pips is null) {
                    continue;
                }

                if (spells.Any(s => string.Equals(s.Spell, spell, StringComparison.OrdinalIgnoreCase))) {
                    diagnostics.At(sm, path, $"spell '{spell}' is listed twice");
                    continue;
                }

                spells.Add(new BossGuideSpell(spell, name, school, damage, pips.Value, castBy is { } c ? (uint) c : null));
            }
        }

        var pages = ImmutableArray.CreateBuilder<BossGuideBoss>();
        if (map.Find("bosses") is { } be && diagnostics.ReadList(be.Value, "guide.bosses") is { } bossList) {
            for (var j = 0; j < bossList.Items.Length; j++) {
                var path = YamlTree.Index("guide.bosses", j);
                if (diagnostics.ReadMap(bossList.Items[j], path) is not { } bm) {
                    continue;
                }

                diagnostics.CheckKeys(bm, path, s_bossKeys, ["template", "shown_as", "pages", "tip"]);
                var template = bm.Find("template") is { } tp ? diagnostics.ReadInt(tp.Value, YamlTree.Join(path, "template"), 1) : null;
                var shownAs = bm.Find("shown_as") is { } sa ? diagnostics.ReadString(sa.Value, YamlTree.Join(path, "shown_as")) : null;
                var bossPages = bm.Find("pages") is { } pg ? Texts(pg.Value, YamlTree.Join(path, "pages"), diagnostics) : [];
                var tip = bm.Find("tip") is { } ti ? diagnostics.ReadString(ti.Value, YamlTree.Join(path, "tip")) : null;
                if (template is null || shownAs is null || tip is null || bossPages.IsEmpty) {
                    continue;
                }

                if (pages.Any(p => p.Template == (uint) template.Value)) {
                    diagnostics.At(bm, path, $"template {template} is listed twice");
                    continue;
                }

                pages.Add(new BossGuideBoss((uint) template.Value, shownAs, bossPages, tip));
            }
        }

        if (title is null || location is null || intro.IsEmpty || diagnostics.HasErrors != before && diagnostics.HasErrors) {
            return null;
        }

        var guide = new BossCheatGuide(title, location, intro, spells.ToImmutable(), pages.ToImmutable());
        foreach (var problem in BossCheatGuideCheck.Problems(bosses, guide)) {
            diagnostics.At(map, keyPath, $"the guide does not match the cheats: {problem}");
        }

        return guide;
    }

    private static ImmutableArray<string> Texts(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        var texts = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            if (diagnostics.ReadString(list.Items[i], YamlTree.Index(keyPath, i)) is { } text) {
                texts.Add(text);
            }
        }

        return texts.ToImmutable();
    }

}
