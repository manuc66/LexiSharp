using System;
using System.Collections.Generic;
using LexiSharp.Core;

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
    /// <remarks>
    /// The plate carries the per-window bounds when it was decoded from a segment, so a cached term
    /// keeps everything a pruning traversal reads and a second query does not lose the pruning.
    /// </remarks>
    public bool TryGet(string term, out PostingArrays postings)
    {
        lock (_byTerm)
        {
            if (_byTerm.TryGetValue(term, out var entry))
            {
                _recency.Remove(term);
                _recency.AddLast(term);
                postings = entry.Postings;

                return true;
            }
        }

        postings = PostingArrays.Empty;

        return false;
    }

    /// <summary>Stores the term's decoded postings, evicting the least-recently-used while over budget.</summary>
    public void Store(string term, PostingArrays postings)
    {
        lock (_byTerm)
        {
            if (_byTerm.TryGetValue(term, out var existing))
            {
                if (ReferenceEquals(existing.Postings.Ordinals, postings.Ordinals))
                    return;

                _entries -= existing.Postings.Count;
                _byTerm.Remove(term);
                _recency.Remove(term);
            }

            _byTerm[term] = new Entry(postings);
            _recency.AddLast(term);
            _entries += postings.Count;

            while (_entries > _maxEntries && _recency.First is { } oldest)
            {
                string evicted = oldest.Value;

                if (_byTerm.Remove(evicted, out var removed))
                    _entries -= removed.Postings.Count;

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

    /// <summary>
    /// Posting entries this cache may hold. A caller that would have to build arrays before knowing
    /// whether they fit asks this first, because a term larger than the budget is evicted as soon as
    /// it is stored — the allocation would be paid per query and retained by nothing.
    /// </summary>
    public int MaxEntries => _maxEntries;

    private sealed record Entry(PostingArrays Postings);
}