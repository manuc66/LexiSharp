using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Controls how a <see cref="CascadeRerankPipeline"/> passes candidates from stage to stage.
/// </summary>
/// <param name="StageLimit">
/// Maximum number of candidates forwarded from one stage to the next; <c>null</c> disables the
/// intermediate cuts. Cutting after every stage is what makes the later — usually more expensive
/// — stages cheap. Must be <c>null</c> or at least <c>1</c>.
/// </param>
/// <param name="FinalLimit">Maximum number of results kept after the last stage; <c>null</c> keeps all. Must be <c>null</c> or at least <c>1</c>.</param>
/// <param name="MinimumScore">Results scoring below this after the last stage are dropped; must not be NaN.</param>
public sealed record CascadeRerankOptions(
    int? StageLimit = null,
    int? FinalLimit = null,
    double MinimumScore = double.NegativeInfinity)
{
    /// <summary>The defaults every positional parameter already has: keep 20 between stages, return 5.</summary>
    public static readonly CascadeRerankOptions Default = new();
}

/// <summary>
/// Chains rerankers in cascade: each stage re-ranks the output of the previous one, and the
/// shortlist may be trimmed between stages so only the strongest candidates reach the
/// expensive final stages.
/// </summary>
/// <remarks>
/// <para>
/// The canonical two-stage topology pairs a cheap recall stage (lexical re-scoring) with a
/// precise but costly final stage (cross-encoder, LLM judge, ...): retrieval breadth is decided
/// once, precision is layered on top. Stages are free to mix strategies — a diversity pass may
/// precede or follow a relevance pass, since <see cref="IReranker"/> only fixes the contract,
/// not the method.
/// </para>
/// <para>
/// Because the pipeline itself implements <see cref="IReranker"/>, cascades nest arbitrarily.
/// As everywhere in LexiSharp, a final score of exactly <c>0</c> means "not a match" and NaN or
/// infinite scores are dropped. The input list is never mutated.
/// </para>
/// </remarks>
public sealed class CascadeRerankPipeline : IReranker, IRerankerExplainer
{
    private readonly IReadOnlyList<IReranker> _stages;
    private readonly CascadeRerankOptions _options;

    /// <param name="stages">Ordered reranking stages; at least one is required.</param>
    /// <param name="options">Flow control between stages (defaults: no cuts, no threshold).</param>
    public CascadeRerankPipeline(IReadOnlyList<IReranker> stages, CascadeRerankOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var materialized = stages.ToList();

        if (materialized.Count == 0)
            throw new ArgumentException("At least one stage is required.", nameof(stages));

        if (materialized.Contains(null!))
            throw new ArgumentException("Stages must not contain null.", nameof(stages));

        _stages = materialized;
        _options = options ?? CascadeRerankOptions.Default;

        if (_options.StageLimit is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.StageLimit, "StageLimit must be null or at least 1.");

        if (_options.FinalLimit is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.FinalLimit, "FinalLimit must be null or at least 1.");

        if (double.IsNaN(_options.MinimumScore))
            throw new ArgumentOutOfRangeException(nameof(options), _options.MinimumScore, "MinimumScore must not be NaN.");
    }

    /// <inheritdoc />
    public string Name => "Cascade";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
        => RerankCore(query, candidates, null).Results;

    private (IReadOnlyList<SearchResult> Results, Dictionary<string, List<(string Stage, double Score)>> StageScores) RerankCore(
        string query,
        IReadOnlyList<SearchResult> candidates,
        Dictionary<string, List<(string Stage, double Score)>>? stageScores)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return (Array.Empty<SearchResult>(), stageScores ?? new Dictionary<string, List<(string, double)>>());

        var captured = stageScores ?? new Dictionary<string, List<(string, double)>>();

        // Seed every candidate with its incoming score as stage "input".
        foreach (var c in candidates)
        {
            if (!captured.ContainsKey(c.DocumentId))
                captured[c.DocumentId] = new List<(string, double)>();
            captured[c.DocumentId].Add(("input", c.Score));
        }

        IReadOnlyList<SearchResult> current = candidates;

        foreach (var stage in _stages)
        {
            current = stage.Rerank(query, current);

            foreach (var r in current)
            {
                if (captured.TryGetValue(r.DocumentId, out var list))
                    list.Add((stage.Name, r.Score));
            }

            if (_options.StageLimit is int stageLimit && stageLimit < current.Count)
                current = current.Take(stageLimit).ToList();
        }

        var results = current
            .Where(x => !double.IsNaN(x.Score) && !double.IsInfinity(x.Score)
                        && x.Score != 0 && x.Score >= _options.MinimumScore)
            .Take(_options.FinalLimit ?? int.MaxValue)
            .ToList();

        return (results, captured);
    }

    /// <inheritdoc />
    public RerankExplanation? ExplainRerank(string query, IReadOnlyList<SearchResult> candidates, string documentId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!candidates.Any(c => c.DocumentId == documentId))
            return null;

        var stageScores = new Dictionary<string, List<(string Stage, double Score)>>();
        var (results, _) = RerankCore(query, candidates, stageScores);

        if (!stageScores.TryGetValue(documentId, out var stages))
            return null;

        var signals = stages
            .Select(s => new RerankSignal(s.Stage, 1.0, s.Score, s.Score))
            .ToList();

        var picked = results.FirstOrDefault(r => r.DocumentId == documentId);
        double finalScore = results.Any(r => r.DocumentId == documentId) ? picked.Score : 0;
        // results is IReadOnlyList<SearchResult> (struct); use Any to check presence.
        bool present = results.Any(r => r.DocumentId == documentId);
        if (!present) finalScore = 0;

        string summary = "Cascade: " + documentId + " went through " + stages.Count + " stage(s): " +
            string.Join(" -> ", stages.Select(s => s.Stage + "=" + s.Score.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + ".";

        return new RerankExplanation(documentId, finalScore, signals, summary);
    }
}
