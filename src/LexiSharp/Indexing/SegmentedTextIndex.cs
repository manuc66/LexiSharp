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
/// dictionary write — not the about 0.9 s and 126 MB a segment write costs at 40,000 documents — and it
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
/// <b>Durability, stated exactly.</b> Every write is appended to a <b>write-ahead journal</b> beside the
/// segments and replayed when the store opens, so a crash loses at most the entries the flush policy
/// has not forced to the device — a hundred writes or fifty milliseconds with the default, none with
/// <see cref="JournalFlushPolicy.PerWrite"/>, and everything since the last commit with
/// <see cref="JournalFlushPolicy.OnCommit"/>. A segment carries the last journal entry it contains, so
/// a replay stops at what the segment already holds rather than applying a write twice.
/// <see cref="Commit"/> makes the writes durable a second way and compacts them: one merged segment is
/// written to the directory and swapped in atomically, its bytes flushed to the device before the
/// rename, so a power loss cannot expose a final-named file whose content was never written. The
/// <b>directory entry</b> is not fsynced — .NET has no portable API for it and this type does not call
/// into libc — so the order in which the filesystem commits the rename and the cleanup of the replaced
/// generation is the filesystem's: after a power loss the store may still show the previous generation,
/// which is a lost commit rather than a corrupt one, and a directory that lost its segment entirely is
/// refused rather than opened as an empty corpus unless the journal can rebuild it. A reader in flight
/// never sees a half-written store. A crash after that rename and before the cleanup can leave the
/// replaced generation on disk beside its successor; the store opens <b>the newest generation only</b>,
/// because the successor is the flush of everything live at commit time — chaining both would return
/// every shared document twice and bring back the ones the commit deleted.
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
public sealed class SegmentedTextIndex : ITextIndex, IVocabularyIndex, IAccumulatingIndex, ICandidateIndex, ICandidateIdIndex, IOrdinalIdIndex, ITermCursorIndex, IVocabularyPrefixIndex, IDisposable
{
    private readonly string _directory;
    private readonly ITokenizer _tokenizer;
    private readonly SegmentSourceMode _mode;
    private readonly int? _decodedCacheEntries;
    private readonly List<DecodedPostingsCache> _caches = [];

    /// <summary>Mappings this store opened and must release, empty unless <see cref="Mode"/> is Mapped.</summary>
    private readonly List<IDisposable> _owners = [];

    private SegmentChain _chain;

    /// <summary>The journal, or <c>null</c> when it is disabled or this store was replaced by a commit.</summary>
    private SegmentJournal? _journal;

    /// <summary>The policy this store's journal uses, so the store a commit returns opens the same one.</summary>
    private readonly JournalOptions _journalOptions;

    /// <summary>Whether a commit or merge replaced this store, after which it refuses to write.</summary>
    private bool _superseded;

    private SegmentedTextIndex(
        string directory,
        ITokenizer tokenizer,
        SegmentChain chain,
        SegmentSourceMode mode,
        int? decodedCacheEntries,
        JournalOptions journalOptions)
    {
        _directory = directory;
        _tokenizer = tokenizer;
        _chain = chain;
        _mode = mode;
        _decodedCacheEntries = decodedCacheEntries;
        _journalOptions = journalOptions;
        _caches = [];
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
    /// <param name="mode">
    /// How the store's existing segments are opened. <see cref="SegmentSourceMode.Array"/> reads each
    /// segment into memory — the faster search, and the simpler lifetime.
    /// <see cref="SegmentSourceMode.Mapped"/> maps it — the smaller heap. See
    /// <see cref="SegmentSourceMode"/> for what each costs, measured.
    /// </param>
    /// <param name="decodedCacheEntries">
    /// Posting entries each segment may hold decoded, or <c>null</c> for none. A repeat look-up of a
    /// hot term then walks flat arrays instead of decoding the block planes — the same arrays the
    /// in-memory index folds, in the same ascending order, so a warm cache serves a page bit for bit.
    /// The budget is <b>per segment</b>, never shared: a cache is keyed on the term, and the same term
    /// exists in two segments with different postings.
    /// </param>
    /// <param name="journal">
    /// What the write-ahead journal does. Default <see cref="JournalOptions.Default"/>: every write is
    /// appended and replayed when the store opens, flushed in groups of a hundred entries or fifty
    /// milliseconds. <see cref="JournalOptions.Disabled"/> restores the behaviour of a store with no
    /// journal, where a crash before the next <see cref="Commit"/> loses the writes since the last one.
    /// </param>
    /// <exception cref="InvalidDataException">A segment in the directory is not one this reader knows,
    /// or the journal holds a record its checksum does not match.</exception>
    public SegmentedTextIndex(
        string directory,
        ITokenizer? tokenizer = null,
        SegmentSourceMode mode = SegmentSourceMode.Array,
        int? decodedCacheEntries = null,
        JournalOptions? journal = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _directory = directory;
        _tokenizer = tokenizer ?? Linguistics.Tokenizer.Default;
        _mode = mode;
        _journalOptions = (journal ?? JournalOptions.Default).Validated();
        _caches = [];

        var segments = new List<SegmentTextIndex>();
        var owners = new List<IDisposable>();

        // The newest generation only. A commit writes the new segment to a temporary file, renames it
        // into place, and only then deletes the segments it replaced — so a crash in that window
        // leaves the previous generation beside its successor. Both describe the same corpus (the
        // successor is the flush of everything live at commit time), so chaining both would return
        // every shared document twice and resurrect every document the commit had deleted. The
        // generation is in the name and the file list is sorted, so the last match is the newest.
        string? newest = null;

        foreach (string path in SegmentFiles(directory))
            newest = path;

        long segmentSequence = 0;

        if (newest is not null)
        {
            var handle = OpenSegment(newest, _tokenizer, mode, decodedCacheEntries);

            segments.Add(handle.Index);
            segmentSequence = handle.Index.JournalSequence;

            if (handle.Owner is not null)
                owners.Add(handle.Owner);
        }

        // No readable generation but a temporary segment means a commit was interrupted before its
        // rename. Opening that as a new, empty store would answer "no documents" to every query,
        // which is the one failure mode worse than an exception: a corpus that is still on disk,
        // reported as absent. A journal with entries is the difference — the writes are still there,
        // and the replay below rebuilds them.
        if (newest is null
            && Directory.Exists(directory)
            && Directory.GetFiles(directory, SegmentPrefix + "*.lxs.tmp").Length > 0
            && !SegmentJournal.HasEntriesAbove(directory, segmentSequence))
        {
            throw new InvalidDataException(
                $"{directory} holds an interrupted commit: a temporary segment and no readable generation");
        }

        _owners = owners;
        _chain = segments.Count == 0
            ? new SegmentChain([EmptySegment()], _tokenizer)
            : new SegmentChain(segments, _tokenizer);

        if (_journalOptions.Enabled)
        {
            _journal = SegmentJournal.Open(_directory, _journalOptions);

            // A journal whose highest entry sits below the segment's marker was written before that
            // segment — a crash between the rename and the truncation — so the next append has to
            // continue above the marker rather than reuse a sequence the segment already claims.
            if (segmentSequence > _journal.LastSequence)
                _journal.StartAt(segmentSequence);

            TruncatedJournalBytes = _journal.TruncatedBytes;
            Replay(segmentSequence);
        }
        else if (SegmentJournal.HasEntriesAbove(directory, segmentSequence))
        {
            // Ignoring these would answer with a corpus missing every write since the last commit,
            // silently — the entries are durable and the store was not asked to forget them, only not to
            // add to them.
            throw new InvalidDataException(
                $"{directory} holds durable journal entries this open was asked to ignore: open it with the journal enabled to replay them, or commit before disabling it");
        }
    }

    /// <summary>
    /// How many bytes the open found beyond the last whole journal record, or zero when the journal
    /// ended on a boundary.
    /// </summary>
    /// <remarks>
    /// A crash leaves a prefix: the record it interrupted does not frame, and the next append cuts it
    /// off. The count is exposed because that is the one fact an operator cannot otherwise learn — the
    /// store opens, answers, and is missing the tail of the last write it was given. A non-zero value
    /// after a clean shutdown means a torn record reached the file, which is worth looking at even
    /// though the store handled it.
    /// </remarks>
    public long TruncatedJournalBytes { get; }

    /// <summary>
    /// Applies the journal's entries above the segment's marker, in the order they were written.
    /// </summary>
    /// <remarks>
    /// The entries are the operations themselves, not a snapshot: an <see cref="Add"/> is re-indexed, a
    /// <see cref="Remove"/> is applied to the chain, and a <see cref="Clear"/> starts a new chain. That
    /// is what makes the journal small — it holds what changed rather than the corpus — and it is why
    /// the replay must stop at the marker: re-indexing a document that is already in the segment adds a
    /// second copy and tombstones the first, which is not the corpus the caller wrote.
    /// </remarks>
    private void Replay(long segmentSequence)
    {
        foreach (JournalEntry entry in _journal!.ReadFrom(segmentSequence))
        {
            switch (entry.Operation)
            {
                case JournalOperation.Add:
                    _chain.Add(entry.Document!);
                    break;

                case JournalOperation.Remove:
                    _chain.Remove(entry.DocumentId!);
                    break;

                case JournalOperation.Clear:
                    _chain = new SegmentChain([EmptySegment()], _tokenizer);
                    break;
            }
        }
    }

    /// <summary>Refuses a write through a store that a commit or merge has replaced.</summary>
    private void EnsureWritable()
    {
        if (_superseded)
        {
            throw new InvalidOperationException(
                "this store was replaced by Commit or Merge: write through the store it returned");
        }
    }

    /// <summary>
    /// Forces every journaled write to the device now, whatever the flush policy.
    /// </summary>
    /// <remarks>
    /// The grouped policy bounds its window on the next append or on disposal, so a writer that stops
    /// stops with its last flushed entry. This is how a caller closes that window at a cadence of its
    /// own — after a request, before answering a client — without paying a device flush per write.
    /// </remarks>
    public void FlushJournal() => _journal?.Flush();

    /// <summary>How this store's segments are opened. A commit keeps the mode it was opened with.</summary>
    public SegmentSourceMode Mode => _mode;

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
    /// <remarks>
    /// Every mutation applies to the corpus first and is journaled second, and this order is the whole
    /// of the contract: a write the index refuses fails before a record exists, so the journal cannot
    /// hold an operation a replay would choke on, and a record that cannot be written leaves the change
    /// in the memory of a store that is answering queries with the exception in the caller's hand.
    /// Neither order can make a failed write durable; this one cannot make a rejected write replayable.
    /// </remarks>
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureWritable();

        _chain.Add(document);
        _journal?.Append(JournalOperation.Add, document);
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        EnsureWritable();

        if (!_chain.Remove(documentId))
            return false;

        _journal?.Append(JournalOperation.Remove, documentId: documentId);

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Replaces the corpus and journals one <see cref="JournalOperation.Clear"/> rather than a removal
    /// per document: a caller that rebuilds a corpus should not make the journal carry the old one.
    /// </remarks>
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        EnsureWritable();

        _chain = new SegmentChain([EmptySegment()], _tokenizer);
        _journal?.Append(JournalOperation.Clear);

        foreach (var document in documents)
        {
            _chain.Add(document);
            _journal?.Append(JournalOperation.Add, document);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        EnsureWritable();

        _chain = new SegmentChain([EmptySegment()], _tokenizer);
        _journal?.Append(JournalOperation.Clear);
    }

    /// <summary>
    /// Merges the store in memory and returns the resulting index, leaving this one unchanged.
    /// </summary>
    /// <remarks>
    /// Returns a new index rather than mutating this one: the fold reads the segments, their bases and
    /// the length array, so a write between those reads would fold half the old chain and half the new
    /// one. A caller that needs a merge to be invisible to readers swaps the index their engine holds —
    /// the pattern this repository publishes as <c>AtomicEngineReference</c>. The journal moves to the
    /// store this returns, and this one stops writing: a directory holds one journal, and two stores
    /// appending to it would hand out the same sequences.
    /// </remarks>
    public SegmentedTextIndex Merge()
    {
        EnsureWritable();

        long sequence = _journal?.LastSequence ?? 0;
        SegmentChain merged = _chain.Merge(_tokenizer, sequence);
        var result = new SegmentedTextIndex(_directory, _tokenizer, merged, _mode, _decodedCacheEntries, _journalOptions)
        {
            _journal = _journal,
        };

        _journal = null;
        _superseded = true;

        return result;
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
    /// mid-write leaves the previous segment intact rather than a truncated one. Its header carries the
    /// last journal entry it contains, and the journal is truncated after the rename: a crash between
    /// the two replays entries the segment already holds, which the marker makes the replay skip.
    /// </para>
    /// <para>
    /// The journal belongs to the store this returns, and this one stops writing — see
    /// <see cref="Merge"/>.
    /// </para>
    /// </remarks>
    public SegmentedTextIndex Commit()
    {
        EnsureWritable();

        long sequence = _journal?.LastSequence ?? 0;

        // One merge, one write: the live corpus is compacted and the resulting segment's bytes are
        // written out. Compacting a second time to produce bytes that are a pure function of a corpus
        // that has not changed would pay the about 0.9 s and 126 MB twice for nothing.
        SegmentTextIndex merged = _chain.Flush(sequence);
        var committed = new SegmentedTextIndex(_directory, _tokenizer, new SegmentChain([merged], _tokenizer), _mode, _decodedCacheEntries, _journalOptions);

        committed.WriteToDisk(merged);

        _journal?.Reset(sequence);
        _journal?.Dispose();
        _journal = null;
        _superseded = true;

        if (_journalOptions.Enabled)
            committed._journal = SegmentJournal.Open(_directory, _journalOptions, sequence);

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
    ITermCursor? ITermCursorIndex.ResolveTerm(string term) => _chain.ResolveTerm(term);

    /// <inheritdoc />
    IEnumerable<string> IVocabularyPrefixIndex.TermsWithPrefix(string prefix) => _chain.TermsWithPrefix(prefix);

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document) =>
        _chain.TryGetDocument(documentId, out document);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Fields => [TextFields.Default];

    /// <summary>The tokenizer this store reads and writes with.</summary>
    public ITokenizer Tokenizer => _tokenizer;

    /// <summary>
    /// Releases the mappings this store opened. Idempotent, and a store in
    /// <see cref="SegmentSourceMode.Array"/> mode has nothing to release.
    /// </summary>
    /// <remarks>
    /// A store in <see cref="SegmentSourceMode.Mapped"/> mode holds a view over a file, and its
    /// <see cref="SegmentedTextIndex"/> is unusable after it is released — that is a statement about the
    /// mapping rather than about the search. A store in <see cref="SegmentSourceMode.Array"/> mode holds
    /// nothing that needs releasing, so disposing it is a no-op and an undisposed one leaks nothing but
    /// the bytes the garbage collector owns. Set a mapped store on the <c>using</c> you would give any
    /// other disposable resource.
    /// </remarks>
    public void Dispose()
    {
        _journal?.Dispose();
        _journal = null;

        foreach (var owner in _owners)
            owner.Dispose();

        _owners.Clear();
    }

    /// <summary>
    /// Opens one segment file in the mode given, with anything that must be released alongside it.
    /// </summary>
    private SegmentHandle OpenSegment(string path, ITokenizer tokenizer, SegmentSourceMode mode, int? cacheEntries)
    {
        // One cache per segment, never one shared across the chain. A cache is keyed on the term, and the
        // same term exists in two segments with different postings — a shared cache would serve one
        // segment's postings for the other's. The budget is per segment for the same reason.
        DecodedPostingsCache? cache = cacheEntries is { } entries ? new DecodedPostingsCache(entries) : null;

        if (cache is not null)
            _caches.Add(cache);

        return mode == SegmentSourceMode.Mapped
            ? OpenMapped(path, tokenizer, cache)
            : new SegmentHandle(new LexiSharp.Indexing.SegmentTextIndex(new ArraySegmentSource(File.ReadAllBytes(path)), tokenizer, cache), null);
    }

    /// <summary>
    /// Maps one segment file. The mapping is the owner, and it is released when the store is disposed.
    /// </summary>
    private static SegmentHandle OpenMapped(string path, ITokenizer tokenizer, DecodedPostingsCache? cache)
    {
        // MappedSegment.Open releases the mapping itself if the index over it cannot be built, so by the
        // time it returns the index depends on a live view.
        var mapped = MappedSegment.Open(path, tokenizer, cache);

        return new SegmentHandle(mapped.Index, mapped);
    }

    /// <summary>One segment the store opened, and the mapping that must be released with it.</summary>
    private readonly struct SegmentHandle(SegmentTextIndex index, IDisposable? owner)
    {
        public SegmentTextIndex Index { get; } = index;

        public IDisposable? Owner { get; } = owner;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The union of the store's vocabularies, in code-point order. Without this capability the engine's
    /// prefix and fuzzy atoms fall back to the atom's literal base term, which answers a different
    /// question than the one asked without saying so.
    /// </remarks>
    IEnumerable<string> IVocabularyIndex.Vocabulary => _chain.CollectDistinctTerms();

    /// <summary>
    /// The documents containing at least one of the terms, each once, in ascending ordinal order.
    /// </summary>
    /// <remarks>
    /// Forwarded, because the engine takes its per-document loop whenever the accumulating pass
    /// declines — which is every query below the accumulation threshold — and without this capability
    /// that loop walks <see cref="Documents"/>, which for a store decodes the whole corpus. Measured on
    /// a 200,000-document store, a single term with a document frequency of 3 served 207 ms and allocated
    /// 222 MB per search that way, against 13 µs and 928 B for the in-memory index it is supposed to
    /// match.
    /// </remarks>
    IEnumerable<SearchDocument> ICandidateIndex.GetCandidateDocuments(IReadOnlyList<string> terms) =>
        _chain.GetCandidateDocuments(terms);

    /// <inheritdoc />
    IEnumerable<string> ICandidateIdIndex.GetCandidateIds(IReadOnlyList<string> terms) =>
        _chain.GetCandidateIds(terms);

    /// <summary>
    /// The size of the ordinal space the chain folds into, and the size the engine rents its accumulator
    /// at. Forwarded, because a store the engine cannot accumulate would be served one document at a time.
    /// </summary>
    int IAccumulatingIndex.OrdinalSpace => _chain.OrdinalSpace;

    /// <inheritdoc />
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal) =>
        _chain.DocumentAt(ordinal);

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalOf(string documentId) =>
        _chain.OrdinalOf(documentId);

    /// <inheritdoc />
    /// <remarks>
    /// Forwarded to the chain, and the reason it has to be is the whole point of the store. Without this
    /// capability the engine takes its per-document loop, and for a segment-backed index that loop costs
    /// <c>O(documents x document frequency)</c>: it asks the index for one term's frequency once per
    /// document, and a segment answers each of those by walking the term's postings. Measured on a
    /// 40,000-document corpus, a single near-universal term served 2.8 s that way against 0.7 ms in
    /// memory; with the capability forwarded it takes the term-at-a-time pass the chain already has.
    /// </remarks>
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator) =>
        _chain.Accumulate(weight, accumulator);

    /// <summary>
    /// The id of the document at this ordinal, without decoding it.
    /// </summary>
    /// <remarks>
    /// This is the capability that keeps the page window's tie-break from decoding a stored document per
    /// comparison. See <see cref="IOrdinalIdIndex"/> for what the two cost differently.
    /// </remarks>
    string? IOrdinalIdIndex.IdAt(int ordinal) => _chain.IdAt(ordinal);

    /// <summary>The chain the store searches. Internal for the engine and the decorators.</summary>
    internal SegmentChain Chain => _chain;

    /// <summary>
    /// Writes the store's live corpus as one segment into the directory, replacing every segment that
    /// was there, and reopens it from what is now on disk.
    /// </summary>
    /// <remarks>
    /// The bytes come from the chain's own merged segment rather than a second compaction: a flush is
    /// a pure function of the live corpus, so compacting twice would produce the same bytes at twice
    /// the cost — about 0.9 s and 126 MB allocated at 40,000 documents each time.
    /// <para>
    /// <b>Unverified on Windows: a mapped store's commit may fail at the cleanup.</b> The generation it
    /// replaces is deleted while the store it was read from still holds a view over it. Linux allows
    /// deleting a mapped file (the inode outlives the last unmap), so the path is exercised here;
    /// Windows refuses the delete while a mapping is open. Every CI job in this repository runs on
    /// Ubuntu, so nothing in the tree demonstrates the Windows behaviour either way, and it is stated
    /// as a risk rather than fixed blind. A caller that commits a
    /// <see cref="SegmentSourceMode.Mapped"/> store on Windows should dispose it first, which the
    /// array mode does not need.
    /// </para>
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

        // The bytes reach the device before the rename gives them their final name. Without this
        // flush a power loss can leave a correctly named file whose content was never written: the
        // reader refuses it — correctly — and the store cannot be opened at all, while the previous
        // generation has already been deleted. A flush costs one fsync per commit, which is a batch
        // operation by design.
        //
        // The directory entry itself is not fsynced: .NET has no portable API for it and this type
        // does not call into libc, so the order in which the filesystem commits the rename and the
        // cleanup below is the filesystem's to choose.
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, target, overwrite: true);

        foreach (string path in SegmentFiles(_directory))
        {
            if (!string.Equals(path, target, StringComparison.Ordinal))
                File.Delete(path);
        }

        // A temporary segment beside a readable generation is the tail of a commit that was interrupted
        // after this one's predecessor — the journal may have rebuilt the corpus it was writing. It is
        // stale, and leaving it beside a live generation would make a later reader's "is this directory
        // an interrupted commit?" answer depend on debris nobody will read.
        foreach (string stale in Directory.GetFiles(_directory, SegmentPrefix + "*.lxs.tmp"))
            File.Delete(stale);

        // Reopen from what is now on disk, so the returned store is the one the directory holds, and in
        // the mode it was opened with.
        var handle = OpenSegment(target, _tokenizer, _mode, _decodedCacheEntries);
        _chain = new SegmentChain([handle.Index], _tokenizer);

        if (handle.Owner is not null)
            _owners.Add(handle.Owner);
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
