using System;
using System.Collections.Generic;

namespace LexiSharp.Indexing;

/// <summary>
/// A bounded cache of decoded postings: a term's flat <c>(ordinal, frequency)</c> arrays, filled on
/// first demand and evicted least-recently-used once the entry budget is spent. A query that hits
/// walks the flat arrays exactly as the in-memory index does — the same ascending entry order, so the
/// accumulated sums are bit-identical to a decode from the compact form — and pays only the fold, not
/// the per-entry decode.
/// </summary>
/// <remarks>
/// <para>
/// The budget is denominated in total cached entries because that is what the memory costs: eight
/// bytes an entry (two <c>int[]</c>), against two to four in the compact form. The cache is the middle
/// of the measured spectrum — the compact segment without it (fast to hold, slower to query) and the
/// in-memory index's fat per-entry dictionaries (fast to query, ~15-30x the bytes) — and which point
/// on that spectrum serves a workload is a measured question, not a choice of taste.
/// </para>
/// <para>
/// Thread safety: one lock covers both the map and the recency list, so any number of concurrent
/// searches may miss, fill and evict without corrupting an entry. Filling twice for the same term is
/// possible (two misses racing); the second store simply replaces the first, and the duplicate work is
/// the only cost.
/// </para>
/// </remarks>
internal sealed class DecodedPostingsCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, Entry> _byTerm = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private int _entries;

    public DecodedPostingsCache(int maxEntries)
    {
        if (maxEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "a cache holds at least one entry");

        _maxEntries = maxEntries;
    }

    /// <summary>The term's decoded postings, or <c>false</c>. A hit refreshes the recency order.</summary>
    public bool TryGet(string term, out int[] ordinals, out int[] frequencies)
    {
        lock (_byTerm)
        {
            if (_byTerm.TryGetValue(term, out var entry))
            {
                _recency.Remove(term);
                _recency.AddLast(term);
                ordinals = entry.Ordinals;
                frequencies = entry.Frequencies;

                return true;
            }
        }

        ordinals = [];
        frequencies = [];

        return false;
    }

    /// <summary>Stores the term's decoded postings, evicting the least-recently-used while over budget.</summary>
    public void Store(string term, int[] ordinals, int[] frequencies)
    {
        lock (_byTerm)
        {
            if (_byTerm.TryGetValue(term, out var existing))
            {
                if (ReferenceEquals(existing.Ordinals, ordinals))
                    return;

                _entries -= existing.Ordinals.Length;
                _byTerm.Remove(term);
                _recency.Remove(term);
            }

            _byTerm[term] = new Entry(ordinals, frequencies);
            _recency.AddLast(term);
            _entries += ordinals.Length;

            while (_entries > _maxEntries && _recency.First is { } oldest)
            {
                string evicted = oldest.Value;

                if (_byTerm.Remove(evicted, out var removed))
                    _entries -= removed.Ordinals.Length;

                _recency.RemoveFirst();
            }
        }
    }

    /// <summary>Number of entries the cache holds, for the memory accounting that decides its budget.</summary>
    public int EntryCount
    {
        get
        {
            lock (_byTerm)
                return _entries;
        }
    }

    private sealed record Entry(int[] Ordinals, int[] Frequencies);
}