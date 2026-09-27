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
 * SHARED PLAYER STATE
 * ========================================================================
 *
 * PURPOSE:
 * A list that one actor may read while another adds to it. Every write
 * publishes a new array; a reader works on the array it started with.
 *
 * USAGE EXAMPLE:
 * wizard.InventoryBehavior.Items.Add(item);                // TutorialService's actor
 * var copy = wizard.InventoryBehavior.Items.ConvertAll(f); // ZoneService's actor
 *
 * NOTE:
 * Each service of a session is its own actor, and they share the Wizard.
 * Writes copy the whole array, so this suits short, read-mostly lists.
 * Equality is EqualityComparer<T>.Default, as List<T> uses.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Imlight.Classic.Collections;

/// <summary>
/// A list whose reads never observe a write in progress.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class CopyOnWriteList<T> : IReadOnlyList<T> {

    private readonly Lock _writeLock = new();
    private T[] _items = [];

    /// <summary>
    /// The number of elements now.
    /// </summary>
    public int Count => Volatile.Read(ref _items).Length;

    /// <summary>
    /// The element at <paramref name="index"/> now.
    /// </summary>
    /// <param name="index">A zero-based index.</param>
    public T this[int index] => Volatile.Read(ref _items)[index];

    /// <summary>
    /// Appends an element.
    /// </summary>
    /// <param name="item">The element.</param>
    public void Add(T item) {
        lock (_writeLock) {
            var current = _items;
            var next = new T[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = item;
            Volatile.Write(ref _items, next);
        }
    }

    /// <summary>
    /// Removes the first element equal to <paramref name="item"/>.
    /// </summary>
    /// <param name="item">The element.</param>
    /// <returns>True if an element was removed.</returns>
    public bool Remove(T item) {
        lock (_writeLock) {
            var current = _items;
            var index = Array.IndexOf(current, item);
            if (index < 0) {
                return false;
            }

            var next = new T[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref _items, next);

            return true;
        }
    }

    /// <summary>
    /// The first element that matches, or the default value.
    /// </summary>
    /// <param name="match">The condition.</param>
    /// <returns>The element, or the default value.</returns>
    public T? Find(Predicate<T> match) => Array.Find(Volatile.Read(ref _items), match);

    /// <summary>
    /// Converts one snapshot of the elements into a new list.
    /// </summary>
    /// <typeparam name="TOutput">The converted type.</typeparam>
    /// <param name="converter">The conversion.</param>
    /// <returns>The converted elements, in order.</returns>
    public List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter) {
        var snapshot = Volatile.Read(ref _items);
        var converted = new List<TOutput>(snapshot.Length);
        foreach (var item in snapshot) {
            converted.Add(converter(item));
        }

        return converted;
    }

    /// <summary>
    /// Enumerates one snapshot of the elements.
    /// </summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>) Volatile.Read(ref _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}
