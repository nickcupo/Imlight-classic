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
 * AMBIENT CHAT PLANNER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): when a zone's ambient wizards talk on their own
 * and what they say, without the server (testable).
 *   - ChatRhythm: a zone has quiet spells (4-12 minutes, a line every
 *     2-6 minutes, often none) and busy spells (2-5 minutes, a line or a
 *     short talk every 25-90 seconds), as a real hub had. A player
 *     talking makes the zone a little busier.
 *   - Solo: one wizard's line for what it is doing (AmbientLinePool),
 *     skipping what it said in its last forty lines and what the
 *     listeners heard lately (LineHistory), typed its way (ChatStyle).
 *   - Exchange: two wizards talk for two to four turns (AmbientLinePool
 *     .Exchanges), each line after the other's reading, thinking and
 *     typing time (ChatTiming). A turn whose every alternative fails the
 *     filter ends the talk there.
 *   - PickSpeaker: chatty wizards talk more, quiet ones rarely, nobody
 *     twice in two minutes.
 *   - CLASSIC (2026-10-10): threads (ChatMemory.Thread): now and then an
 *     idle wizard starts a small story (AmbientLinePool.Threads) and
 *     tells its next step on later turns, so it refers back to itself;
 *     "a||b" templates go out as two lines (PlannedLine.Then); an
 *     exchange turn may be silence, which ends the talk; Sendable is the
 *     last check (IsClean and the client's dictionary) for every line.
 * Randomness comes from a Random the caller seeds, so a run can be
 * replayed in tests.
 *
 * USAGE EXAMPLE:
 * if (rhythm.Due(now)) { var line = AmbientChatPlanner.Solo(speaker, rng, filter); rhythm.Spent(now); }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>
/// What one wizard said lately, so it does not repeat itself, and (CLASSIC 2026-10-10) the small story it is in the
/// middle of telling (AmbientLinePool.Threads) and its last line, so it can refer back to them.
/// </summary>
public sealed class ChatMemory {

    /// <summary>How many of its own lines a wizard will not say again.</summary>
    public const int Window = 40;

    private readonly Queue<string> _said = new();
    private readonly HashSet<string> _told = new(StringComparer.Ordinal);

    public bool Said(string template) => _said.Contains(template);

    public void Note(string template) {
        _said.Enqueue(template);
        while (_said.Count > Window) {
            _said.Dequeue();
        }
    }

    /// <summary>The last line it sent, as typed, or null.</summary>
    public string? LastLine { get; set; }

    /// <summary>The story it is telling, or null.</summary>
    public ChatThread? Thread { get; private set; }

    /// <summary>The next step of <see cref="Thread"/>.</summary>
    public int Step { get; private set; }

    /// <summary>True when it told (or started) the thread with this key already this session.</summary>
    public bool Told(string key) => _told.Contains(key);

    /// <summary>Starts <paramref name="thread"/> at its first step.</summary>
    public void Start(ChatThread thread) {
        ArgumentNullException.ThrowIfNull(thread);
        Thread = thread;
        Step = 0;
        _told.Add(thread.Key);
    }

    /// <summary>The step was said: on to the next, or the story is over.</summary>
    public void Advance() {
        if (Thread is null) {
            return;
        }

        if (++Step >= Thread.Steps.Length) {
            Thread = null;
            Step = 0;
        }
    }

}

/// <summary>The zone's busy and quiet spells (see the file header).</summary>
public sealed class ChatRhythm {

    private readonly Random _rng;
    private DateTime _spellEnds;

    public ChatRhythm(Random rng, DateTime now) {
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        Busy = _rng.NextDouble() < 0.3;
        _spellEnds = now.AddSeconds(Busy ? 120 + _rng.Next(180) : 240 + _rng.Next(480));
        NextEvent = now.AddSeconds(20 + _rng.Next(70));
    }

    /// <summary>True in a busy spell.</summary>
    public bool Busy { get; private set; }

    /// <summary>When the zone may next talk on its own.</summary>
    public DateTime NextEvent { get; private set; }

    /// <summary>True when an unprompted line is due.</summary>
    public bool Due(DateTime now) => now >= NextEvent;

    /// <summary>
    /// The due moment passed (spoken or skipped): the next one is set, and a spell that ran out turns. In a quiet spell
    /// some moments are let go by (returns false: say nothing this time).
    /// </summary>
    public bool Spent(DateTime now) {
        if (now >= _spellEnds) {
            Busy = !Busy;
            _spellEnds = now.AddSeconds(Busy ? 120 + _rng.Next(180) : 240 + _rng.Next(480));
        }

        NextEvent = now.AddSeconds(Busy ? 25 + _rng.Next(65) : 120 + _rng.Next(240));
        return Busy || _rng.NextDouble() < 0.6;
    }

    /// <summary>Someone (a real player) talked: talk breeds talk, so the next line comes a bit sooner.</summary>
    public void Bump(DateTime now) {
        var soon = now.AddSeconds(20 + _rng.Next(40));
        if (NextEvent > soon) {
            NextEvent = soon;
        }
    }

}

/// <summary>One wizard as the planner sees it.</summary>
/// <param name="Persona">How it chats.</param>
/// <param name="Context">Who and where it is (and who listens).</param>
/// <param name="Moment">What it is doing.</param>
/// <param name="Memory">What it said lately.</param>
public sealed record ChatSpeaker(ChatPersona Persona, ChatContext Context, ChatMoment Moment, ChatMemory Memory);

/// <summary>
/// A line to send: by speaker 0 (A) or 1 (B), <see cref="After"/> the previous line of the plan (or now).
/// <see cref="Then"/> are more lines the same wizard sends right after, each after a short pause (a template "a||b").
/// </summary>
public sealed record PlannedLine(int Speaker, string Text, TimeSpan After, string Template, IReadOnlyList<string>? Then = null);

/// <summary>Picks unprompted lines (see the file header).</summary>
public static class AmbientChatPlanner {

    /// <summary>How long a wizard keeps quiet after its own line before the zone picks it again.</summary>
    public static readonly TimeSpan OwnGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The index of the wizard who speaks next, or -1. <paramref name="candidates"/> lists each wizard's temper, when it
    /// last spoke and whether it is free to talk.
    /// </summary>
    public static int PickSpeaker(IReadOnlyList<(AmbientTemper Temper, DateTime LastSpoke, bool Free)> candidates, DateTime now, Random rng) {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rng);
        var weights = candidates.Select(c => !c.Free || now - c.LastSpoke < OwnGap ? 0.0 : c.Temper switch {
            AmbientTemper.Chatty => 3.0,
            AmbientTemper.Friendly => 1.6,
            _ => 0.5,
        }).ToArray();
        var total = weights.Sum();
        if (total <= 0) {
            return -1;
        }

        var roll = rng.NextDouble() * total;
        for (var i = 0; i < weights.Length; i++) {
            roll -= weights[i];
            if (roll < 0 && weights[i] > 0) {
                return i;
            }
        }

        return Array.FindLastIndex(weights, w => w > 0);
    }

    /// <summary>How often an idle wizard in the middle of a story tells its next step instead of something else.</summary>
    public const double ThreadChance = 0.35;

    /// <summary>How often an idle wizard with no story starts one.</summary>
    public const double ThreadStartChance = 0.12;

    /// <summary>A line <paramref name="speaker"/> says on its own, or null when everything fitting was said lately.</summary>
    public static PlannedLine? Solo(ChatSpeaker speaker, Random rng, ChatWordFilter? filter = null, DayOfWeek? day = null) {
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(rng);
        var context = speaker.Context;
        var extra = Extra(context, rng);
        if (ThreadLine(speaker, rng, filter, extra) is { } told) {
            return told;
        }

        var pool = AmbientLinePool.Solo(speaker.Moment, context.ZoneKey, context.Level, context.School, context.Hour, speaker.Persona, day);
        var text = Pick(pool, speaker, rng, filter, extra, out var template);
        return text is null ? null : Planned(text, template!, speaker, rng);
    }

    // CLASSIC (2026-10-10): the next step of the wizard's story, or a new story, now and then (idle kids and teens only).
    private static PlannedLine? ThreadLine(ChatSpeaker speaker, Random rng, ChatWordFilter? filter, IReadOnlyDictionary<string, string> extra) {
        var memory = speaker.Memory;
        if (speaker.Moment != ChatMoment.Idle || speaker.Persona.Channel == ChatChannel.Menu || speaker.Persona.Grownup) {
            return null;
        }

        if (memory.Thread is null) {
            var fits = AmbientLinePool.ThreadsFor(speaker.Context.ZoneKey, speaker.Context.Level).Where(t => !memory.Told(t.Key)).ToList();
            if (fits.Count == 0 || rng.NextDouble() >= ThreadStartChance) {
                return null;
            }

            memory.Start(fits[rng.Next(fits.Count)]);
        }
        else if (rng.NextDouble() >= ThreadChance) {
            return null;
        }

        var thread = memory.Thread!;
        var template = thread.Steps[memory.Step];
        var key = $"t:{thread.Key}:{memory.Step}";
        memory.Advance();
        var filled = Fill(template, speaker, extra);
        if (filled is null) {
            return null;
        }

        var styled = ChatStyle.Apply(filled, speaker.Persona, rng, filter);
        if (!Sendable(styled, speaker.Persona, filter)) {
            return null;
        }

        memory.Note(key);
        memory.LastLine = styled;
        speaker.Context.History?.Note(speaker.Context.Audience ?? [], key);
        return new PlannedLine(0, styled, ChatTiming.Typing(styled, speaker.Persona, rng), key);
    }

    /// <summary>
    /// CLASSIC (2026-10-10): the last check before a line goes out: clean, and every word in the client's chat dictionary
    /// when the lists are loaded (numbers only for open chat).
    /// </summary>
    public static bool Sendable(string? line, ChatPersona persona, ChatWordFilter? filter)
        => AmbientChatBrain.IsClean(line) && (filter is null || !filter.HasDictionary || filter.Passes(line, persona.Numbers));

    // A picked line, split where the template had "||" (the rest go out right after, as their own lines).
    private static PlannedLine Planned(string text, string template, ChatSpeaker speaker, Random rng) {
        var parts = text.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = parts.Length > 0 ? parts[0] : text;
        return new PlannedLine(0, first, ChatTiming.Typing(first, speaker.Persona, rng), template, parts.Length > 1 ? parts[1..] : null);
    }

    /// <summary>
    /// A short talk between <paramref name="a"/> and <paramref name="b"/> (neither may be a menu-chat wizard), or an empty
    /// list. Its lines come in order, each <see cref="PlannedLine.After"/> the one before.
    /// </summary>
    public static List<PlannedLine> Exchange(ChatSpeaker a, ChatSpeaker b, Random rng, ChatWordFilter? filter = null,
                                             LineHistory? history = null) {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(rng);
        var plan = new List<PlannedLine>();
        if (a.Persona.Channel == ChatChannel.Menu || b.Persona.Channel == ChatChannel.Menu) {
            return plan;
        }

        var zone = a.Context.ZoneKey ?? "";
        var audience = a.Context.Audience ?? [];
        var fits = AmbientLinePool.Exchanges.Where(x => x.Zones.Length == 0 || x.Zones.Any(z => zone.Contains(z, StringComparison.OrdinalIgnoreCase)))
            .Where(x => history?.Recent(audience, "x:" + x.Key) != true && !a.Memory.Said("x:" + x.Key))
            .ToList();
        if (fits.Count == 0) {
            return plan;
        }

        var exchange = fits[rng.Next(fits.Count)];
        var bosses = AmbientLinePool.BossesFor(zone);
        var slots = new Dictionary<string, string> {
            ["a"] = a.Context.MyFirstName, ["b"] = b.Context.MyFirstName,
            ["aschool"] = AmbientChatBrain.SchoolName(a.Context.School).ToLowerInvariant(),
            ["bschool"] = AmbientChatBrain.SchoolName(b.Context.School).ToLowerInvariant(),
            ["pet"] = AmbientLinePool.Pet(rng.Next()),
            ["spell"] = AmbientLinePool.SpellFor(a.Context.School, a.Context.Level),
        };
        if (bosses.Length > 0) {
            slots["boss"] = bosses[rng.Next(bosses.Length)];
        }

        string? previous = null;
        var picked = 0;
        foreach (var turn in exchange.Turns) {
            var who = turn.StartsWith('B') ? 1 : 0;
            var speaker = who == 0 ? a : b;

            // CLASSIC (2026-10-10): "B=" answers the option the other one picked (the same position; '/' splits its
            // alternatives), so "what level are you" gets a level and "bored" gets "same"; "B:" picks freely.
            var all = turn[2..].Split('|');
            var aligned = turn[1] == '=' && picked < all.Length;
            var indexed = aligned
                ? all[picked].Split('/').Select(o => (Index: picked, Text: o)).OrderBy(_ => rng.Next()).ToList()
                : all.Select((o, i) => (Index: i, Text: o)).OrderBy(_ => rng.Next()).ToList();
            string? line = null;
            foreach (var (index, option) in indexed) {
                picked = index;
                if (option.Length == 0) {
                    break; // CLASSIC (2026-10-10): this one just doesn't answer; talks fizzle out like that
                }

                var filled = Fill(option, speaker, slots);
                if (filled is null) {
                    continue;
                }

                var styled = ChatStyle.Apply(filled, speaker.Persona, rng, filter);
                if (Sendable(styled, speaker.Persona, filter)) {
                    line = styled;
                    break;
                }
            }

            if (line is null) {
                break;
            }

            speaker.Memory.LastLine = line;

            var wait = previous is null
                ? ChatTiming.Typing(line, speaker.Persona, rng)
                : ChatTiming.Reading(previous, rng) + ChatTiming.Typing(line, speaker.Persona, rng);
            plan.Add(new PlannedLine(who, line, wait, "x:" + exchange.Key));
            previous = line;
        }

        if (plan.Count < 2) {
            plan.Clear();
            return plan;
        }

        a.Memory.Note("x:" + exchange.Key);
        history?.Note(audience, "x:" + exchange.Key);
        return plan;
    }

    /// <summary>
    /// A reply template from <paramref name="pool"/> for <paramref name="speaker"/>: filled, typed its way and checked,
    /// avoiding its own recent lines (answers fall back to a recent one rather than say nothing).
    /// </summary>
    public static string? Answer(IReadOnlyList<string> pool, ChatSpeaker speaker, Random rng, ChatWordFilter? filter = null,
                                 IReadOnlyDictionary<string, string>? extra = null)
        => Pick(pool, speaker, rng, filter, extra, out _, allowRepeat: true);

    private static string? Pick(IReadOnlyList<string> pool, ChatSpeaker speaker, Random rng, ChatWordFilter? filter,
                                IReadOnlyDictionary<string, string>? extra, out string? template, bool allowRepeat = false) {
        template = null;
        if (pool.Count == 0) {
            return null;
        }

        var audience = speaker.Context.Audience ?? [];
        var order = Enumerable.Range(0, pool.Count).OrderBy(_ => rng.Next()).ToList();
        string? fallback = null;
        string? fallbackTemplate = null;
        foreach (var i in order) {
            var candidate = pool[i];
            var filled = Fill(candidate, speaker, extra);
            if (filled is null) {
                continue;
            }

            // Each part of a double line ("a||b") is typed and checked on its own.
            var parts = filled.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => ChatStyle.Apply(part, speaker.Persona, rng, filter)).ToArray();
            if (parts.Length == 0 || !parts.All(part => Sendable(part, speaker.Persona, filter))) {
                continue;
            }

            var styled = string.Join("||", parts);
            if (speaker.Memory.Said(candidate) || speaker.Context.History?.Recent(audience, candidate) == true) {
                (fallback, fallbackTemplate) = (fallback ?? styled, fallbackTemplate ?? candidate);
                continue;
            }

            speaker.Memory.Note(candidate);
            speaker.Memory.LastLine = parts[^1];
            speaker.Context.History?.Note(audience, candidate);
            template = candidate;
            return styled;
        }

        if (allowRepeat && fallback is not null) {
            template = fallbackTemplate;
            return fallback;
        }

        return null;
    }

    /// <summary>A template filled for the speaker; null when a slot has no value or a number would show in dictionary chat.</summary>
    public static string? Fill(string template, ChatSpeaker speaker, IReadOnlyDictionary<string, string>? extra = null) {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(speaker);
        if (!speaker.Persona.Numbers && (template.Contains("{level}", StringComparison.Ordinal) || template.Contains("{next}", StringComparison.Ordinal))) {
            return null;
        }

        var slots = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["me"] = speaker.Context.MyName,
            ["school"] = AmbientChatBrain.SchoolName(speaker.Context.School).ToLowerInvariant(),
            ["spell"] = AmbientLinePool.SpellFor(speaker.Context.School, speaker.Context.Level),
        };
        if (speaker.Memory.LastLine is { Length: > 0 and <= 40 } last && !last.Contains("||", StringComparison.Ordinal)) {
            slots["last"] = last.TrimEnd('.', '!', '?').ToLowerInvariant();
        }
        foreach (var (key, value) in extra ?? new Dictionary<string, string>()) {
            slots[key] = value;
        }

        return AmbientLines.Fill(template, speaker.Context, slots);
    }

    private static Dictionary<string, string> Extra(ChatContext context, Random rng) {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        var bosses = AmbientLinePool.BossesFor(context.ZoneKey);
        if (bosses.Length > 0) {
            extra["boss"] = bosses[rng.Next(bosses.Length)];
        }

        if (AmbientLinePool.MobFor(context.ZoneKey, rng.Next()) is { } mob) {
            extra["mob"] = mob;
        }

        extra["pet"] = AmbientLinePool.Pet(rng.Next());
        extra["level"] = context.Level.ToString(CultureInfo.InvariantCulture);
        return extra;
    }

}
