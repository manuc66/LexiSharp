using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Hybrid;

/// <summary>
/// A model-free sparse encoder that augments a text with its <b>n-gram phrases</b> (bigrams and
/// trigrams) as weighted virtual terms, so that a lexical engine can match multi-word concepts
/// without a learned model.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> a learned sparse model (SPLADE, uniCOIL, ...). It does not require a
/// model, a vocabulary file, or a network call — it is BCL-only and runs in microseconds. What
/// it buys is phrase-level matching inside the same inverted index a lexical engine already
/// uses: a query for "machine learning" can match a document containing "machine learning
/// algorithms" even when the document never contains the exact two-word phrase as a single token.
/// </para>
/// <para>
/// The encoder implements <see cref="ISparseEmbeddingProvider"/> so it plugs into the same seam
/// as a learned sparse model: a consumer can swap it for a real SPLADE model behind the same
/// interface without changing the pipeline. Weights are term-frequency-based (TF), non-negative,
/// and pruned to the top <see cref="MaxTerms"/> terms to keep the vector sparse.
/// </para>
/// <para>
/// Because the encoder is symmetric (query and document use the same logic), it works for both
/// <see cref="EmbeddingUse.Query"/> and <see cref="EmbeddingUse.Passage"/>.
/// </para>
/// </remarks>
public sealed class SparseSemanticEncoder : ISparseEmbeddingProvider
{
    private readonly ITokenizer _tokenizer;
    private readonly int _maxNgram;
    private readonly int _maxTerms;

    /// <param name="tokenizer">Tokenizer used to split text; defaults to <see cref="Tokenizer.Default"/>.</param>
    /// <param name="maxNgram">Maximum n-gram size (2 = bigrams, 3 = trigrams). Default 3.</param>
    /// <param name="maxTerms">Prune to the top N terms by weight. Default 64.</param>
    public SparseSemanticEncoder(ITokenizer? tokenizer = null, int maxNgram = 3, int maxTerms = 64)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
        if (maxNgram < 1 || maxNgram > 4)
            throw new ArgumentOutOfRangeException(nameof(maxNgram), maxNgram, "maxNgram must be in [1, 4].");
        if (maxTerms < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTerms), maxTerms, "maxTerms must be at least 1.");
        _maxNgram = maxNgram;
        _maxTerms = maxTerms;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
        string text,
        EmbeddingUse use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var tokens = _tokenizer.Tokenize(text);
        if (tokens.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, float>>(new Dictionary<string, float>());

        // Term frequencies for unigrams.
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            if (!tf.TryGetValue(t, out int c)) c = 0;
            tf[t] = c + 1;
        }

        // N-gram frequencies.
        var ngramTf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int n = 2; n <= _maxNgram; n++)
        {
            for (int i = 0; i + n <= tokens.Count; i++)
            {
                string phrase = string.Join(" ", tokens.Skip(i).Take(n));
                if (!ngramTf.TryGetValue(phrase, out int c)) c = 0;
                ngramTf[phrase] = c + 1;
            }
        }

        // Combine: unigrams (weight 1.0) + n-grams (weight n, so phrases dominate).
        var combined = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (term, count) in tf)
            combined[term] = count; // unigram weight = TF
        foreach (var (phrase, count) in ngramTf)
            combined[phrase] = combined.TryGetValue(phrase, out float existing)
                ? existing + count * _maxNgram
                : count * _maxNgram;

        // Prune to top maxTerms by weight.
        var pruned = combined
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(_maxTerms)
            .ToDictionary(kv => kv.Key, kv => (float)kv.Value);

        return Task.FromResult<IReadOnlyDictionary<string, float>>(pruned);
    }

    /// <summary>Maximum number of terms retained per encoding.</summary>
    public int MaxTerms => _maxTerms;
}