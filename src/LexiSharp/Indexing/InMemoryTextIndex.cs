using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// In-memory inverted index: for each term, the documents containing it and the
/// positions within each document. Exposes the corpus statistics required by scorers.
/// </summary>
/// <remarks>
/// Mutations (<c>Add</c>/<c>Remove</c>/<c>Clear</c>/...) are not thread-safe: apply them from a
/// single thread (or synchronize externally). Read-only queries hold no shared mutable state and
/// may run concurrently with one another.
/// </remarks>
public sealed class InMemoryTextIndex : ITextIndex, IUnorderedCandidateIndex, IVocabularyIndex, IAccumulatingIndex, IFieldStatisticsIndex
{
    private readonly ITokenizer _tokenizer;

    /// <summary>Document id → the document's ordinal. The one map ids are resolved through.</summary>
    private readonly Dictionary<string, int> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PostingList> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _corpusFrequencies = new(StringComparer.Ordinal);

    /// <summary>
    /// The corpus in slot order: <c>list[ordinal]</c> is the document living in that slot, or
    /// <c>null</c> when the slot is free. This is what <see cref="Documents"/> and the ordered
    /// candidate path walk, and what the term-at-a-time pass resolves an ordinal through.
    /// </summary>
    /// <remarks>
    /// A list rather than the dictionary's own value collection, because the scoring pass addresses
    /// documents by ordinal and one structure should answer both questions. Order is insertion
    /// order, which is the order the value collection already gave; the difference is that a freed
    /// slot is now reused deterministically rather than by the dictionary's hash-anchored
    /// free-slot scan, so enumeration does not depend on the id hashes.
    /// </remarks>
    private readonly List<SearchDocument?> _byOrdinal = [];

    /// <summary>Slots freed by <see cref="Remove"/>, reused before the list grows.</summary>
    private readonly List<int> _freeOrdinals = [];

    /// <summary>
    /// Token count per document, indexed by ordinal. The single source of truth for document
    /// length: an array rather than a field on a per-document object because the term-at-a-time pass
    /// reads it once per posting entry, and a 4-byte-per-document read beats a dependent load
    /// through an object for the very reason the flat posting lists exist.
    /// </summary>
    private int[] _lengthsByOrdinal = [];

    /// <summary>
    /// Bumped by every corpus mutation. The derived per-term posting copies carry the epoch they
    /// were built from, so a stale copy is recognised and rebuilt rather than silently used.
    /// <para>
    /// <c>_epoch++</c> is deliberately non-atomic and the scoring pass reads this field plainly:
    /// that is sound only because the documented contract is one writer with no write overlapping a
    /// read. The memory model is not what protects this — the contract is — so if the single-writer
    /// assumption ever changes, the first casualty is this counter, not the volatile postings.
    /// </para>
    /// </summary>
    private int _epoch;

    /// <summary>
    /// Cached quantization of <see cref="_lengthsByOrdinal"/>, published with a volatile write for
    /// the same reason <see cref="PostingList.Flat"/> is: two concurrent readers can miss and both
    /// build. They build identical arrays from an unchanged <see cref="_lengthsByOrdinal"/> and the
    /// reference assignments are atomic, so the loser is wasted work — but the volatile keeps the
    /// array's contents visible to a reader that observed the publication, exactly as the flat
    /// posting copies' immutability does for them.
    /// </summary>
    private int[]? _quantizedLengthsByOrdinal;

    /// <summary>
    /// One term's inverted list, and the single string instance the whole index uses for that term.
    /// </summary>
    /// <remarks>
    /// The tokenizer allocates a fresh string for every token occurrence, so an index that stored
    /// what it was handed would hold one string object per token — 500k objects to represent 38
    /// distinct words on a 10k-document / 500k-token corpus. Keeping the first-seen instance in the
    /// posting list makes the corpus hold a vocabulary instead: measured 21.1 MB of token strings
    /// becomes 4.1 MB, and the whole index 58.9 MB becomes 40.9 MB.
    /// <para>
    /// It is free to maintain because this is the map the index already had to consult: resolving a
    /// term to its posting list is one lookup whether or not the shared instance is tracked, so the
    /// canonical form rides along instead of costing a second hash of every token.
    /// </para>
    /// </remarks>
    private sealed class PostingList(string term)
    {
        /// <summary>The shared instance for this term's value; store this, not the caller's string.</summary>
        public string Term { get; } = term;

        /// <summary>
        /// Document id → where the term sits in that document: its ordinal and its positions.
        /// </summary>
        /// <remarks>
        /// Keyed on the id, so <see cref="IReadOnlyTextIndex.TermFrequency"/> and
        /// <see cref="IReadOnlyTextIndex.GetTermPositions"/> stay at the two string lookups they have always
        /// cost — one to resolve the term, one to resolve the document inside its list. Keying on
        /// an object instead saves nothing there and costs a third lookup to recover the id, which
        /// is a measured 0.63x on <c>SearchBenchmarks.BooleanSearch</c>.
        /// <para>
        /// The ordinal rides in the value rather than in the key, as a field of a struct so the
        /// dictionary stores it inline beside the key. That is what lets the term-at-a-time pass and
        /// the candidate union reach a document's ordinal without hashing an id: they read it out of
        /// the value the enumerator has already fetched.
        /// </para>
        /// </remarks>
        public Dictionary<string, Posting> ByDocument { get; } = new(StringComparer.Ordinal);

        private FlatPosting? _flat;

        /// <summary>
        /// The contiguous view of <see cref="ByDocument"/>, or <c>null</c> when there is none for
        /// the current corpus.
        /// </summary>
        /// <remarks>
        /// <b>Concurrent readers:</b> two searches can miss at the same time and both build a copy.
        /// They build identical arrays and the reference is published with a volatile write, so the
        /// loser is wasted work rather than a correctness problem — as long as a half-built array
        /// can never be observed, which is why the epoch and the arrays travel together inside one
        /// immutable object rather than in two fields.
        /// </remarks>
        public FlatPosting? Flat => Volatile.Read(ref _flat);

        /// <summary>Publishes a freshly built copy for concurrent readers.</summary>
        public void PublishFlat(FlatPosting flat) => Volatile.Write(ref _flat, flat);
    }

    /// <summary>
    /// One term's placement inside one document. A struct, deliberately: as a class it would be a
    /// second dependent load per posting entry on every path that walks a posting list.
    /// </summary>
    private readonly struct Posting(int ordinal, List<int> positions)
    {
        /// <summary>The document's dense ordinal, the address the scoring pass writes to.</summary>
        public int Ordinal { get; } = ordinal;

        /// <summary>Ascending token positions of the term in the document; the phrase gate's input.</summary>
        public List<int> Positions { get; } = positions;
    }

    /// <summary>
    /// A term's postings as two parallel arrays — document ordinals ascending, term frequencies
    /// aligned — plus the index epoch they were built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dictionary form is the right shape for «what are this term's positions in this
    /// document?» and the wrong shape for «score every document this term appears in». The second
    /// question is what a query actually asks, and answering it from the dictionary costs a
    /// dependent load per posting entry — the <c>List&lt;int&gt;</c> of positions, a separate heap
    /// object. Measured on a 10,000-document Zipf corpus with three head terms (26,525 posting
    /// entries), the dictionary walk takes 1.21 ms and the identical arithmetic over these arrays
    /// takes 0.12 ms, a ~10x gap that is entirely the pointer chasing.
    /// </para>
    /// <para>
    /// Eight bytes per posting entry, against roughly eighty for the dictionary entry, the
    /// <c>List&lt;int&gt;</c> and its backing array it mirrors — so caching every term's copy costs
    /// about a tenth of what the authoritative form already costs. Measured: a 10,000-document
    /// Zipf index grows 72.5 MB to 72.9 MB once three head terms have been queried, and 41.2 MB
    /// from 40.9 MB on a 38-word corpus, that difference being the ordinals the documents carry.
    /// </para>
    /// <para>
    /// <b>Concurrent readers:</b> two searches can miss the cache at the same time and both build
    /// a copy. They build identical arrays, the reference is published with a volatile write, and
    /// the arrays are immutable once published — so the loser is wasted work, not a correctness
    /// problem. What would not be safe is a half-built array, which is why the epoch and the arrays
    /// travel together inside one immutable object rather than in two fields.
    /// </para>
    /// <para>
    /// Built on first use and rebuilt whenever the corpus changes, which is the trade: a read-only
    /// index pays the build once, and an index mutated between every query pays an O(df · log df)
    /// build per query. That is still cheaper than the walk it replaces, but it is a real cost,
    /// and it is why the build is skipped for the tiny lists that gain nothing from it.
    /// </para>
    /// </remarks>
    private sealed class FlatPosting(int epoch, int[] ordinals, int[] frequencies)
    {
        public int Epoch { get; } = epoch;

        public int[] Ordinals { get; } = ordinals;

        public int[] Frequencies { get; } = frequencies;

        public int Count => Ordinals.Length;
    }

    // Per-field statistics, populated from SearchDocument.TextFields. The document's main Text is
    // the field TextFields.Default, and it is tracked here as well as in the flat structures above
    // so a field-aware scorer can read every field through one path.
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> _fieldFrequencies =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _fieldLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fieldTotalTokens = new(StringComparer.Ordinal);

    private long _totalTokens;

    /// <summary>Which document count divides the corpus token count in <see cref="AverageDocumentLength"/>.</summary>
    private readonly AverageLengthDivisor _averageLengthDivisor;

    /// <summary>Whether <see cref="DocumentLength"/> reports the exact length or the byte-quantized one.</summary>
    private readonly DocumentLengthQuantization _documentLengthQuantization;

    /// <summary>
    /// Rounds a document length the way an implementation that stores lengths in one byte does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One byte cannot hold a document's length, so such an index rounds: it encodes the length as the
    /// largest of 256 codes whose decoded value is at or below it, and reads that code back. The codes
    /// are a float with a three-bit mantissa and a five-bit exponent, so lengths up to 40 survive
    /// exactly and the relative error settles around eight per cent — 111 terms reads back as 104,
    /// 469 as 440, 592 as 536.
    /// </para>
    /// <para>
    /// Two properties make this worth reproducing. It always rounds <b>down</b>, so the length
    /// normalization divides by a smaller number and the score comes out slightly higher. And it maps
    /// several distinct lengths onto one value, so documents that differ by up to fifty terms are
    /// penalized identically, which is enough to reorder documents whose scores are close.
    /// </para>
    /// <para>
    /// Written as arithmetic rather than as a 256-entry table: the code is
    /// <c>24 + ((8 + (code &amp; 7)) &lt;&lt; ((code &gt;&gt; 3) - 4))</c> above code 40 and the identity
    /// below it, which reproduces all 256 values of the table exactly. Choosing the group without a
    /// loop is what keeps it affordable here — <c>DocumentLength</c> is called once per candidate
    /// document per query term.
    /// </para>
    /// </remarks>
    internal static int QuantizedLength(int exactLength)
    {
        if (exactLength <= 40)
            return exactLength;

        // The group is the largest power-of-two bucket the length falls in: base = 24 + 2^(e-1), and
        // e is one more than the bit length of (length - 24).
        int exponent = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(exactLength - 24));
        int baseLength = 24 + (1 << (exponent - 1));
        int step = 1 << (exponent - 4);

        return baseLength + (((exactLength - baseLength) / step) * step);
    }

    /// <summary>Creates an empty index, tokenizing with <c>Tokenizer.Default</c> unless told otherwise.</summary>
    /// <param name="tokenizer">
    /// The tokenizer whose terms, stop-word removal and stemming shape the index statistics. It
    /// must be the same one the queries are tokenized with, or the terms will not match.
    /// </param>
    /// <param name="averageLengthDivisor">
    /// Which document count divides <see cref="CorpusTokenCount"/> in
    /// <see cref="AverageDocumentLength"/>. <see cref="AverageLengthDivisor"/> (the default) divides by
    /// every indexed document; <see cref="AverageLengthDivisor.NonEmptyDocuments"/> divides by the
    /// documents that hold at least one term, which is the convention the published BM25 baselines
    /// were produced under.
    /// <para>
    /// The two differ only when the corpus holds an empty document — 1 of 8,674 on ArguAna — and the
    /// denominator is the number of documents having at least one term in every implementation this
    /// has been checked against. The gap is about one part in ten thousand, which is far too small to
    /// move a ranking but large enough to change the last bits of a score, and an exact tie is decided
    /// on exactly those bits. Exposed rather than corrected because it is a scoring convention, and a
    /// caller reproducing a published number needs the one that number used.
    /// </para>
    /// </param>
    public InMemoryTextIndex(
        ITokenizer? tokenizer = null,
        AverageLengthDivisor averageLengthDivisor = AverageLengthDivisor.AllDocuments)
    {
        _tokenizer = tokenizer ?? LexiSharp.Linguistics.Tokenizer.Default;
        _averageLengthDivisor = averageLengthDivisor;
        _documentLengthQuantization = DocumentLengthQuantization.Exact;
    }

    /// <summary>
    /// An index whose document lengths are quantized. Internal, and reachable only by the evaluation
    /// harness and the tests.
    /// </summary>
    /// <remarks>
    /// <paramref name="documentLengthQuantization"/> chooses how a length is stored for scoring.
    /// <see cref="DocumentLengthQuantization.Exact"/> keeps the real length.
    /// <see cref="DocumentLengthQuantization.OneByte"/> stores it as a single byte through the
    /// arithmetic encoding one implementation uses, which is what makes two documents of 149 and 151
    /// terms score identically under BM25 length normalisation.
    /// <para>
    /// It is a separate constructor rather than a parameter on the public one because it is not a
    /// setting but a different account of what a length is. Measured on BEIR ArguAna, quantizing made
    /// nDCG@10 worse by about 0.006, so it is a device for reproducing a published figure and not an
    /// improvement — and a caller who reaches for it has almost certainly misunderstood what it does.
    /// </para>
    /// </remarks>
    internal InMemoryTextIndex(
        ITokenizer? tokenizer,
        AverageLengthDivisor averageLengthDivisor,
        DocumentLengthQuantization documentLengthQuantization)
    {
        _tokenizer = tokenizer ?? LexiSharp.Linguistics.Tokenizer.Default;
        _averageLengthDivisor = averageLengthDivisor;
        _documentLengthQuantization = documentLengthQuantization;
    }

    /// <summary>The tokenizer used to split documents and queries into terms.</summary>
    public ITokenizer Tokenizer => _tokenizer;

    /// <summary>
    /// Whether <see cref="DocumentLength"/> reports exact or byte-quantized lengths. Internal, for
    /// the reason on the enumeration: this is a statement about what a length means, not a knob.
    /// </summary>
    internal DocumentLengthQuantization DocumentLengthQuantization => _documentLengthQuantization;

    /// <summary>Which document count divides the corpus token count in <see cref="AverageDocumentLength"/>.</summary>
    public AverageLengthDivisor LengthDivisor => _averageLengthDivisor;

    /// <summary>
    /// The corpus in insertion order, rebuilt on no allocation: the wrapper reads the live
    /// <see cref="_byOrdinal"/> list, so it tracks additions and removals the way the dictionary's
    /// value collection did.
    /// </summary>
    public IReadOnlyCollection<SearchDocument> Documents => new DocumentView(this);

    /// <inheritdoc />
    public int Count => _documents.Count;

    /// <inheritdoc />
    /// <remarks>
    /// This is the count that divides <see cref="AverageDocumentLength"/>, so a scorer's inverse
    /// document frequency has to be built from the same one. Reading <see cref="Count"/> here instead
    /// while the average is divided by the documents that hold a term produces a mix of the two, which
    /// is neither of the conventions available: it scores with an <c>N</c> one document too large next
    /// to an average taken over fewer documents. Measured on BEIR ArguAna, which holds one document
    /// with no term, the mix puts every score about three parts in a hundred thousand above the value
    /// the implementation it is compared against reports.
    /// </remarks>
    public int StatisticDocumentCount => _averageLengthDivisor == AverageLengthDivisor.AllDocuments
        ? Count
        : Count - CountEmpty();

    /// <summary>
    /// The live corpus view over the ordinal slots. Allocation-free to enumerate and to pass
    /// around, unlike a materialised list, which is what keeps <c>ITextIndex.Documents</c> usable
    /// on a hot path that only wants to walk it.
    /// </summary>
    private sealed class DocumentView(InMemoryTextIndex owner) : IReadOnlyCollection<SearchDocument>
    {
        public int Count => owner._documents.Count;

        public IEnumerator<SearchDocument> GetEnumerator()
        {
            var slots = owner._byOrdinal;

            for (int ordinal = 0; ordinal < slots.Count; ordinal++)
            {
                var document = slots[ordinal];

                if (document is not null)
                    yield return document;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <inheritdoc />
    public double AverageDocumentLength
    {
        get
        {
            if (_averageLengthDivisor == AverageLengthDivisor.NonEmptyDocumentsStoredLengths)
                return AverageOfStoredLengths();

            int divisor = _averageLengthDivisor == AverageLengthDivisor.NonEmptyDocuments
                ? _documents.Count - CountEmpty()
                : Count;

            return divisor == 0 ? 0 : (double)_totalTokens / divisor;
        }
    }

    /// <summary>
    /// The mean of the lengths as this index stores them, over the documents that hold at least one.
    /// </summary>
    /// <remarks>
    /// Both halves have to move together. Averaging quantized lengths while counting non-empty documents
    /// by the exact lengths is a third number again, and none of the three is the one a reference index
    /// was built with. Computed here rather than from a running total because the stored lengths are the
    /// quantized ones and only the array holds them.
    /// </remarks>
    private double AverageOfStoredLengths()
    {
        int[] lengths = _documentLengthQuantization == DocumentLengthQuantization.Exact
            ? _lengthsByOrdinal
            : QuantizedLengths();

        long total = 0;
        int documents = 0;

        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] <= 0)
                continue;

            total += lengths[i];
            documents++;
        }

        return documents == 0 ? 0 : (double)total / documents;
    }

    /// <summary>
    /// How many indexed documents hold no term at all. One dictionary walk, and only on a statistic
    /// the length-sensitive scorers read once per query, so it is not on a per-document path.
    /// </summary>
    private int CountEmpty()
    {
        int empty = 0;

        foreach (string id in _documents.Keys)
        {
            if (_tokens.TryGetValue(id, out IReadOnlyList<string>? tokens) && tokens.Count == 0)
                empty++;
        }

        return empty;
    }

    /// <inheritdoc />
    public int VocabularySize => _postings.Count;

    /// <inheritdoc />
    public IEnumerable<string> Vocabulary => _postings.Keys;

    /// <inheritdoc />
    public long CorpusTokenCount => _totalTokens;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        Clear();

        foreach (var document in documents)
            Add(document);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_documents.ContainsKey(document.Id))
            Remove(document.Id);

        // A freed slot is reused before the list grows, so the ordinal space tracks the live
        // corpus instead of every document the index has ever held.
        int ordinal;

        if (_freeOrdinals.Count > 0)
        {
            ordinal = _freeOrdinals[^1];
            _freeOrdinals.RemoveAt(_freeOrdinals.Count - 1);
            _byOrdinal[ordinal] = null;
        }
        else
        {
            ordinal = _byOrdinal.Count;
            _byOrdinal.Add(null);
        }

        // The length array is sized to the ordinal space, doubling so a bulk load stays amortized.
        if (ordinal >= _lengthsByOrdinal.Length)
        {
            int size = _lengthsByOrdinal.Length == 0 ? 4 : _lengthsByOrdinal.Length * 2;
            Array.Resize(ref _lengthsByOrdinal, Math.Max(ordinal + 1, size));
        }

        _byOrdinal[ordinal] = document;
        _documents[document.Id] = ordinal;
        _lengthsByOrdinal[ordinal] = 0;
        InvalidateDerivedPostings();

        // Post returns the index's shared instance of the term, so the token list built here holds
        // one string per distinct value rather than one per occurrence -- the tokenizer hands back
        // a fresh string every time, and retaining those is what made the corpus scale with token
        // count instead of vocabulary size.
        var raw = _tokenizer.Tokenize(document.Text);
        var mainTerms = new List<string>(raw.Count);

        for (int position = 0; position < raw.Count; position++)
            mainTerms.Add(Post(document.Id, ordinal, raw[position], position));

        RecordField(TextFields.Default, document.Id, mainTerms);

        // Counted once, here, so the flat corpus total is right on both branches below. Every
        // length-normalized scorer divides by it, so missing it silently rescales the whole
        // ranking.
        _totalTokens += mainTerms.Count;
        _lengthsByOrdinal[ordinal] = mainTerms.Count;

        if (document.TextFields is not { Count: > 0 })
        {
            _tokens[document.Id] = mainTerms;
            return;
        }

        // The flat view is the union of every field, so a document that only matches inside a
        // TextField is still reachable by candidate generation and still scores. The list is only
        // allocated when a document actually has text fields.
        var allTerms = new List<string>(mainTerms);
        int nextPosition = mainTerms.Count;

        foreach (var (field, text) in document.TextFields)
        {
            TextFields.Validate(field, nameof(SearchDocument.TextFields));

            var rawField = _tokenizer.Tokenize(text);
            var fieldTerms = new List<string>(rawField.Count);

            // A gap between the previous run and this one, so a quoted phrase can match inside
            // one field but never bridge two of them.
            nextPosition += FieldPositionGap;

            for (int i = 0; i < rawField.Count; i++)
                fieldTerms.Add(Post(document.Id, ordinal, rawField[i], nextPosition + i));

            nextPosition += fieldTerms.Count;

            RecordField(field, document.Id, fieldTerms);

            allTerms.AddRange(fieldTerms);
            _totalTokens += fieldTerms.Count;
        }

        _tokens[document.Id] = allTerms;
        _lengthsByOrdinal[ordinal] = allTerms.Count;
    }

    /// <summary>
    /// Number of unused positions inserted between two token runs (main text to first field, field
    /// to field), so positions belonging to different fields are never adjacent and a phrase
    /// query cannot span a field boundary. Same idea, and same value, as
    /// <see cref="ExpansionPositionOffset"/>.
    /// </summary>
    private const int FieldPositionGap = 2;

    /// <summary>
    /// Adds one occurrence of <paramref name="term"/> to the flat inverted lists at
    /// <paramref name="position"/>, and returns the index's shared instance of the term — which is
    /// what callers must store, not the string they were handed.
    /// </summary>
    private string Post(string documentId, int ordinal, string term, int position)
    {
        // The lookup the index had to do anyway, carrying the shared instance along: the first
        // occurrence of a value defines the instance, and every later occurrence is rewritten to it
        // at no extra hashing cost.
        if (!_postings.TryGetValue(term, out var posting))
            _postings.Add(term, posting = new PostingList(term));

        string shared = posting.Term;
        var postings = posting.ByDocument;

        if (!postings.TryGetValue(documentId, out var existing))
        {
            // Assigned locally first: writing straight into the dictionary and reading it back
            // would cost a second hash and probe of the same key, once per (document, term) pair
            // the document introduces.
            existing = new Posting(ordinal, new List<int>(2));
            postings[documentId] = existing;
        }

        existing.Positions.Add(position);

        _corpusFrequencies.TryGetValue(shared, out int corpusCount);
        _corpusFrequencies[shared] = corpusCount + 1;

        return shared;
    }

    /// <summary>
    /// Records one field's tokens in the per-field statistics, replacing whatever the document
    /// had for that field. The document is expected to have been removed already. The terms are the
    /// shared instances <see cref="Post"/> returned, so the field maps share the corpus vocabulary.
    /// </summary>
    private void RecordField(string field, string documentId, IReadOnlyList<string> terms)
    {
        if (!_fieldFrequencies.TryGetValue(field, out var byTerm))
        {
            byTerm = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            _fieldFrequencies[field] = byTerm;
        }

        foreach (string term in terms)
        {
            if (!byTerm.TryGetValue(term, out var byDocument))
            {
                byDocument = new Dictionary<string, int>(StringComparer.Ordinal);
                byTerm[term] = byDocument;
            }

            byDocument.TryGetValue(documentId, out int count);
            byDocument[documentId] = count + 1;
        }

        if (!_fieldLengths.TryGetValue(field, out var lengths))
        {
            lengths = new Dictionary<string, int>(StringComparer.Ordinal);
            _fieldLengths[field] = lengths;
        }

        lengths[documentId] = terms.Count;
        _fieldTotalTokens[field] = (_fieldTotalTokens.TryGetValue(field, out long total) ? total : 0)
            + terms.Count;
    }

    /// <summary>
    /// Number of synthetic positions inserted between a document's literal tokens and its
    /// expansion terms, so a phrase query can never bridge across the boundary.
    /// </summary>
    private const int ExpansionPositionOffset = 2;

    /// <summary>
    /// Indexes additional terms for an already-indexed document (the « semantic lexical »
    /// expansion). Each term counts once, at a synthetic position strictly after the
    /// document's literal tokens, and participates in the same statistics (document length,
    /// corpus frequencies) as any regular token. Terms the document already contains are
    /// skipped. The <see cref="SearchDocument"/> itself is left untouched.
    /// </summary>
    /// <param name="documentId">An id currently present in the index.</param>
    /// <param name="additionalTerms">The distinct terms to add.</param>
    public void AddExpansionTerms(string documentId, IEnumerable<string> additionalTerms)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentNullException.ThrowIfNull(additionalTerms);

        if (!_documents.TryGetValue(documentId, out int ordinal))
            throw new KeyNotFoundException($"Unknown document id: '{documentId}'.");

        var existing = new HashSet<string>(_tokens[documentId], StringComparer.Ordinal);
        int nextPosition = _lengthsByOrdinal[ordinal] + ExpansionPositionOffset;

        var added = new List<string>();
        int addedCount = 0;

        foreach (var candidate in additionalTerms)
        {
            if (candidate.Length == 0 || !existing.Add(candidate))
                continue;

            if (!_postings.TryGetValue(candidate, out var posting))
                _postings.Add(candidate, posting = new PostingList(candidate));

            string term = posting.Term;

            if (!posting.ByDocument.TryGetValue(documentId, out var placed))
            {
                placed = new Posting(ordinal, new List<int>(1));
                posting.ByDocument[documentId] = placed;
            }

            placed.Positions.Add(nextPosition++);
            added.Add(term);
            addedCount++;

            _corpusFrequencies.TryGetValue(term, out int corpusCount);
            _corpusFrequencies[term] = corpusCount + 1;
        }

        if (addedCount == 0)
            return;

        var allTokens = new List<string>(_tokens[documentId]);
        allTokens.AddRange(added);
        _tokens[documentId] = allTokens;

        _lengthsByOrdinal[ordinal] += addedCount;
        _totalTokens += addedCount;
        InvalidateDerivedPostings();
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        if (!_documents.Remove(documentId, out int ordinal))
            return false;

        InvalidateDerivedPostings();

        if (_tokens.TryGetValue(documentId, out var terms))
        {
            foreach (var term in terms)
            {
                if (_postings.TryGetValue(term, out var posting) &&
                    posting.ByDocument.Remove(documentId) && posting.ByDocument.Count == 0)
                {
                    _postings.Remove(term);
                }

                _corpusFrequencies.TryGetValue(term, out int corpusCount);
                if (corpusCount <= 1)
                    _corpusFrequencies.Remove(term);
                else
                    _corpusFrequencies[term] = corpusCount - 1;
            }
        }

        _tokens.Remove(documentId);

        _totalTokens -= _lengthsByOrdinal[ordinal];

        // Free the slot rather than closing the list up: the ordinals other documents already hold
        // must stay valid, because a search that is running concurrently reads them.
        _lengthsByOrdinal[ordinal] = 0;
        _byOrdinal[ordinal] = null;
        _freeOrdinals.Add(ordinal);

        RemoveFromFieldStatistics(documentId);

        return true;
    }

    /// <summary>
    /// Marks every derived per-term posting copy stale. One integer bump rather than a walk over
    /// the vocabulary, so a bulk load does not pay a full sweep per document added; the copies
    /// themselves are dropped lazily, the next reader of each term replacing its own.
    /// </summary>
    private void InvalidateDerivedPostings()
    {
        Volatile.Write(ref _quantizedLengthsByOrdinal, null);
        _epoch++;
    }

    /// <summary>
    /// Drops one document from every per-field structure, and forgets a field once no document
    /// carries it any more, so <see cref="Fields"/> never reports a stale name.
    /// </summary>
    private void RemoveFromFieldStatistics(string documentId)
    {
        foreach (string field in _fieldFrequencies.Keys.ToList())
        {
            if (!_fieldFrequencies.TryGetValue(field, out var byTerm))
                continue;

            foreach (var entry in byTerm)
            {
                if (entry.Value.Remove(documentId) && entry.Value.Count == 0)
                    byTerm.Remove(entry.Key);
            }

            if (byTerm.Count == 0)
                _fieldFrequencies.Remove(field);
        }

        foreach (string field in _fieldLengths.Keys.ToList())
        {
            if (!_fieldLengths.TryGetValue(field, out var lengths))
                continue;

            if (!lengths.Remove(documentId, out int removedLength))
                continue;

            if (_fieldTotalTokens.TryGetValue(field, out long total) &&
                total - removedLength <= 0)
            {
                _fieldTotalTokens.Remove(field);
            }
            else
            {
                _fieldTotalTokens[field] = total - removedLength;
            }

            if (lengths.Count == 0)
            {
                _fieldLengths.Remove(field);
                _fieldTotalTokens.Remove(field);
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _postings.Clear();
        _tokens.Clear();
        _corpusFrequencies.Clear();
        _fieldFrequencies.Clear();
        _fieldLengths.Clear();
        _fieldTotalTokens.Clear();
        _byOrdinal.Clear();
        _freeOrdinals.Clear();
        _lengthsByOrdinal = [];
        _totalTokens = 0;
        InvalidateDerivedPostings();
    }

    /// <inheritdoc />
    public bool Contains(string documentId) => _documents.ContainsKey(documentId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId) =>
        _tokens.TryGetValue(documentId, out var terms)
            ? terms
            : Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        if (_postings.TryGetValue(term, out var posting) &&
            posting.ByDocument.TryGetValue(documentId, out var placed))
        {
            return placed.Positions;
        }

        return Array.Empty<int>();
    }

    /// <inheritdoc />
    public int DocumentFrequency(string term) =>
        _postings.TryGetValue(term, out var posting)
            ? posting.ByDocument.Count
            : 0;

    /// <inheritdoc />
    public int CorpusFrequency(string term) =>
        _corpusFrequencies.TryGetValue(term, out int count)
            ? count
            : 0;

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term) =>
        _postings.TryGetValue(term, out var posting) &&
        posting.ByDocument.TryGetValue(documentId, out var placed)
            ? placed.Positions.Count
            : 0;

    /// <inheritdoc />
    public int DocumentLength(string documentId) =>
        _documents.TryGetValue(documentId, out int ordinal)
            ? Quantized(_lengthsByOrdinal[ordinal])
            : 0;

    /// <summary>
    /// The length as <see cref="DocumentLengthQuantization"/> reports it: the exact one by default,
    /// or the byte-quantized one when the index was built to match an implementation that stores
    /// lengths that way.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored, so the option costs one branch per call on a path that already
    /// does a dictionary lookup and an array read. The arithmetic is
    /// <see cref="QuantizedLength"/>, verified against a published index to be exact over 0 to 3000.
    /// </remarks>
    private int Quantized(int exactLength)
    {
        if (_documentLengthQuantization == DocumentLengthQuantization.Exact)
            return exactLength;

        return QuantizedLength(exactLength);
    }

    /// <summary>
    /// The exact lengths, rounded — built once and dropped with the postings, so it cannot go stale
    /// behind a mutation. Null whenever the index reports exact lengths, and then never built.
    /// Published with a volatile write so a reader that observes the publication also observes the
    /// array's contents, the same guarantee <see cref="PostingList.Flat"/> gives its immutable copy.
    /// </summary>
    private int[] QuantizedLengths()
    {
        int[]? quantized = Volatile.Read(ref _quantizedLengthsByOrdinal);

        if (quantized is null)
        {
            int[] exact = _lengthsByOrdinal;

            quantized = new int[exact.Length];

            for (int i = 0; i < exact.Length; i++)
            {
                quantized[i] = QuantizedLength(exact[i]);
            }

            // Two readers can miss and both build; they build identical arrays from an unchanged
            // _lengthsByOrdinal, so the loser is wasted work, never a wrong length.
            Volatile.Write(ref _quantizedLengthsByOrdinal, quantized);
        }

        return quantized;
    }

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document)
    {
        if (_documents.TryGetValue(documentId, out int ordinal))
        {
            document = _byOrdinal[ordinal]!;
            return true;
        }

        document = null;
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || _postings.Count == 0)
            return Array.Empty<SearchDocument>();

        return terms.Count == 1
            ? EnumerateSingleTerm(terms[0])
            : EnumerateMultiTerm(terms);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The candidate set is identical to <see cref="GetCandidateDocuments"/>; only the order
    /// differs, and the search engine does not depend on it. See
    /// <see cref="IUnorderedCandidateIndex"/> for what the ordering pass costs.
    /// </remarks>
    IEnumerable<SearchDocument> IUnorderedCandidateIndex.GetCandidatesUnordered(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || _postings.Count == 0)
            return Array.Empty<SearchDocument>();

        // A single term comes out of its posting list in insertion order, which already is the
        // corpus order, so the two paths coincide for it and there is nothing to save.
        return terms.Count == 1
            ? EnumerateSingleTerm(terms[0])
            : EnumerateUnorderedMultiTerm(terms);
    }

    // A single term enumerates its posting list in document-insertion order, which is exactly
    // the corpus order — no candidate set needed, indistinguishable from a full scan.
    private IEnumerable<SearchDocument> EnumerateSingleTerm(string term)
    {
        if (!_postings.TryGetValue(term, out var posting) || posting.ByDocument.Count == 0)
            yield break;

        // The ordinal rides in the posting value, so a candidate costs one array read rather than
        // a string-hashed lookup of the id.
        var slots = _byOrdinal;

        foreach (var placed in posting.ByDocument.Values)
            yield return slots[placed.Ordinal]!;
    }

    private IEnumerable<SearchDocument> EnumerateMultiTerm(IReadOnlyList<string> terms)
    {
        var candidates = UnionCandidates(terms);

        if (candidates.Count == 0)
            yield break;

        // Re-enumerate the corpus so ties keep the corpus order, performing plain identity
        // lookups (no string hashing) against the local candidate set.
        var slots = _byOrdinal;

        for (int ordinal = 0; ordinal < slots.Count; ordinal++)
        {
            var document = slots[ordinal];

            if (document is not null && candidates.Contains(document))
                yield return document;
        }
    }

    private IEnumerable<SearchDocument> EnumerateUnorderedMultiTerm(IReadOnlyList<string> terms)
    {
        // The union is already deduplicated and complete; the order it comes out in is the
        // posting lists' own, which is what the caller asked for by calling this.
        foreach (var document in UnionCandidates(terms))
            yield return document;
    }

    /// <summary>
    /// Union of the terms' posting lists, each document once, into a set local to this call.
    /// </summary>
    /// <remarks>
    /// The set used to be a shared, epoch-stamped dictionary reused across queries, but that is
    /// a read-vs-read race: two concurrent searches mutate the same map, so one can overwrite
    /// the other's marks and drop (or leak) candidates. The marking state must never outlive
    /// the call, which also keeps it correct across interleaved enumerations.
    /// </remarks>
    private HashSet<SearchDocument> UnionCandidates(IReadOnlyList<string> terms)
    {
        var candidates = new HashSet<SearchDocument>(ReferenceEqualityComparer.Instance);
        var slots = _byOrdinal;

        // Per-query hot path: LINQ Where on these loops would allocate per candidate. // NOSONAR:S3267
        foreach (var term in terms)
        {
            if (_postings.TryGetValue(term, out var posting))
            {
                // Ordinal to document is an array read, so a candidate costs a reference add and a
                // read — no string hash anywhere in the walk.
                foreach (var placed in posting.ByDocument.Values)
                    candidates.Add(slots[placed.Ordinal]!);
            }
        }

        return candidates;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The whole of the term-at-a-time pass: resolve the term's list once, then walk it folding
    /// the weight in. No id is hashed anywhere in here — the loop reads a dictionary enumerator,
    /// a term frequency and the length the enumerator already brought into cache.
    /// <para>
    /// Generic over the weight and constrained to a <c>struct</c> on purpose. A
    /// <c>Func</c>-shaped or interface-shaped call here would be an indirect call per posting
    /// entry, which at these volumes costs more than everything else in the loop put together.
    /// </para>
    /// </remarks>
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator)
    {
        if (!_postings.TryGetValue(weight.Term, out var posting))
            return;

        var flat = posting.Flat;

        if (flat is null || flat.Epoch != _epoch)
        {
            flat = BuildFlatPosting(posting, _epoch);
            posting.PublishFlat(flat);
        }

        int[] ordinals = flat.Ordinals;
        int[] frequencies = flat.Frequencies;

        // The hot loop reads the length array directly rather than through DocumentLength, so the
        // quantized lengths have to be an array too. Rounding inside this loop would put an
        // arithmetic sequence on the innermost loop of the whole library for a convention that one
        // caller in one reproduction wants.
        int[] lengths = _documentLengthQuantization == DocumentLengthQuantization.Exact
            ? _lengthsByOrdinal
            : QuantizedLengths();
        int count = flat.Count;

        for (int i = 0; i < count; i++)
        {
            int ordinal = ordinals[i];
            accumulator.Record(ordinal, weight.Weight(frequencies[i], lengths[ordinal]));
        }
    }

    /// <summary>
    /// The contiguous copy of one posting list, ordered by document ordinal.
    /// </summary>
    /// <remarks>
    /// Sorting is what turns the accumulator's scatter into a mostly sequential one: the ordinals
    /// come out of the dictionary in hash order, which is close to random, and walking them in
    /// ordinal order lets the score writes sweep forward through the buffer instead of jumping
    /// across it. The sort also means the touched-ordinal list a search produces is ascending, so
    /// two runs over the same corpus visit the same documents in the same order.
    /// </remarks>
    private static FlatPosting BuildFlatPosting(PostingList posting, int epoch)
    {
        int count = posting.ByDocument.Count;
        var ordinals = new int[count];
        var frequencies = new int[count];

        int i = 0;

        foreach (var placed in posting.ByDocument.Values)
        {
            ordinals[i] = placed.Ordinal;
            frequencies[i] = placed.Positions.Count;
            i++;
        }

        Array.Sort(ordinals, frequencies);

        return new FlatPosting(epoch, ordinals, frequencies);
    }

    /// <inheritdoc />
    public int OrdinalSpace => _byOrdinal.Count;

    /// <inheritdoc />
    public SearchDocument? DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < _byOrdinal.Count ? _byOrdinal[ordinal] : null;

    /// <inheritdoc />
    public TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);

    /// <inheritdoc />
    /// <remarks>
    /// The default field always comes first, the named ones after it in ordinal order, so the
    /// sequence is stable across runs and safe to assert on.
    /// </remarks>
    public IReadOnlyCollection<string> Fields
    {
        get
        {
            // The default field lives in the same map as the named ones, so it has to be filtered
            // out here rather than prepended blindly — it is not in `named`.
            if (_fieldLengths.Count == 0)
                return [TextFields.Default];

            var named = _fieldLengths.Keys
                .Where(name => name != TextFields.Default)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var fields = new string[named.Length + 1];
            fields[0] = TextFields.Default;
            named.CopyTo(fields, 1);
            return fields;
        }
    }

    /// <summary>
    /// Implements <see cref="IFieldStatisticsIndex"/>: this index always tracks per-field statistics.
    /// </summary>
    public int FieldTermFrequency(string documentId, string field, string term)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        TextFields.Validate(field, nameof(field));
        ArgumentNullException.ThrowIfNull(term);

        return _fieldFrequencies.TryGetValue(field, out var byTerm) &&
               byTerm.TryGetValue(term, out var byDocument) &&
               byDocument.TryGetValue(documentId, out int count)
            ? count
            : 0;
    }

    /// <inheritdoc />
    public int FieldLength(string documentId, string field)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        TextFields.Validate(field, nameof(field));

        return _fieldLengths.TryGetValue(field, out var lengths) &&
               lengths.TryGetValue(documentId, out int length)
            ? length
            : 0;
    }

    /// <inheritdoc />
    public double AverageFieldLength(string field)
    {
        TextFields.Validate(field, nameof(field));

        if (!_fieldLengths.TryGetValue(field, out var lengths) || lengths.Count == 0)
            return 0;

        return (double)(_fieldTotalTokens.TryGetValue(field, out long total) ? total : 0)
            / lengths.Count;
    }

    /// <inheritdoc />
    public int FieldDocumentFrequency(string field, string term)
    {
        TextFields.Validate(field, nameof(field));
        ArgumentNullException.ThrowIfNull(term);

        return _fieldFrequencies.TryGetValue(field, out var byTerm) &&
               byTerm.TryGetValue(term, out var byDocument)
            ? byDocument.Count
            : 0;
    }
}
/// <summary>
/// Which document count divides the corpus token count when <see cref="InMemoryTextIndex.AverageDocumentLength"/>
/// is computed.
/// </summary>
public enum AverageLengthDivisor
{
    /// <summary>Every indexed document, including one that holds no term. The library's default.</summary>
    AllDocuments = 0,

    /// <summary>
    /// Only the documents that hold at least one term <b>as the index stores the length</b>, and the
    /// total is the sum of those stored lengths rather than the exact ones. Default: see
    /// <see cref="InMemoryTextIndex"/>.
    /// </summary>
    /// <remarks>
    /// The two settings of <see cref="DocumentLengthQuantization"/> make these three conventions differ
    /// by more than the one part in ten thousand the divisor choice is worth, and the difference is not a
    /// rounding detail. Measured on BEIR ArguAna against the reference implementation's own index, read
    /// out of it rather than assumed: its average document length is <b>108.372651</b>, the mean of the
    /// decoded one-byte lengths (939,916 over 8,673 documents). The mean of the exact lengths is
    /// <b>111.786925</b> — 3.05% higher — so an index that stores quantized lengths and averages the
    /// exact ones is scoring with a length normalisation no implementation uses.
    /// </remarks>
    NonEmptyDocumentsStoredLengths = 2,

    /// <summary>
    /// Only the documents that hold at least one term. The convention the published BM25 baselines
    /// use, so a corpus with an empty document and a target produced under that convention need it.
    /// </summary>
    NonEmptyDocuments = 1,
}

/// <summary>
/// How <see cref="InMemoryTextIndex.DocumentLength"/> reports a document's length.
/// </summary>
/// <remarks>
/// Internal because it is not a setting a caller might reasonably want. <c>OneByte</c> does not
/// report a length more precisely or less precisely — it reports a <em>different quantity</em>, and
/// nothing about the call site says which of the two it is comparing against. A public option here
/// would let a caller depend on a unit whose meaning depends on how an index was built, which is
/// the kind of dependency that surfaces as a wrong number rather than as a compile error. It stays
/// reachable from the evaluation harness, which is the only thing that has a reason to want it.
/// </remarks>
internal enum DocumentLengthQuantization
{
    /// <summary>
    /// The length the tokenizer produced, unrounded. The default, and the only one of the two that is
    /// a measurement of anything.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// The length rounded down to the nearest value an index storing one byte per document can
    /// represent.
    /// </summary>
    /// <remarks>
    /// Not an improvement: it is a rounding, it always rounds down, and it makes scores slightly
    /// higher. It exists for one purpose — a caller reproducing a figure published by an
    /// implementation that stores lengths this way gets a different ranking unless their lengths are
    /// rounded the same way, and a difference in ranking is not a difference anyone can argue away.
    /// Measured on BEIR ArguAna at k1=0.9 and b=0.4, it moves BM25 nDCG@10 by about -0.006 and
    /// recall@100 by about -0.002, so it is worse by both measures here.
    /// </remarks>
    OneByte = 1,
}
