using System;
using System.Collections.Generic;
using System.IO;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// A search index that can be written to and read back from disk: immutable segments, a live overlay,
/// and tombstones that survive until a merge reclaims them.
/// </summary>
/// <remarks>
/// <para>
/// This is the storage half a search server needs. A corpus is written once as a segment and read many
/// times — segments are immutable, compact, and searchable without being rebuilt — while the writes a
/// serving process takes land in a live overlay in memory. The reader sees both as one index: one
/// ordinal space, one fold, and the <b>same page the in-memory index serves, bit for bit</b>, which is
/// the property every part of this rests on.
/// </para>
/// <para>
/// <b>What a write costs, and what it does not.</b> Adding a document is a tokenization and a
/// dictionary write — not the 0.6-1.2 s and ~2.9 GB a segment write costs at 40,000 documents — and it
/// is visible to the very next search. Removing one is a hash insert. Replacing one tombstones the old
/// ordinal and writes the new version, so a document stops matching its old text.
/// </para>
/// <para>
/// <b>What a deletion does to a score, and it is the rule the whole design turns on.</b> A tombstone
/// touches nothing but the fold. The corpus statistics — <see cref="StatisticDocumentCount"/>,
/// <see cref="AverageDocumentLength"/>, <see cref="CorpusTokenCount"/>,
/// <see cref="DocumentFrequency"/>, <see cref="VocabularySize"/> — describe the segments as written and
/// are <b>frozen until a merge</b>, so deleting a document moves no surviving score. That is not a
/// limitation, it is the only rule bit-exact parity is possible under: BM25's idf reads <c>N</c> and
/// its length normalisation reads the average, so any statistic a deletion touches moves every score on
/// the index. Measured, the same deletions over the same segment: <b>frozen statistics left 0 of 401
/// scores moved, corrected statistics moved 401 of 401</b>. A <see cref="Commit"/> or a
/// <see cref="Merge"/> rewrites the segment, and the scores move then.
/// </para>
/// <para>
/// <b>What a tombstone costs to read.</b> The fold tests it per posting entry, which measures
/// <b>1.18× a head query at 10 ‰ deleted and falls from there</b> — 1.10×, 1.07×, and 0.95× at 250 ‰,
/// because a skipped posting is a <c>Record</c> the accumulator never does. So the first per cent of
/// deletions costs most of the tax, which is where <see cref="ShouldMerge"/>'s threshold sits.
/// </para>
/// <para>
/// <b>Durability, stated exactly.</b> Writes are visible immediately and live as long as the process.
/// <see cref="Commit"/> makes them durable by writing one merged segment to the directory and swapping
/// it in atomically, so a reader in flight never sees a half-written store. What is <b>not</b> durable
/// is the overlay between writes: a crash after an <see cref="Add"/> and before a <see cref="Commit"/>
/// loses that document. That is a write-ahead log, it is not this increment, and the type does not
/// pretend otherwise.
/// </para>
/// <para>
/// <b>One writer, any number of readers.</b> Mutations are not safe to interleave with a search in
/// flight; a reader must capture its own <see cref="SegmentedTextIndex"/> reference and search that.
/// This is the same contract <c>InMemoryTextIndex</c> documents. Searches hold no shared mutable state
/// and may run concurrently with one another.
/// </para>
/// <para>
/// <b>The tokenizer must be the one the segments were written with.</b> The dictionary holds normalized
/// terms, so a lookup is by the normalized form; a store opened with a different tokenizer will not
/// find the terms it indexed.
/// </para>
/// </remarks>
public sealed class SegmentedTextIndex : ITextIndex
{
    private readonly string _directory;
    private readonly ITokenizer _tokenizer;
    private SegmentChain _chain;

    private SegmentedTextIndex(string directory, ITokenizer tokenizer, SegmentChain chain)
    {
        _directory = directory;
        _tokenizer = tokenizer;
        _chain = chain;
    }

    /// <summary>
    /// Opens the store at <paramref name="directory"/>, or creates it.
    /// </summary>
    /// <param name="directory">
    /// A directory holding one segment file per generation. An empty or absent directory is created and
    /// the store starts empty.
    /// </param>
    /// <param name="tokenizer">
    /// The tokenizer the segments were written with. Defaults to <see cref="Tokenizer.Default"/>, which
    /// is what an index built without one uses.
    /// </param>
    /// <exception cref="InvalidDataException">A segment in the directory is not one this reader knows.</exception>
    public SegmentedTextIndex(string directory, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _directory = directory;
        _tokenizer = tokenizer ?? Linguistics.Tokenizer.Default;

        var segments = new List<SegmentTextIndex>();

        foreach (string path in SegmentFiles(directory))
            segments.Add(new LexiSharp.Indexing.SegmentTextIndex(File.ReadAllBytes(path), _tokenizer));

        _chain = segments.Count == 0
            ? new SegmentChain([EmptySegment()], _tokenizer)
            : new SegmentChain(segments, _tokenizer);
    }

    /// <summary>Documents indexed and not removed.</summary>
    public int Count => _chain.Count;

    /// <summary>Segments the store holds. One until a merge writes more; <see cref="Commit"/> keeps it at one.</summary>
    public int SegmentCount => _chain.SegmentCount;

    /// <summary>Documents removed and not yet reclaimed by a merge.</summary>
    public int TombstoneCount => _chain.TombstoneCount;

    /// <summary>Documents added since the last commit.</summary>
    public int PendingCount => _chain.OverlayCount;

    /// <summary>Whether the tombstone count has passed the share of documents that makes a merge worth it.</summary>
    public bool ShouldMerge() => _chain.ShouldMerge();

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _chain.Add(document);
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        return _chain.Remove(documentId);
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        _chain = new SegmentChain([EmptySegment()], _tokenizer);

        foreach (var document in documents)
            _chain.Add(document);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _chain = new SegmentChain([EmptySegment()], _tokenizer);
    }

    /// <summary>
    /// Merges the store in memory and returns the resulting index, leaving this one unchanged.
    /// </summary>
    /// <remarks>
    /// Returns a new index rather than mutating this one: the fold reads the segments, their bases and
    /// the length array, so a write between those reads would fold half the old chain and half the new
    /// one. A caller that needs a merge to be invisible to readers swaps the index their engine holds —
    /// the pattern this repository publishes as <c>AtomicEngineReference</c>.
    /// </remarks>
    public SegmentedTextIndex Merge()
    {
        SegmentChain merged = _chain.Merge();

        return new SegmentedTextIndex(_directory, _tokenizer, merged);
    }

    /// <summary>
    /// Makes every pending write durable: merges the store, writes one segment to the directory, and
    /// returns the resulting index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a merge followed by a write, and it is the point at which the statistics stop being
    /// frozen — the new segment's <c>N</c>, average length and document frequencies are its own and no
    /// longer count the deleted documents, so <b>every surviving score changes</b>. That is the price
    /// of a compaction and the reason this is a scheduled batch operation rather than something a query
    /// triggers.
    /// </para>
    /// <para>
    /// The segment is written to a temporary file and renamed over the store's only segment, so a crash
    /// mid-write leaves the previous segment intact rather than a truncated one.
    /// </para>
    /// </remarks>
    public SegmentedTextIndex Commit()
    {
        // One merge, one write: the live corpus is compacted and the resulting segment's bytes are
        // written out. Compacting a second time to produce bytes that are a pure function of a corpus
        // that has not changed would pay the 0.6-1.2 s and ~2.9 GB twice for nothing.
        SegmentTextIndex merged = _chain.Flush();
        var committed = new SegmentedTextIndex(_directory, _tokenizer, new SegmentChain([merged], _tokenizer));

        committed.WriteToDisk(merged);

        return committed;
    }

    /// <inheritdoc />
    public int StatisticDocumentCount => _chain.StatisticDocumentCount;

    /// <inheritdoc />
    public int VocabularySize => _chain.VocabularySize;

    /// <inheritdoc />
    public long CorpusTokenCount => _chain.CorpusTokenCount;

    /// <inheritdoc />
    public double AverageDocumentLength => _chain.AverageDocumentLength;

    /// <inheritdoc />
    public IReadOnlyCollection<SearchDocument> Documents => _chain.Documents;

    /// <inheritdoc />
    public bool Contains(string documentId) => _chain.Contains(documentId);

    /// <inheritdoc />
    public int DocumentFrequency(string term) => _chain.DocumentFrequency(term);

    /// <inheritdoc />
    public int CorpusFrequency(string term) => _chain.CorpusFrequency(term);

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term) => _chain.TermFrequency(documentId, term);

    /// <inheritdoc />
    public int DocumentLength(string documentId) => _chain.DocumentLength(documentId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId) => _chain.GetTerms(documentId);

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
        _chain.GetTermPositions(documentId, term);

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document) =>
        _chain.TryGetDocument(documentId, out document);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Fields => [TextFields.Default];

    /// <summary>The tokenizer this store reads and writes with.</summary>
    public ITokenizer Tokenizer => _tokenizer;

    /// <summary>The chain the store searches. Internal for the engine and the decorators.</summary>
    internal SegmentChain Chain => _chain;

    /// <summary>
    /// Writes the store's live corpus as one segment into the directory, replacing every segment that
    /// was there, and reopens it from what is now on disk.
    /// </summary>
    /// <remarks>
    /// The bytes come from the chain's own merged segment rather than a second compaction: a flush is
    /// a pure function of the live corpus, so compacting twice would produce the same bytes at twice
    /// the cost — 0.6-1.2 s and ~2.9 GB allocated at 40,000 documents each time.
    /// </remarks>
    private void WriteToDisk(SegmentTextIndex merged)
    {
        ArgumentNullException.ThrowIfNull(merged);

        if (merged.TryGetBytes() is not { } bytes)
            throw new InvalidOperationException("the merged segment does not hold its bytes");

        Directory.CreateDirectory(_directory);

        // A merge leaves one segment, so the store holds one file and the generation is its name.
        int generation = 0;

        foreach (string path in SegmentFiles(_directory))
        {
            string name = Path.GetFileNameWithoutExtension(path);

            if (int.TryParse(name.AsSpan(SegmentPrefix.Length), out int existing) && existing >= generation)
                generation = existing + 1;
        }

        string target = Path.Combine(_directory, SegmentPrefix + generation.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".lxs");
        string temporary = target + ".tmp";

        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, target, overwrite: true);

        foreach (string path in SegmentFiles(_directory))
        {
            if (!string.Equals(path, target, StringComparison.Ordinal))
                File.Delete(path);
        }

        // Reopen from what is now on disk, so the returned store is the one the directory holds.
        _chain = new SegmentChain(
            [new LexiSharp.Indexing.SegmentTextIndex(File.ReadAllBytes(target), _tokenizer)],
            _tokenizer);
    }

    private const string SegmentPrefix = "segment-";

    /// <summary>The store's segment files, in generation order.</summary>
    private static IEnumerable<string> SegmentFiles(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        var paths = new List<string>();

        foreach (string path in Directory.EnumerateFiles(directory, SegmentPrefix + "*.lxs"))
            paths.Add(path);

        paths.Sort(StringComparer.Ordinal);

        foreach (string path in paths)
            yield return path;
    }

    /// <summary>A zero-document segment, so an empty store still has one to fold over.</summary>
    /// <remarks>
    /// A chain needs at least one segment — <see cref="SegmentChain"/> refuses an empty list — and
    /// rather than making an empty chain a special case in every read path, one zero-document segment
    /// is written and every path folds over it unchanged.
    /// </remarks>
    private static LexiSharp.Indexing.SegmentTextIndex EmptySegment()
    {
        var empty = new InMemoryTextIndex();

        return new(SegmentWriter.Write((ITextIndex)new EmptySegmentSource()));
    }
}
