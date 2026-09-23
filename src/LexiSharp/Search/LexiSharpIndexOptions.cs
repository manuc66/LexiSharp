using LexiSharp.Core;
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
    public Func<TDocument, IReadOnlyDictionary<string, string>>? Fields { get; set; }

    /// <summary>
    /// Extracts the document category, used for supervised classification. When
    /// <typeparamref name="TDocument"/> is <see cref="SearchDocument"/>, defaults to its
    /// <see cref="SearchDocument.Category"/>.
    /// </summary>
    public Func<TDocument, string>? Category { get; set; }

    /// <summary>The ranking strategy. Defaults to Okapi BM25 (<see cref="Bm25Scorer"/>).</summary>
    public ITextScorer Scorer { get; set; } = new Bm25Scorer();

    /// <summary>
    /// Tokenizer for documents and queries. Defaults to an <see cref="Tokenizer"/> built from
    /// <see cref="RemoveStopWords"/>, <see cref="Stemmer"/> and <see cref="NGramMax"/>, or the
    /// stock tokenizer when every knob is at its default.
    /// </summary>
    public ITokenizer? Tokenizer { get; set; }

    /// <summary>Optional synonym edges applied to free query terms.</summary>
    public SynonymMap? Synonyms { get; set; }

    /// <summary>
    /// Optional second-stage reranker (e.g. MMR). When set, search over-fetches candidates,
    /// re-ranks them and trims to the requested limit.
    /// </summary>
    public IReranker? Reranker { get; set; }

    /// <summary>Candidates fetched from the base engine for <see cref="Reranker"/> (default: 50).</summary>
    public int RerankerMaxCandidates { get; set; } = 50;

    /// <summary>
    /// When set, plain queries are rewritten so every term is fuzzy-matched
    /// (<c>term</c> behaves like <c>term~1</c>). Queries that already carry an explicit
    /// <c>*</c>/<c>~</c> operator or a quoted phrase are left untouched.
    /// </summary>
    public bool EnableFuzzy { get; set; }

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