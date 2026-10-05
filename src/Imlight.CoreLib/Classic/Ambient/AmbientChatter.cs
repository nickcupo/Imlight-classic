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
 * AMBIENT CHATTER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-05): one zone's ambient talk, run by its AmbientZone
 * actor (all calls on the actor's thread; lines go out through its
 * timers, nothing blocks).
 *   - On their own (Tick): the zone's ChatRhythm says when; a wizard is
 *     picked by temper; it says a line for what it is doing (hunting,
 *     shopping, near a boss, idle...) or two wizards talk for a few turns;
 *     now and then one goes "brb", stands still for a few minutes and
 *     comes "back". Only when real players are here to hear it.
 *   - Players (Census): a player who stops near a free wizard may get a
 *     "hi" (once in ten minutes per player, one greeting a minute per
 *     zone); the wizard then counts as talking with them for ninety
 *     seconds, so their next lines are answered without its name.
 *   - Heard: one decision per line for the whole zone: the wizard named,
 *     else the one already talking with the player, else (for a line to
 *     everyone: "anyone wanna...", "hi all") maybe the nearest willing
 *     wizard. Answers come after noticing, reading and typing time
 *     (ChatTiming), staggered so two wizards never answer at once, and
 *     typed in the wizard's own way. Menu-chat wizards answer with menu
 *     phrases.
 *   - Fights: "gg"/"ty for the help" after a win with players, "aw man"
 *     after a defeat.
 *   - LLM (optional, AmbientLlmClient): replies and some unprompted lines
 *     may come from the local model when it answers in time; otherwise the
 *     rule lines go out, so the game never waits.
 *
 * USAGE EXAMPLE:
 * _chatter.Tick(now, _audience); _chatter.Heard(wizard, speaker, text, whisper);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imcodec.Math;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>One zone's ambient talk (see the file header).</summary>
internal sealed class AmbientChatter {

    private const float NearForGreeting = 450f;
    private const float NearForTalk = 1600f;
    private static readonly TimeSpan TalkingFor = TimeSpan.FromSeconds(90);

    private sealed class State {
        public ChatPersona Persona = null!;
        public ChatMemory Memory = new();
        public DateTime LastSpoke;
        public DateTime BackAt;                  // "brb": away until then
        public ulong TalkingWith;
        public DateTime TalkingUntil;
    }

    private readonly string _zone;
    private readonly List<AmbientWizard> _wizards;
    private readonly LineHistory _heard;
    private readonly Random _rng;
    private readonly Action<AmbientWizard, TimeSpan, Action<AmbientWizard>> _later;
    private readonly Func<AmbientWizard, ulong, (string Name, string Zone, string Quest), ChatContext> _context;
    private readonly Dictionary<ulong, State> _state = [];
    private readonly Dictionary<ulong, (Vector3 At, DateTime Still)> _players = [];
    private readonly Dictionary<ulong, DateTime> _greeted = [];
    private readonly Queue<string> _recent = new();
    private ChatRhythm _rhythm;
    private DateTime _nextGreeting;
    private DateTime _lastAnswer;
    private (ulong Speaker, string Text, DateTime At) _lastLine;

    public AmbientChatter(string zone, List<AmbientWizard> wizards, LineHistory heard, Random rng,
                          Action<AmbientWizard, TimeSpan, Action<AmbientWizard>> later,
                          Func<AmbientWizard, ulong, (string Name, string Zone, string Quest), ChatContext> context) {
        _zone = zone;
        _wizards = wizards;
        _heard = heard;
        _rng = rng;
        _later = later;
        _context = context;
        _rhythm = new ChatRhythm(rng, DateTime.UtcNow);
    }

    /// <summary>The optional language model, shared by every zone (null: rule lines only).</summary>
    internal static AmbientLlmClient Llm { get; set; }

    private State StateOf(AmbientWizard wizard) {
        if (!_state.TryGetValue(wizard.CharId, out var state)) {
            _state[wizard.CharId] = state = new State { Persona = ChatPersona.For(wizard.Identity) };
        }

        return state;
    }

    private static bool Free(AmbientWizard w)
        => w.Present && w.Activity is AmbientActivity.Idle or AmbientActivity.Walking or AmbientActivity.Shopping or AmbientActivity.Following;

    private static bool Busy(AmbientWizard w) => w.Activity is AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Helping;

    private ChatMoment MomentOf(AmbientWizard w) => w.Activity switch {
        AmbientActivity.Shopping => ChatMoment.Shopping,
        AmbientActivity.Following => ChatMoment.Following,
        AmbientActivity.Walking when w.DuelSigil == ulong.MaxValue => ChatMoment.Hunting,
        _ => AmbientLinePool.BossesFor(_zone).Length > 0 && _rng.NextDouble() < 0.15 ? ChatMoment.BossDoor : ChatMoment.Idle,
    };

    // ---- on their own ---------------------------------------------------------------------------

    /// <summary>Called every zone tick: an unprompted line or talk when the rhythm says so and players are here.</summary>
    public void Tick(DateTime now, IReadOnlyList<ulong> audience) {
        foreach (var (id, away) in _state) {
            if (away.BackAt != default && now >= away.BackAt) {
                away.BackAt = default;
                if (audience.Count > 0 && _wizards.FirstOrDefault(w => w.CharId == id) is { Present: true } back) {
                    Send(back, away, now, back.Name.Length % 2 == 0 ? "back" : "im back", TimeSpan.FromSeconds(1));
                }
            }
        }

        if (!_rhythm.Due(now)) {
            return;
        }

        if (!_rhythm.Spent(now) || audience.Count == 0 || !AmbientWizards.Settings.Chat) {
            return;
        }

        var present = _wizards.Where(w => w.Present).ToList();
        var candidates = present.Select(w => {
            var s = StateOf(w);
            return (w.Identity.Temper, s.LastSpoke, Free(w) && s.BackAt == default);
        }).ToList();
        var pick = AmbientChatPlanner.PickSpeaker(candidates, now, _rng);
        if (pick < 0) {
            return;
        }

        var wizard = present[pick];
        var state = StateOf(wizard);
        var speaker = Speaker(wizard, state, 0, default);
        var roll = _rng.NextDouble();

        // Two wizards talk (a busy spell more often).
        if (roll < (_rhythm.Busy ? 0.4 : 0.25) && state.Persona.Channel != ChatChannel.Menu) {
            var partner = present.Where(w => w != wizard && Free(w) && StateOf(w).BackAt == default
                                             && StateOf(w).Persona.Channel != ChatChannel.Menu
                                             && now - StateOf(w).LastSpoke > TimeSpan.FromSeconds(40))
                .OrderBy(w => Distance(w.Position, wizard.Position)).FirstOrDefault();
            if (partner is not null) {
                var plan = AmbientChatPlanner.Exchange(speaker, Speaker(partner, StateOf(partner), 0, default), _rng,
                    ChatWordFilter.Current, _heard);
                if (plan.Count > 0) {
                    var at = TimeSpan.Zero;
                    foreach (var line in plan) {
                        at += line.After;
                        var who = line.Speaker == 0 ? wizard : partner;
                        Schedule(who, StateOf(who), now, line.Text, at);
                    }

                    return;
                }
            }
        }

        // "brb": a few minutes standing still, then "back".
        if (roll > 0.96 && wizard.Activity == AmbientActivity.Idle && !wizard.Moving && state.Persona.Channel != ChatChannel.Menu) {
            var away = TimeSpan.FromSeconds(90 + _rng.Next(180));
            state.BackAt = now + away + TimeSpan.FromSeconds(3);
            wizard.Until = state.BackAt;
            Send(wizard, state, now, _rng.Next(3) == 0 ? "brb dinner" : "brb", TimeSpan.FromSeconds(1 + _rng.NextDouble()));
            return;
        }

        string text = null;
        if (Llm is { Enabled: true } llm && state.Persona.Channel != ChatChannel.Menu) {
            var key = $"{_zone}|{speaker.Moment}|{wizard.Identity.School}|{state.Persona.Spelling}|{state.Persona.Grownup}";
            if (_rng.NextDouble() < 0.5 && llm.TryTake(key, out var generated)) {
                text = generated;
                Logger.Debug("Ambient wizard {Name} uses a model line: {Text}", Logger.Args(wizard.Name, text));
            }

            llm.Refill(key, AmbientLlmPrompt.System,
                AmbientLlmPrompt.User(state.Persona, wizard.Identity.School, speaker.Context.ZoneName, speaker.Moment, [.. _recent], null),
                state.Persona, ChatWordFilter.Current);
        }

        if (text is null) {
            var planned = AmbientChatPlanner.Solo(speaker, _rng, ChatWordFilter.Current, DateTime.Now.DayOfWeek);
            text = planned?.Text;
        }

        if (text is not null) {
            Schedule(wizard, state, now, text, ChatTiming.Typing(text, state.Persona, _rng));
        }
    }

    /// <summary>A wizard just arrived in the zone while players are here: sometimes a "hi everyone".</summary>
    public void Arrived(AmbientWizard wizard, IReadOnlyList<ulong> audience) {
        var state = StateOf(wizard);
        if (audience.Count == 0 || !AmbientWizards.Settings.Chat || _rng.NextDouble() > 0.2) {
            return;
        }

        var speaker = Speaker(wizard, state, 0, default) with { Moment = ChatMoment.Arrived };
        if (AmbientChatPlanner.Solo(speaker, _rng, ChatWordFilter.Current) is { } line) {
            Schedule(wizard, state, DateTime.UtcNow, line.Text, TimeSpan.FromSeconds(3) + line.After);
        }
    }

    // ---- players --------------------------------------------------------------------------------

    /// <summary>Every census (5 s): greets a player who stopped near a free wizard, now and then.</summary>
    public void Census(DateTime now, IReadOnlyList<ulong> audience) {
        foreach (var gone in _players.Keys.Where(id => !audience.Contains(id)).ToList()) {
            _players.Remove(gone);
        }

        if (!AmbientWizards.Settings.Chat) {
            return;
        }

        foreach (var player in audience) {
            if (!ActiveWizardDirectory.TryGetByCharId(player, out var who) || who.Zone != _zone || who.IsInDuel) {
                continue;
            }

            var at = who.Location;
            var still = _players.TryGetValue(player, out var last) && Distance(last.At, at) < 120 ? last.Still : now;
            _players[player] = (at, still);
            if (now - still < TimeSpan.FromSeconds(4) || now < _nextGreeting
                || (_greeted.TryGetValue(player, out var greetedAt) && now - greetedAt < TimeSpan.FromMinutes(10))) {
                continue;
            }

            var wizard = _wizards.Where(w => Free(w) && StateOf(w).BackAt == default && Distance(w.Position, at) < NearForGreeting)
                .OrderBy(w => Distance(w.Position, at)).FirstOrDefault();
            if (wizard is null) {
                continue;
            }

            var state = StateOf(wizard);
            var chance = wizard.Identity.Temper switch { AmbientTemper.Chatty => 0.6, AmbientTemper.Friendly => 0.4, _ => 0.2 };
            _greeted[player] = now; // decided once a visit, greeted or not
            if (_greeted.Count > 500) {
                _greeted.Clear();
            }

            if (_rng.NextDouble() > chance) {
                continue;
            }

            _nextGreeting = now.AddSeconds(60);
            var facts = AmbientKnowledge.Facts(player);
            var speaker = Speaker(wizard, state, player, facts);
            var pool = state.Persona.Channel == ChatChannel.Menu ? AmbientLinePool.MenuIdle.Take(3).ToArray()
                : speaker.Context.Friend is not null ? AmbientLines.FriendGreetings : AmbientLinePool.GreetNear;
            if (AmbientChatPlanner.Answer(pool, speaker, _rng, ChatWordFilter.Current) is { } line) {
                state.TalkingWith = player;
                state.TalkingUntil = now + TalkingFor;
                Schedule(wizard, state, now, line, TimeSpan.FromSeconds(1 + _rng.NextDouble() * 2) + ChatTiming.Typing(line, state.Persona, _rng));
            }
        }
    }

    /// <summary>
    /// A player's typed line (a Say heard by every wizard, or a whisper to <paramref name="wizard"/>): decides once for the
    /// zone who answers, and schedules the answer. True when an answer is on its way.
    /// </summary>
    public bool Heard(AmbientWizard wizard, ulong speakerId, string text, bool whisper) {
        var now = DateTime.UtcNow;
        if (!AmbientWizards.Settings.Chat || string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        if (!whisper) {
            if (_lastLine.Speaker == speakerId && _lastLine.Text == text && now - _lastLine.At < TimeSpan.FromSeconds(3)) {
                return false; // the same Say reaching the next wizard: already decided
            }

            _lastLine = (speakerId, text, now);
            Remember(AmbientLlmPrompt.Scrub(text));
            _rhythm.Bump(now);
        }

        var at = ActiveWizardDirectory.TryGetByCharId(speakerId, out var who) ? who.Location : (Vector3?) null;
        AmbientWizard answerer;
        var addressed = true;
        if (whisper) {
            answerer = wizard;
        }
        else {
            answerer = _wizards.FirstOrDefault(w => w.Present && AmbientChatBrain.Mentions(text, w.Name.Split(' ')[0]))
                       ?? _wizards.FirstOrDefault(w => w.Present && StateOf(w).TalkingWith == speakerId && now < StateOf(w).TalkingUntil
                                                       && (at is null || Distance(w.Position, at.Value) < NearForTalk));
            if (answerer is null && AmbientChatBrain.IsOpenCall(text)) {
                // Chat reaches the whole zone: a wizard nearby is likelier to answer, but one down the street may too.
                addressed = false;
                var willing = _wizards.Where(w => w.Present && w.Activity != AmbientActivity.Away && StateOf(w).BackAt == default).ToList();
                var near = at is { } spot ? willing.Where(w => Distance(w.Position, spot) < NearForTalk).ToList() : [];
                var pool = near.Count > 0 ? near : willing;
                var chance = near.Count > 0 ? 0.6 : 0.35;
                if (pool.Count > 0 && _rng.NextDouble() < chance) {
                    var pick = AmbientChatPlanner.PickSpeaker(pool.Select(w => (w.Identity.Temper, DateTime.MinValue, true)).ToList(), now, _rng);
                    answerer = pick >= 0 ? pool[pick] : null;
                }
            }
        }

        if (answerer is null) {
            return false;
        }

        var state = StateOf(answerer);
        if (!answerer.Limiter.TryTake(now, speakerId)) {
            return false;
        }

        if (!whisper && addressed && ChatTiming.Ignores(state.Persona, _rng)) {
            return false; // people miss lines now and then
        }

        var facts = AmbientKnowledge.Facts(speakerId);
        var speaker = Speaker(answerer, state, speakerId, facts);
        var intent = AmbientChatBrain.Understand(text, speaker.Context, state.Persona);
        if (intent is null && !addressed) {
            return false; // a line to everyone the wizard has nothing to say to
        }

        intent ??= AmbientChatBrain.Fallback;
        var me = new Dictionary<string, string>(intent.Extra ?? new Dictionary<string, string>()) { ["me"] = answerer.Name };
        var reply = state.Persona.Channel == ChatChannel.Menu
            ? (intent.Menu ?? AmbientLinePool.MenuReply)[_rng.Next((intent.Menu ?? AmbientLinePool.MenuReply).Count)]
            : AmbientChatPlanner.Answer(intent.Pool, speaker, _rng, ChatWordFilter.Current, me);
        if (reply is null) {
            return false;
        }

        // The model may word it better; it has until the wizard would have finished typing.
        Task<string> generated = null;
        if (Llm is { Enabled: true } llm && state.Persona.Channel != ChatChannel.Menu && intent.Name is "fallback" or "how" or "doing" or "greet") {
            generated = llm.Reply(AmbientLlmPrompt.System,
                AmbientLlmPrompt.User(state.Persona, answerer.Identity.School, speaker.Context.ZoneName, ChatMoment.Idle, [.. _recent], text),
                state.Persona, ChatWordFilter.Current);
        }

        state.TalkingWith = speakerId;
        state.TalkingUntil = now + TalkingFor;
        var due = Stagger(now + ChatTiming.Answer(text, reply, state.Persona, Busy(answerer), _rng));
        state.LastSpoke = due;
        _later(answerer, due - now, w => {
            var line = generated is { IsCompletedSuccessfully: true, Result: { } better } ? better : reply;
            if (!ReferenceEquals(line, reply)) {
                Logger.Debug("Ambient wizard {Name} answers with a model line: {Text}", Logger.Args(w.Name, line));
            }

            Remember(line);
            if (whisper) {
                AmbientChat.Whisper(w, speakerId, line);
            }
            else {
                AmbientChat.Say(w, line);
            }
        });
        return true;
    }

    /// <summary>When an answer due at <paramref name="due"/> goes out, at least two seconds after the zone's last one.</summary>
    public DateTime Stagger(DateTime due) {
        _lastAnswer = ChatTiming.Stagger(due, _lastAnswer, _rng);
        return _lastAnswer;
    }

    // ---- fights ---------------------------------------------------------------------------------

    /// <summary>After a duel: "gg"/"ty for the help" with players, "aw man" after a defeat.</summary>
    public void AfterDuel(AmbientWizard wizard, bool won, IReadOnlyList<ulong> players) {
        if (!AmbientWizards.Settings.Chat || players.Count == 0 || !wizard.Limiter.TryTake(DateTime.UtcNow)) {
            return;
        }

        var state = StateOf(wizard);
        var player = players[_rng.Next(players.Count)];
        var facts = AmbientKnowledge.Facts(player);
        var speaker = Speaker(wizard, state, player, facts) with { Moment = won ? ChatMoment.AfterWin : ChatMoment.AfterLoss };
        string line;
        if (won && state.Persona.Channel != ChatChannel.Menu && _rng.NextDouble() < 0.35) {
            line = AmbientChatPlanner.Answer(["ty for the help {name}", "thanks {name}", "gg {name}", "nice job {name}", "ty {name}"],
                speaker, _rng, ChatWordFilter.Current);
        }
        else {
            line = AmbientChatPlanner.Solo(speaker, _rng, ChatWordFilter.Current)?.Text;
        }

        if (line is not null) {
            Schedule(wizard, state, DateTime.UtcNow, line, TimeSpan.FromSeconds(1.5 + _rng.NextDouble() * 2) + ChatTiming.Typing(line, state.Persona, _rng));
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private ChatSpeaker Speaker(AmbientWizard wizard, State state, ulong speaker, (string Name, string Zone, string Quest) facts)
        => new(state.Persona, _context(wizard, speaker, facts), MomentOf(wizard), state.Memory);

    private void Schedule(AmbientWizard wizard, State state, DateTime now, string text, TimeSpan after) {
        state.LastSpoke = now + after;
        _later(wizard, after, w => {
            if (!w.Present || !w.Limiter.TryTake(DateTime.UtcNow)) {
                return; // it left, or it would be talking too much
            }

            Remember(text);
            AmbientChat.Say(w, text);
        });
    }

    private void Send(AmbientWizard wizard, State state, DateTime now, string text, TimeSpan after) => Schedule(wizard, state, now, text, after);

    private void Remember(string line) {
        _recent.Enqueue(line);
        while (_recent.Count > 6) {
            _recent.Dequeue();
        }
    }

    private static float Distance(Vector3 a, Vector3 b) {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

}
