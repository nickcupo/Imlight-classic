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
 * CLASSIC ZONE MAP
 * ========================================================================
 * 
 * PURPOSE:
 * A zone-name pattern from classic-data/zones: '/'-separated segments where
 * '*' matches any run of characters inside one segment.
 * 
 * USAGE EXAMPLE:
 * ZonePattern.Parse("WizardCity/Interiors/WC_Park_*").Matches(ZonePattern.SplitZone(zone))
 * 
 * NOTE:
 * A pattern matches a zone when it matches the zone's leading segments, so
 * "WizardCity" covers "WizardCity/WC_Hub" but not "WizardCityX/...".
 * Comparison ignores case. Specificity is (segment count, literal count).
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

namespace Imlight.Classic.Zones;

/// <summary>
/// A segment glob over zone names.
/// </summary>
public sealed class ZonePattern {

    private static readonly Regex s_valid = new(@"^[A-Za-z0-9_.*-]+(/[A-Za-z0-9_.*-]+)*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ImmutableArray<Segment> _segments;

    private ZonePattern(string text, ImmutableArray<Segment> segments) {
        Text = text;
        _segments = segments;
        LiteralCount = text.Count(c => c is not '*' and not '/');
    }

    /// <summary>
    /// The pattern as written.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The number of '/'-separated segments.
    /// </summary>
    public int SegmentCount => _segments.Length;

    /// <summary>
    /// The number of characters that are not '*' or '/'.
    /// </summary>
    public int LiteralCount { get; }

    /// <summary>
    /// True when <paramref name="text"/> is a valid pattern.
    /// </summary>
    /// <param name="text">The candidate pattern.</param>
    /// <returns>True if it matches the zones schema's pattern rule.</returns>
    public static bool IsValid(string? text)
        => text is not null && s_valid.IsMatch(text);

    /// <summary>
    /// Parses a pattern.
    /// </summary>
    /// <param name="text">The pattern text.</param>
    /// <returns>The parsed pattern.</returns>
    /// <exception cref="ArgumentException">The text is not a valid pattern.</exception>
    public static ZonePattern Parse(string text) {
        if (!IsValid(text)) {
            throw new ArgumentException($"'{text}' is not a valid zone pattern", nameof(text));
        }

        return new ZonePattern(text, [.. text.Split('/').Select(Segment.Create)]);
    }

    /// <summary>
    /// Splits a zone name into segments: trimmed, split on '/', empty segments dropped.
    /// </summary>
    /// <param name="zone">A zone name such as <c>WizardCity/WC_Hub</c>.</param>
    /// <returns>The segments; empty for a blank zone.</returns>
    public static ImmutableArray<string> SplitZone(string? zone)
        => string.IsNullOrWhiteSpace(zone)
            ? []
            : [.. zone.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// True when the pattern matches the leading segments of a zone.
    /// </summary>
    /// <param name="zoneSegments">The zone's segments (<see cref="SplitZone"/>).</param>
    /// <returns>True on a match.</returns>
    public bool Matches(IReadOnlyList<string> zoneSegments) {
        if (zoneSegments.Count < _segments.Length) {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++) {
            if (!_segments[i].Matches(zoneSegments[i])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the pattern matches the leading segments of <paramref name="zone"/>.
    /// </summary>
    /// <param name="zone">A zone name.</param>
    /// <returns>True on a match.</returns>
    public bool Matches(string? zone)
        => Matches(SplitZone(zone));

    /// <summary>
    /// Compares specificity: more segments first, then more literal characters.
    /// </summary>
    /// <param name="other">The pattern to compare with.</param>
    /// <returns>Positive when this pattern is more specific, negative when less, zero on a tie.</returns>
    public int CompareSpecificity(ZonePattern other) {
        var bySegments = SegmentCount.CompareTo(other.SegmentCount);

        return bySegments != 0 ? bySegments : LiteralCount.CompareTo(other.LiteralCount);
    }

    public override string ToString() => Text;

    private sealed class Segment {

        private readonly string _literal;
        private readonly Regex? _glob;

        private Segment(string literal, Regex? glob) {
            _literal = literal;
            _glob = glob;
        }

        public static Segment Create(string text) {
            if (!text.Contains('*')) {
                return new Segment(text, null);
            }

            var body = string.Join(".*", text.Split('*').Select(Regex.Escape));

            return new Segment(text, new Regex($"^{body}\\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        }

        public bool Matches(string zoneSegment)
            => _glob?.IsMatch(zoneSegment) ?? string.Equals(_literal, zoneSegment, StringComparison.OrdinalIgnoreCase);

    }

}
