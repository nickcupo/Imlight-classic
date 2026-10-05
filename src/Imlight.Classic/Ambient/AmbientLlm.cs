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
 * AMBIENT LLM (OPTIONAL, OFF BY DEFAULT)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner, 2026-10-04: "if we have bandwidth on the server for a
 * small llm that might be cool"): ambient wizards may take some lines
 * from a small local language model (llama.cpp's llama-server with a
 * ~1B instruct model, deploy/linux/llm). Off unless [Classic]
 * AmbientWizardLlm = true. The game never waits for it:
 *   - One fixed system prompt (kept in llama-server's prompt cache); the
 *     persona, place and recent chat go in the short user message.
 *   - Unprompted lines come from a small per-context cache that a
 *     background task refills (one request at a time, a per-minute cap);
 *     an empty cache means the rule-based line (AmbientLinePool).
 *   - A reply is asked for when the player's line arrives; the wizard's
 *     reading and typing time (several seconds) is the deadline: if the
 *     model has not answered by then, the rule-based reply goes out.
 *   - Every request has a hard timeout and a token cap; after three
 *     failures in a row the client rests for five minutes.
 *   - Output is post-filtered: one line, 2-60 characters, plain letters
 *     and punctuation, no anachronisms (s_era) or AI and unsafe talk (s_unsafe), IsClean,
 *     and the client's chat dictionary (ChatWordFilter) when loaded;
 *     anything else is thrown away.
 *   - Only a loopback or private-network endpoint is accepted, so no
 *     line or player text leaves the server's network; player names are
 *     never put in a prompt ({name} is filled in afterwards).
 *
 * USAGE EXAMPLE:
 * var llm = new AmbientLlmClient(AmbientLlmSettings.Parse("true", "http://127.0.0.1:8088", "", "", ""));
 * if (llm.TryTake(key, out var line)) Say(line); else { llm.Refill(key, prompt); Say(ruleLine); }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Classic.Ambient;

/// <summary>The [Classic] AmbientWizardLlm* switches (see the file header).</summary>
/// <param name="Enabled">On only when asked for and the endpoint is local.</param>
/// <param name="Endpoint">llama-server's base address.</param>
/// <param name="Timeout">The hard limit for one request.</param>
/// <param name="PerMinute">Requests a minute, at most.</param>
/// <param name="MaxTokens">Tokens a line may have.</param>
/// <param name="Why">Why it is off, when it is.</param>
public sealed record AmbientLlmSettings(bool Enabled, Uri? Endpoint, TimeSpan Timeout, int PerMinute, int MaxTokens, string Why) {

    public const string DefaultEndpoint = "http://127.0.0.1:8088";

    public static AmbientLlmSettings Off { get; } = new(false, null, TimeSpan.FromSeconds(4), 0, 0, "off");

    /// <summary>Parses the settings; anything doubtful means off.</summary>
    public static AmbientLlmSettings Parse(string? enabled, string? url, string? timeoutMs, string? perMinute, string? maxTokens) {
        if (!bool.TryParse((enabled ?? "").Trim(), out var on) || !on) {
            return Off;
        }

        var text = string.IsNullOrWhiteSpace(url) ? DefaultEndpoint : url.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) {
            return Off with { Why = $"AmbientWizardLlmUrl '{text}' is not an http address" };
        }

        if (!IsLocal(endpoint)) {
            return Off with { Why = $"AmbientWizardLlmUrl '{text}' is not on this machine or the private network; nothing may leave it" };
        }

        return new AmbientLlmSettings(true, endpoint,
            TimeSpan.FromMilliseconds(Int(timeoutMs, 5000, 500, 15000)), Int(perMinute, 6, 1, 60), Int(maxTokens, 24, 8, 64), "on");
    }

    /// <summary>True for localhost, a loopback address or a private (RFC 1918 / unique local) address.</summary>
    public static bool IsLocal(Uri endpoint) {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.IsLoopback || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var ip)) {
            return false; // a name could resolve anywhere
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6) {
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || IPAddress.IsLoopback(ip);
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static int Int(string? text, int fallback, int min, int max)
        => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max) : fallback;

}

/// <summary>The prompt and the output filter (pure; see the file header).</summary>
public static class AmbientLlmPrompt {

    // Later worlds, features and slang (after May 2010); the game had none of them.
    private static readonly HashSet<string> s_era = new(StringComparer.OrdinalIgnoreCase) {
        "celestia", "zafaria", "avalon", "azteca", "khrysalis", "polaris", "mirage", "empyrea", "karamelle", "lemuria", "novus",
        "wysteria", "darkmoor", "arcanum", "aquila", "gardening", "fishing", "jewel", "jewels", "socket", "sockets", "socketing",
        "astral", "derby", "pirate101", "youtube", "discord", "tiktok", "instagram", "twitter", "iphone", "ipad", "selfie",
        "meme", "memes", "rizz", "sus", "yeet", "bruh", "lit", "fam", "bae", "yolo", "swag", "poggers", "pog", "skibidi", "emoji",
        "smartphone", "minecraft", "fortnite", "roblox", "streamer", "stream", "vlog", "wifi", "texting",
    };

    // Things a fake 2009 kid must never say: AI or server talk, personal data, the web, KingsIsle, and words a kids' game
    // would not show.
    private static readonly HashSet<string> s_unsafe = new(StringComparer.OrdinalIgnoreCase) {
        "ai", "bot", "robot", "model", "assistant", "chatgpt", "language", "prompt", "npc", "server", "password", "address",
        "phone", "email", "kingsisle", "website", "http", "www", "kill", "die", "dead", "blood", "hate", "stupid", "dumb", "idiot",
        "shut", "sucks", "crap", "damn", "hell", "noob", "girlfriend", "boyfriend", "date", "kiss", "age", "irl", "facebook",
        "henchman", "henchmen", "crafting", "craft", "garden", "fish",
    };

    private static readonly string[] s_eraPhrases = ["sun school", "star school", "moon school", "shadow magic", "pet derby"];

    /// <summary>True when <paramref name="line"/> names nothing from after May 2010.</summary>
    public static bool InEra(string? line) {
        var lower = (line ?? "").ToLowerInvariant();
        return !Regex.Matches(lower, @"[a-z0-9']+").Any(m => s_era.Contains(m.Value.Trim('\'')))
               && !s_eraPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// The system prompt, the same for every wizard so llama-server keeps it in its prompt cache (on the N100 the
    /// prompt costs more than the answer); who the wizard is goes in the user message.
    /// </summary>
    public const string System =
        "You play a kid in the online game Wizard101 in the year 2009 and type one short chat line. "
        + "Write 3 to 10 words the way kids typed in game chat. No numbers. You are a player, not a helper. "
        + "Never mention anything outside the game or after 2009. Only Wizard City, Krokotopia, Marleybone, MooShu, "
        + "Dragonspyre and Grizzleheim exist. Be friendly. Examples of how players type: \"anyone wanna quest\", "
        + "\"lol nice hat\", \"i need more gold\", \"ty\", \"ice is the best school\", \"brb dinner\". "
        + "Reply with the chat line only.";

    /// <summary>The user prompt: who the wizard is, what was said around (no names), and what to answer when spoken to.</summary>
    public static string User(ChatPersona persona, AmbientSchool school, string zoneName, ChatMoment moment, IReadOnlyList<string> recent,
                              string? heard) {
        ArgumentNullException.ThrowIfNull(persona);
        var sb = new StringBuilder();
        sb.Append(persona.Grownup ? "You are a parent playing with your kid" : "You are a kid")
          .Append($", a {AmbientChatBrain.SchoolName(school)} wizard in {Scrub(zoneName)}, {Doing(moment)}. ")
          .Append(persona.Spelling switch {
              ChatSpelling.Neat => "You write full words with capitals.",
              ChatSpelling.Sloppy => "You write all lowercase, no punctuation, lol, plz, wanna, idk, ty.",
              ChatSpelling.Excited => "You write lowercase and excited.",
              _ => "You write lowercase.",
          }).Append('\n');
        if (recent.Count > 0) {
            sb.Append("Chat nearby: ").Append(string.Join(" / ", recent.TakeLast(3).Select(Scrub))).Append('\n');
        }

        sb.Append(heard is null ? "Your line:" : $"Someone says: \"{Scrub(heard)}\"\nYour answer:");
        return sb.ToString();
    }

    // Prompt echoes and helper talk a small model slips into.
    private static readonly string[] s_badPhrases = [
        "help you", "assist", "chat nearby", "your line", "your answer", "someone says", "a player", "how may i", "how can i",
        "as a kid", "in the year", "wizard101",
    ];

    /// <summary>
    /// The model's output as a chat line, or null when it is not fit: first line only, quotes and "Name:" removed, 2-60
    /// characters, plain characters, nothing blocked, clean, through the chat dictionary when loaded, typed in the
    /// persona's style.
    /// </summary>
    public static string? Clean(string? raw, ChatPersona persona, ChatWordFilter? filter, Random rng) {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(rng);
        if (string.IsNullOrWhiteSpace(raw)) {
            return null;
        }

        var line = raw.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        line = Regex.Replace(line, @"^[A-Za-z ]{1,24}:\s+", "");          // "Ryan: ..."
        line = line.Trim().Trim('"', '\'', '“', '”', '*', '-', ' ');
        line = Regex.Replace(line, @"\s+", " ");
        if (line.Length < 2 || line.Length > 60 || line.Any(char.IsDigit)) {
            return null;
        }

        var words = Regex.Matches(line.ToLowerInvariant(), @"[a-z']+").Select(m => m.Value.Trim('\'')).ToList();
        if (words.Count == 0 || words.Count > 14 || words.Any(s_unsafe.Contains) || !InEra(line)
            || s_badPhrases.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase))) {
            return null;
        }

        var styled = ChatStyle.Apply(persona.Spelling == ChatSpelling.Neat ? line : line.ToLowerInvariant(), persona, rng, filter);
        if (!AmbientChatBrain.IsClean(styled)) {
            return null;
        }

        return filter is null || filter.Passes(styled) ? styled : null;
    }

    /// <summary>A player's line for the prompt: letters, spaces and plain punctuation only, at most 80 characters.</summary>
    public static string Scrub(string text)
        => new string((text ?? "").Where(c => char.IsLetterOrDigit(c) || " .,!?'".Contains(c)).Take(80).ToArray());

    private static string Doing(ChatMoment moment) => moment switch {
        ChatMoment.Hunting => "fighting monsters for a quest",
        ChatMoment.Shopping => "looking at the shop",
        ChatMoment.AfterWin => "who just won a fight",
        ChatMoment.AfterLoss => "who just lost a fight",
        ChatMoment.BossDoor => "looking for help with a boss",
        ChatMoment.Following => "following a friend",
        _ => "standing around",
    };

}

/// <summary>The llama-server client (see the file header). Thread-safe; nothing in it blocks the caller.</summary>
public sealed class AmbientLlmClient : IDisposable {

    /// <summary>Lines kept per context.</summary>
    public const int CachePerKey = 4;

    private readonly AmbientLlmSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _recent = new();
    private readonly object _gate = new();
    private int _failures;
    private DateTime _restUntil;

    /// <summary>Requests sent, lines kept, lines thrown away by the filter, failures (for the PERF log).</summary>
    public long Requests, Kept, Rejected, Failures;

    public AmbientLlmClient(AmbientLlmSettings settings, HttpMessageHandler? handler = null) {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = settings.Timeout;
        if (settings.Endpoint is not null) {
            _http.BaseAddress = settings.Endpoint;
        }
    }

    public bool Enabled => _settings.Enabled;

    /// <summary>A cached line for <paramref name="key"/>, if one is ready.</summary>
    public bool TryTake(string key, out string line) {
        line = "";
        return Enabled && _cache.TryGetValue(key, out var queue) && queue.TryDequeue(out line!);
    }

    /// <summary>Starts a background request for <paramref name="key"/> when its cache is low and the rate allows.</summary>
    public void Refill(string key, string system, string user, ChatPersona persona, ChatWordFilter? filter) {
        if (!Enabled || (_cache.TryGetValue(key, out var queue) && queue.Count >= CachePerKey) || !TakeRate()) {
            return;
        }

        _ = Task.Run(async () => {
            var line = await Generate(system, user, persona, filter, CancellationToken.None).ConfigureAwait(false);
            if (line is not null) {
                var q = _cache.GetOrAdd(key, _ => new ConcurrentQueue<string>());
                if (q.Count < CachePerKey && !q.Contains(line)) {
                    q.Enqueue(line);
                }
            }
        });
    }

    /// <summary>
    /// A reply, as a task the caller checks when its own typing time is up (never awaited on the game's threads); null
    /// when off, rate-limited, failed or filtered out.
    /// </summary>
    public Task<string?> Reply(string system, string user, ChatPersona persona, ChatWordFilter? filter)
        => Enabled && TakeRate() ? Task.Run(() => Generate(system, user, persona, filter, CancellationToken.None)) : Task.FromResult<string?>(null);

    /// <summary>One request: the model's line through <see cref="AmbientLlmPrompt.Clean"/>, or null.</summary>
    public async Task<string?> Generate(string system, string user, ChatPersona persona, ChatWordFilter? filter, CancellationToken cancel) {
        if (!Enabled || DateTime.UtcNow < _restUntil || !await _oneAtATime.WaitAsync(_settings.Timeout, cancel).ConfigureAwait(false)) {
            return null;
        }

        try {
            Interlocked.Increment(ref Requests);
            var body = JsonSerializer.Serialize(new {
                messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
                max_tokens = _settings.MaxTokens,
                temperature = 0.9,
                top_p = 0.9,
                stop = new[] { "\n" },
                cache_prompt = true,
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(_settings.Timeout);
            using var response = await _http.PostAsync("v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"),
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            _failures = 0;
            var line = AmbientLlmPrompt.Clean(text, persona, filter, new Random());
            if (line is null) {
                Interlocked.Increment(ref Rejected);
            }
            else {
                Interlocked.Increment(ref Kept);
            }

            return line;
        }
        catch (Exception) {
            Interlocked.Increment(ref Failures);
            if (Interlocked.Increment(ref _failures) >= 3) {
                _restUntil = DateTime.UtcNow.AddMinutes(5); // the service is down or slow: rest, the rule lines carry on
                _failures = 0;
            }

            return null;
        }
        finally {
            _oneAtATime.Release();
        }
    }

    private bool TakeRate() {
        lock (_gate) {
            var now = DateTime.UtcNow;
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1)) {
                _recent.Dequeue();
            }

            if (_recent.Count >= _settings.PerMinute || now < _restUntil) {
                return false;
            }

            _recent.Enqueue(now);
            return true;
        }
    }

    public void Dispose() {
        _http.Dispose();
        _oneAtATime.Dispose();
    }

}
