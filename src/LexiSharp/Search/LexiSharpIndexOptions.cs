using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp;

/// <summary>
/// Configuration of a <see cref="LexiSharpIndex{TDocument}"/>: how caller documents map to
/// indexable records, and which engine pieces to use.
/// </summary>
/// <typeparam name="TDocument">The caller's document type; must be a reference type.</typeparam>
public sealed class LexiSharpIndexOptions<TDocument>
    where TDocument : class
{
    /// <summary>
    /// Extracts the stable document id. When unset and <typeparamref name="TDocument"/> is
    /// <see cref="string"/> or <see cref="SearchDocument"/>, a sensible default applies
    /// (the string itself, or the document's <see cref="SearchDocument.Id"/>); for any other
    /// type the selector is required.
    /// </summary>
    public Func<TDocument, string>? Id { get; set; }

    /// <summary>
    /// Extracts the text to index. When unset and <typeparamref name="TDocument"/> is
    /// <see cref="string"/> or <see cref="SearchDocument"/>, a sensible default applies
    /// (the string itself, or the document's <see cref="SearchDocument.Text"/>); for any other
    /// type the selector is required.
    /// </summary>
    public Func<TDocument, string>? Text { get; set; }

    /// <summary>
    /// Extracts the structured metadata fields of the document (faceted and filterable by the
    /// underlying engine). When <typeparamref name="TDocument"/> is <see cref="SearchDocument"/>,
    /// defaults to its <see cref="SearchDocument.Fields"/>.
    /// </summary>
    public Func<TDocument, IReadOnlyDictionary<string, string>?>? Fields { get; set; }

    /// <summary>
    /// Extracts the document category, used for supervised classification. When
    /// <typeparamref name="TDocument"/> is <see cref="SearchDocument"/>, defaults to its
    /// <see cref="SearchDocument.Category"/>.
    /// </summary>
    public Func<TDocument, string?>? Category { get; set; }

    /// <summary>
    /// Extracts the document's named text fields, so an index built through the typed facade can
    /// answer per-field statistics (<see cref="IReadOnlyTextIndex.Fields"/>) and feed a field-weighted
    /// scorer. When <typeparamref name="TDocument"/> is <see cref="SearchDocument"/>, defaults to
    /// its <see cref="SearchDocument.TextFields"/>.
    /// </summary>
    /// <remarks>
    /// A field's tokens are also part of the flat view, so <see cref="Text"/> and these fields are
    /// both searchable by a plain BM25 query; what the fields add is per-field weighting, not
    /// visibility. An empty or null result means the document declares no text field, which is
    /// not an error.
    /// </remarks>
    public Func<TDocument, IReadOnlyDictionary<string, string>?>? TextFields { get; set; }

    /// <summary>
    /// When set, the index is a <see cref="SegmentedTextIndex"/> over this directory rather than an
    /// in-memory <see cref="InMemoryTextIndex"/>: the corpus is written to immutable segments and
    /// survives a restart without being re-tokenized. <see cref="LexiSharpIndex{TDocument}.Commit"/>
    /// makes the pending writes durable and <see cref="LexiSharpIndex{TDocument}.Merge"/> reclaims the
    /// deleted ones. Default: <c>null</c> (in memory, nothing on disk).
    /// </summary>
    /// <remarks>
    /// A store cannot carry <see cref="TermExpander"/> (its index-time expansion has no store to write
    /// through) and it keeps no per-field statistics, so <see cref="TextFields"/> reaches a plain BM25
    /// query but not a field-weighted scorer. A reopened store does not hold the caller's documents, so
    /// set <see cref="Load"/> for any <typeparamref name="TDocument"/> other than
    /// <see cref="SearchDocument"/>.
    /// </remarks>
    public string? Directory { get; set; }

    /// <summary>
    /// How a persistent index opens the segments already in <see cref="Directory"/>.
    /// <see cref="SegmentSourceMode.Array"/> (the default) reads each segment into memory;
    /// <see cref="SegmentSourceMode.Mapped"/> maps it and makes the index
    /// <see cref="IDisposable"/>. Ignored without <see cref="Directory"/>.
    /// </summary>
    public SegmentSourceMode SourceMode { get; set; } = SegmentSourceMode.Array;

    /// <summary>
    /// Posting entries each segment of a persistent index may hold decoded, or <c>null</c> for none.
    /// A repeat look-up of a hot term then walks flat arrays instead of decoding the block planes.
    /// The budget is per segment. Ignored without <see cref="Directory"/>.
    /// </summary>
    public int? DecodedCacheEntries { get; set; }

    /// <summary>
    /// Maps a document id back to the caller's document, for a hit the facade does not hold — which is
    /// every document a reopened store carries. When <typeparamref name="TDocument"/> is
    /// <see cref="SearchDocument"/> it defaults to the index's own document; for any other type it is
    /// required to search a store the caller did not add this session. Default: <c>null</c>.
    /// </summary>
    public Func<string, TDocument?>? Load { get; set; }


    /// <summary>The ranking strategy. Defaults to Okapi BM25 (<see cref="Bm25Scorer"/>).</summary>
    public ITextScorer Scorer { get; set; } = new Bm25Scorer();

    /// <summary>
    /// Tokenizer for documents and queries. Defaults to an <see cref="Tokenizer"/> built from
    /// <see cref="RemoveStopWords"/>, <see cref="Stemmer"/> and <see cref="NGramMax"/>, or the
    /// stock tokenizer when every knob is at its default.
    /// </summary>
    public ITokenizer? Tokenizer { get; set; }

    /// <summary>
    /// Optional « semantic lexical » expansion: every indexed document is also filed under the
    /// weak terms this expander derives from its own tokens (see
    /// <see cref="PmiTermExpander"/> for a corpus-learned, model-free implementation). Queries
    /// then reach conceptually related documents they never mention literally, while the whole
    /// index stays a plain inverted structure. Default: <c>null</c> (no expansion).
    /// </summary>
    public ITermExpander? TermExpander { get; set; }

    /// <summary>
    /// Optional index-time context enrichment: every document's searchable text passes through
    /// this enricher before it is tokenized (see <see cref="ContextEnrichingIndex"/>), so the
    /// corpus that is scored can carry context the document does not spell out — a chunk saying
    /// "the benefit rose 12%" is reworded with the company and year its fields or an LLM
    /// provide. The caller's original document stays the one results highlight and display.
    /// Default: <c>null</c> (no enrichment).
    /// </summary>
    public IChunkContextEnricher? ContextEnricher { get; set; }

    /// <summary>Optional synonym edges applied to free query terms.</summary>
    public SynonymMap? Synonyms { get; set; }

    /// <summary>
    /// Optional second-stage reranker (e.g. MMR). When set, search over-fetches candidates,
    /// re-ranks them and trims to the requested limit.
    /// </summary>
    public IReranker? Reranker { get; set; }

    /// <summary>
    /// Optional pre-retrieval query transformer (see
    /// <see cref="Expansion.TransformingTextSearchEngine"/>): the raw query is rewritten into
    /// one or more variants — a rewrite, sub-queries of a decomposed question, lexical
    /// variants, or a HyDE-style generated answer — each searched against the same engine and
    /// fused by best score per document. A broken transformer falls back to the untransformed
    /// query. Default: <c>null</c> (no transformation).
    /// </summary>
    public IQueryTransformer? QueryTransformer { get; set; }

    /// <summary>Candidates fetched from the base engine for <see cref="Reranker"/> (default: 50).</summary>
    public int RerankerMaxCandidates { get; set; } = 50;

    /// <summary>
    /// Optional observability sink, handed to every engine this index builds. Set it to log search
    /// latency and candidate counts, to collect metrics, or to do both; leave it <c>null</c> (the
    /// default) and no engine reads a clock or records anything.
    /// </summary>
    public RetrievalTelemetry? Telemetry { get; set; }

    /// <summary>
    /// When set, plain queries are rewritten so every term is fuzzy-matched
    /// (<c>term</c> behaves like <c>term~1</c>). Queries that already carry an explicit
    /// <c>*</c>/<c>~</c> operator or a quoted phrase are left untouched.
    /// </summary>
    public bool EnableFuzzy { get; set; }

    /// <summary>
    /// Sharpens <see cref="EnableFuzzy"/> (and explicit <c>term~</c> operators): a fuzzy term
    /// whose exact form is already in the index vocabulary matches that exact form only, so
    /// only genuinely unknown words are corrected. Correctly spelled tokens no longer pull in
    /// unrelated close variants and cannot be degraded by the fuzzy rewrite.
    /// </summary>
    public bool FuzzyOnlyOutOfVocabulary { get; set; }

    /// <summary>Drop stop words during tokenization. Default: <c>false</c>.</summary>
    public bool RemoveStopWords { get; set; }

    /// <summary>Optional stemmer applied to every term. Default: <c>null</c> (no stemming).</summary>
    public IStemmer? Stemmer { get; set; }

    /// <summary>Largest n-gram size produced. Default: <c>1</c> (unigrams only).</summary>
    public int NGramMax { get; set; } = 1;

    /// <summary>Switches the scorer to Okapi BM25. Returns this instance (fluent).</summary>
    public LexiSharpIndexOptions<TDocument> UseBm25(double k1 = 1.5, double b = 0.75)
    {
        Scorer = new Bm25Scorer(k1, b);
        return this;
    }

    /// <summary>Switches the scorer to TF-IDF. Returns this instance (fluent).</summary>
    public LexiSharpIndexOptions<TDocument> UseTfIdf()
    {
        Scorer = new TfIdfScorer();
        return this;
    }

    /// <summary>Switches the scorer to the query-likelihood language model. Returns this instance (fluent).</summary>
    public LexiSharpIndexOptions<TDocument> UseQueryLikelihood(double lambda = 0.2)
    {
        Scorer = new QueryLikelihoodScorer(lambda);
        return this;
    }

    /// <summary>Switches the scorer to a boolean match. Returns this instance (fluent).</summary>
    public LexiSharpIndexOptions<TDocument> UseBoolean(BooleanMatch match = BooleanMatch.AllTerms)
    {
        Scorer = new BooleanScorer(match);
        return this;
    }
}