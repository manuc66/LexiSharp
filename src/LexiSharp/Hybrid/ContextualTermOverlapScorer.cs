using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Hybrid;

/// <summary>
/// A model-free <see cref="IContextualEvidenceScorer"/> used to exercise the multi-hop expansion
/// stack with no external dependency: it scores a candidate by the fraction of distinct query
/// terms it contains (a lexical necessity proxy), ignoring the supplied context. Replace it with
/// a real decision model (jevos via OpenVINO, Von, Decider, ...) behind the same interface to
/// make the chain truly contextual.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> a semantic model and does not implement the contextual judgment of the
/// reference architecture: it cannot recognise a "bridge" passage whose value only appears once
/// an intermediate fact has been retrieved. It exists to make the contextual-evidence seam usable
/// out of the box — a baseline, a fixture for tests, and a placeholder the consumer swaps for a
/// real model.
/// </para>
/// </remarks>
public sealed class ContextualTermOverlapScorer : IContextualEvidenceScorer
{
    private readonly ITokenizer _tokenizer;

    /// <param name="tokenizer">Tokenizer used to split text into terms; defaults to <see cref="Tokenizer.Default"/>.</param>
    public ContextualTermOverlapScorer(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public string Name => "ContextualTermOverlap";

    /// <inheritdoc />
    public double Score(string query, SearchDocument candidate, IReadOnlyList<SearchResult> context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var queryTerms = _tokenizer.Tokenize(query);

        if (queryTerms.Count == 0)
            return 0;

        var distinctQuery = new HashSet<string>(queryTerms, StringComparer.Ordinal);
        var candidateTerms = new HashSet<string>(_tokenizer.Tokenize(candidate.Text), StringComparer.Ordinal);

        int matched = 0;
        foreach (var term in distinctQuery)
        {
            if (candidateTerms.Contains(term))
                matched++;
        }

        return matched == 0 ? 0 : (double)matched / distinctQuery.Count;
    }
}