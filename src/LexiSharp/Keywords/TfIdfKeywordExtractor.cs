using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Keywords;

/// <summary>
/// Keyword extraction by smoothed TF-IDF: a term's score is its frequency inside the text,
/// optionally discounted by how common the term is across a reference corpus.
/// </summary>
/// <remarks>
/// Built without a corpus, the score degenerates to term frequency alone — still useful for
/// tags, but unable to tell "important here" from "common everywhere". Build it over an
/// <see cref="ITextIndex"/> (the same one the search engine uses, or a sample of the domain)
/// and corpus-frequent words are demoted by the smoothed IDF
/// <c>log((N + 1) / (df + 1)) + 1</c> — the same weighting the
/// <see cref="LexiSharp.Ranking.TfIdfScorer"/> uses.
/// Terms never seen by the corpus get the maximum IDF, so proper nouns surface first.
/// </remarks>
public sealed class TfIdfKeywordExtractor : IKeywordExtractor
{
    private readonly ITextIndex? _corpus;
    private readonly ITokenizer _tokenizer;

    /// <param name="corpus">
    /// Optional reference index for IDF weighting; terms absent from it are treated as maximally
    /// specific. Pass <c>null</c> to score by frequency only.
    /// </param>
    /// <param name="tokenizer">Tokenizer applied to the mined text; keep it consistent with the corpus's.</param>
    public TfIdfKeywordExtractor(ITextIndex? corpus = null, ITokenizer? tokenizer = null)
    {
        _corpus = corpus;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public string Name => "TF-IDF";

    /// <inheritdoc />
    public IReadOnlyList<Keyword> Extract(string text, int topN = 10)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var tokens = _tokenizer.Tokenize(text);

        if (tokens.Count == 0)
            return Array.Empty<Keyword>();

        // Term frequency inside the text; appearance order is kept for deterministic ties.
        var frequencies = new Dictionary<string, double>(StringComparer.Ordinal);
        var appearance = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (frequencies.TryGetValue(token, out var count))
            {
                frequencies[token] = count + 1;
            }
            else
            {
                frequencies[token] = 1;
                appearance[token] = appearance.Count;
            }
        }

        var keywords = new List<Keyword>(frequencies.Count);

        foreach (var (term, tf) in frequencies)
        {
            double idf = _corpus is null
                ? 1.0
                : Math.Log((_corpus.Count + 1.0) / (_corpus.DocumentFrequency(term) + 1.0)) + 1.0;

            keywords.Add(new Keyword(term, tf * idf));
        }

        return keywords
            .OrderByDescending(k => k.Score)
            .ThenBy(k => appearance[k.Term]) // first appearance wins ties
            .Take(topN)
            .ToList();
    }
}
