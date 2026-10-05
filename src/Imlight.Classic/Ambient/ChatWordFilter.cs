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
 * CHAT WORD FILTER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the game's own chat word lists, so an ambient wizard never says
 * a word the client would hide. The client filters received chat with
 * Root.wad:ChatFilter/*.txt (UTF-16): WhiteListBase is the "Wizard
 * Dictionary" of filtered text chat (2009: kids saw only dictionary words),
 * BlackListBase and CarlinListBase are refused words and phrases for
 * everyone. A line passes when every word is in the dictionary and no
 * black-listed word or phrase is in it, so it reads the same at every chat
 * level. The server reads the lists from the install's Root.wad at start
 * (they are KingsIsle's files: read at run time, never bundled); without
 * them only AmbientChatBrain.IsClean applies.
 *
 * Words are split as the client does: letters, digits and apostrophes;
 * an inner hyphen joins; everything else separates. Smileys listed in
 * CapitalLettersExceptions (":D", ":P") count as words that pass.
 * The dictionary has no numbers: 2009 filtered chat showed a number as
 * "..." (fan blogs, 2009), so most ambient wizards never type one. A
 * grown-up with 2009's 18+ open chat (June 2009) may give a level, which
 * Passes allows with allowNumbers (one or two digits).
 *
 * USAGE EXAMPLE:
 * var filter = ChatWordFilter.Load(name => RootArchive.Read(name));
 * if (filter.Passes("anyone wanna help w rattlebones")) ...
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Imlight.Classic.Ambient;

/// <summary>The client's chat word lists (see the file header).</summary>
public sealed class ChatWordFilter {

    /// <summary>The list files inside Root.wad.</summary>
    public static readonly string[] Files = [
        "ChatFilter/WhiteListBase.txt", "ChatFilter/WhiteListPhrasesBase.txt", "ChatFilter/BlackListBase.txt",
        "ChatFilter/CarlinListBase.txt", "ChatFilter/CapitalLettersExceptions.txt",
    ];

    private readonly HashSet<string> _dictionary = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blackWords = new(StringComparer.Ordinal);
    private readonly List<string[]> _blackPhrases = [];
    private readonly HashSet<string> _smileys = new(StringComparer.Ordinal);

    /// <summary>The filter the server loaded at start, or null (then only IsClean applies).</summary>
    public static ChatWordFilter? Current { get; set; }

    /// <summary>True once the dictionary has words.</summary>
    public bool HasDictionary => _dictionary.Count > 0;

    /// <summary>How many dictionary words were read.</summary>
    public int DictionarySize => _dictionary.Count;

    /// <summary>Reads the lists; <paramref name="read"/> returns a file's bytes (see <see cref="Files"/>) or null.</summary>
    public static ChatWordFilter Load(Func<string, byte[]?> read) {
        ArgumentNullException.ThrowIfNull(read);
        var filter = new ChatWordFilter();
        foreach (var word in Lines(read(Files[0]))) {
            filter._dictionary.Add(word.ToLowerInvariant());
        }

        foreach (var phrase in Lines(read(Files[1]))) {
            foreach (var word in Split(phrase)) {
                filter._dictionary.Add(word);
            }
        }

        foreach (var entry in Lines(read(Files[2])).Concat(Lines(read(Files[3])))) {
            var words = Split(entry);
            if (words.Length == 1) {
                filter._blackWords.Add(words[0]);
            }
            else if (words.Length > 1) {
                filter._blackPhrases.Add(words);
            }
        }

        foreach (var smiley in Lines(read(Files[4]))) {
            filter._smileys.Add(smiley.Trim());
        }

        return filter;
    }

    /// <summary>A filter from word lists (tests).</summary>
    public static ChatWordFilter FromLists(IEnumerable<string> dictionary, IEnumerable<string>? black = null,
                                           IEnumerable<string>? smileys = null) {
        var filter = new ChatWordFilter();
        foreach (var word in dictionary) {
            filter._dictionary.Add(word.ToLowerInvariant());
        }

        foreach (var entry in black ?? []) {
            var words = Split(entry);
            if (words.Length == 1) {
                filter._blackWords.Add(words[0]);
            }
            else if (words.Length > 1) {
                filter._blackPhrases.Add(words);
            }
        }

        foreach (var smiley in smileys ?? []) {
            filter._smileys.Add(smiley);
        }

        return filter;
    }

    /// <summary>True when the client would show <paramref name="line"/> whole at every chat level.</summary>
    /// <param name="line">The line.</param>
    /// <param name="allowNumbers">Let one- and two-digit numbers through (a grown-up's open chat; see the header).</param>
    public bool Passes(string? line, bool allowNumbers = false) => line is not null && Refused(line, allowNumbers).Count == 0;

    /// <summary>The words of <paramref name="line"/> the client would hide (empty when it passes).</summary>
    public IReadOnlyList<string> Refused(string line, bool allowNumbers = false) {
        var refused = new List<string>();
        var words = new List<string>();
        foreach (var token in Tokens(line ?? "")) {
            if (_smileys.Contains(token)) {
                continue;
            }

            var word = token.ToLowerInvariant().Replace('’', '\'');
            words.Add(word);
            if (allowNumbers && word.Length <= 2 && word.All(char.IsAsciiDigit)) {
                continue; // a level ("lvl 7"): the dictionary has no numbers, IsClean already refuses longer runs
            }

            if (_blackWords.Contains(word) || (HasDictionary && !_dictionary.Contains(word) && !_dictionary.Contains(word.TrimEnd('\'')))) {
                refused.Add(token);
            }
        }

        foreach (var phrase in _blackPhrases) {
            for (var start = 0; start + phrase.Length <= words.Count; start++) {
                var hit = true;
                for (var k = 0; k < phrase.Length && hit; k++) {
                    hit = words[start + k] == phrase[k];
                }

                if (hit) {
                    refused.Add(string.Join(' ', phrase));
                }
            }
        }

        return refused;
    }

    /// <summary>The words of a line as the client splits them; listed smileys stay whole.</summary>
    internal IEnumerable<string> Tokens(string line) {
        foreach (var chunk in line.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (_smileys.Contains(chunk)) {
                yield return chunk;
                continue;
            }

            var i = 0;
            while (i < chunk.Length) {
                if (!char.IsLetterOrDigit(chunk[i])) {
                    i++;
                    continue;
                }

                var start = i;
                while (i < chunk.Length && (char.IsLetterOrDigit(chunk[i]) || chunk[i] is '\'' or '’'
                                            || (chunk[i] == '-' && i + 1 < chunk.Length && char.IsLetterOrDigit(chunk[i + 1])))) {
                    i++;
                }

                yield return chunk[start..i].TrimEnd('\'', '’');
            }
        }
    }

    private static string[] Split(string text)
        => text.ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The lines of a UTF-16 (BOM) or UTF-8 text file, trimmed, without blanks.</summary>
    public static IEnumerable<string> Lines(byte[]? bytes) {
        if (bytes is null || bytes.Length == 0) {
            yield break;
        }

        var text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim('\r', ' ', '\t', '\0');
            if (line.Length > 0) {
                yield return line;
            }
        }
    }

}
