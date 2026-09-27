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
 * CLASSIC DATA LOADING
 * ========================================================================
 * 
 * PURPOSE:
 * The error type for classic-data that does not load or validate. Every
 * error names the file, the key path and the line.
 * 
 * USAGE EXAMPLE:
 * catch (ClassicDataException ex) { foreach (var error in ex.Errors) { ... } }
 * 
 * NOTE:
 * File paths are relative to the classic-data directory's parent when the
 * file lives under a directory named classic-data.
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
using System.Globalization;
using System.Linq;

namespace Imlight.Classic;

/// <summary>
/// One problem found while loading classic-data.
/// </summary>
/// <param name="File">The file, relative to the classic-data parent when possible.</param>
/// <param name="KeyPath">The key path, such as <c>features.pets.energy</c> or <c>worlds[2]</c>; empty for the whole file.</param>
/// <param name="Line">The 1-based line, when known.</param>
/// <param name="Message">What is wrong.</param>
public sealed record ClassicDataError(string File, string KeyPath, int? Line, string Message) {

    /// <summary>
    /// Formats the error as <c>file:line key.path: message</c>.
    /// </summary>
    /// <returns>The formatted error.</returns>
    public override string ToString() {
        var location = Line is null ? File : $"{File}:{Line.Value.ToString(CultureInfo.InvariantCulture)}";

        return KeyPath.Length == 0 ? $"{location}: {Message}" : $"{location} {KeyPath}: {Message}";
    }

}

/// <summary>
/// Thrown when classic-data does not load or validate. Carries every error found.
/// </summary>
public sealed class ClassicDataException : Exception {

    /// <summary>
    /// Creates the exception from one or more errors.
    /// </summary>
    /// <param name="errors">The errors, in the order they were found.</param>
    public ClassicDataException(IEnumerable<ClassicDataError> errors)
        : this([.. errors]) { }

    /// <summary>
    /// Creates the exception from a single error.
    /// </summary>
    /// <param name="error">The error.</param>
    public ClassicDataException(ClassicDataError error)
        : this(ImmutableArray.Create(error)) { }

    private ClassicDataException(ImmutableArray<ClassicDataError> errors)
        : base(FormatMessage(errors)) {
        Errors = errors;
    }

    /// <summary>
    /// Every error found, in order.
    /// </summary>
    public ImmutableArray<ClassicDataError> Errors { get; }

    private static string FormatMessage(ImmutableArray<ClassicDataError> errors)
        => errors.Length switch {
            0 => "classic-data failed to load.",
            1 => errors[0].ToString(),
            _ => $"{errors.Length} classic-data errors:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors.Select(error => "  " + error)),
        };

}
