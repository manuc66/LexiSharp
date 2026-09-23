using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Demo;

/// <summary>
/// A model-free <see cref="ICrossEncoderScorer"/> used to demonstrate the reranking seam without
/// any external dependency: it scores a document by the fraction of distinct query terms it
/// contains. Replace it with a real cross-encoder model (ONNX, a remote API, ...) behind the
/// same interface to make the last lane truly neural.
/// </summary>
public sealed class OverlapCrossEncoder : ICrossEncoderScorer
{
    private readonly ITokenizer _tokenizer;

    public OverlapCrossEncoder(ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        _tokenizer = tokenizer;
    }

    /// <inheritdoc />
    public string Name => "QueryTermOverlap";

    /// <inheritdoc />
    public double Score(string query, SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);

        var queryTerms = _tokenizer.Tokenize(query);

        if (queryTerms.Count == 0)
            return 0;

        var documentTerms = new HashSet<string>(_tokenizer.Tokenize(document.Text), StringComparer.Ordinal);
        int matched = 0;

        for (int i = 0; i < queryTerms.Count; i++)
        {
            if (documentTerms.Contains(queryTerms[i]))
                matched++;
        }

        // Coverage in [0, 1]; scaled to 100 so the score reads like a percentage in the UI.
        return matched == 0 ? 0 : 100.0 * matched / queryTerms.Count;
    }
}
