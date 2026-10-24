using LexiSharp.Linguistics;

namespace LexiSharp.Keywords;

/// <summary>
/// Keyword extraction with the graph-based TextRank algorithm: terms are nodes, co-occurring
/// terms within a sliding window are edges, and the PageRank of each node becomes the keyword
/// score. No corpus is needed — the text's own structure provides the weighting.
/// </summary>
/// <remarks>
/// <para>
/// <c>score(v) = (1 − d)/N + d · Σ_{u→v} w(u,v) · score(u) / Σ_{u→·} w(u,·)</c>, iterated until
/// scores stabilize (or <c>maxIterations</c> is reached), with <c>d = 0.85</c>. Edge weights
/// count how often two terms co-occur inside the window, so a hub term repeated next to many
/// different terms — typically the subject of the text — wins, and its frequent neighbours
/// follow.
/// </para>
/// <para>
/// Filter what enters the graph through the tokenizer: pass a <see cref="Tokenizer"/> built
/// with stop word removal (and optionally stemming) so particles do not compete for rank.
/// Scores are PageRank values and do not sum to 1; they are comparable within a single
/// extraction, not across texts. Deterministic: same text, same tokens, same result.
/// </para>
/// </remarks>
public sealed class TextRankKeywordExtractor : IKeywordExtractor
{
    private readonly ITokenizer _tokenizer;
    private readonly int _windowSize;
    private readonly double _damping;
    private readonly int _maxIterations;
    private readonly double _tolerance;

    /// <param name="tokenizer">Tokenizer applied to the mined text (configure stop word removal here).</param>
    /// <param name="windowSize">Co-occurrence window (adjacent pairs when 2). Must be at least 2.</param>
    public TextRankKeywordExtractor(
        ITokenizer? tokenizer = null,
        int windowSize = 2)
    {
        if (windowSize < 2)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "windowSize must be at least 2.");

        _tokenizer = tokenizer ?? Tokenizer.Default;
        _windowSize = windowSize;
        _damping = 0.85;
        _maxIterations = 100;
        _tolerance = 1e-6;
    }

    /// <inheritdoc />
    public string Name => "TextRank";

    /// <inheritdoc />
    public IReadOnlyList<Keyword> Extract(string text, int topN = 10)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var tokens = _tokenizer.Tokenize(text);

        if (tokens.Count == 0)
            return Array.Empty<Keyword>();

        var (nodes, weights) = BuildGraph(tokens);

        if (nodes.Count == 1)
            return new[] { new Keyword(nodes[0], 1.0) };

        var scores = PageRank(weights, nodes.Count);

        // Node order = first appearance order, so ranking is deterministic: first appearance wins ties.
        return Enumerable.Range(0, nodes.Count)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i)
            .Take(topN)
            .Select(i => new Keyword(nodes[i], scores[i]))
            .ToList();
    }

    private (List<string> Nodes, double[][] Weights) BuildGraph(IReadOnlyList<string> tokens)
    {
        // Node order = first appearance order, for deterministic iteration and tie-breaking.
        var nodes = new List<string>();
        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (!nodeIndex.TryGetValue(token, out var index))
            {
                index = nodes.Count;
                nodeIndex[token] = index;
                nodes.Add(token);
            }
        }

        // Symmetric weighted adjacency from the sliding co-occurrence window.
        var weights = new double[nodes.Count][];

        for (int i = 0; i < nodes.Count; i++)
            weights[i] = new double[nodes.Count];

        for (int i = 0; i < tokens.Count; i++)
        {
            int a = nodeIndex[tokens[i]];
            int max = Math.Min(i + _windowSize, tokens.Count);

            for (int j = i + 1; j < max; j++)
            {
                int b = nodeIndex[tokens[j]];

                if (a != b)
                {
                    weights[a][b] += 1;
                    weights[b][a] += 1;
                }
            }
        }

        return (nodes, weights);
    }

    private double[] PageRank(double[][] weights, int nodeCount)
    {
        var outWeights = ComputeOutDegrees(weights, nodeCount);

        // PageRank from a uniform distribution.
        var scores = new double[nodeCount];
        Array.Fill(scores, 1.0 / nodeCount);
        var next = new double[nodeCount];

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            double totalChange = Sweep(weights, outWeights, scores, next);

            (scores, next) = (next, scores);

            if (totalChange < _tolerance)
                break;
        }

        return scores;
    }

    private static double[] ComputeOutDegrees(double[][] weights, int nodeCount)
    {
        var outWeights = new double[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            for (int j = 0; j < nodeCount; j++)
                outWeights[i] += weights[i][j];
        }

        return outWeights;
    }

    private double Sweep(double[][] weights, double[] outWeights, double[] scores, double[] next)
    {
        int count = scores.Length;
        double baseScore = (1 - _damping) / count;

        // Accumulated by source node, so the inner loop walks one contiguous row of the adjacency
        // matrix. The destination-outer form read a *column* of a jagged matrix once per node —
        // n rows touched at one offset each, which on a graph of a few hundred terms is a strided
        // walk over megabytes and misses on most of them. The sums are unchanged: for a given
        // destination the contributions still arrive in ascending source order, and the text still
        // multiplies the same quotient by the same score, so the result is the same double.
        Array.Clear(next);

        for (int source = 0; source < count; source++)
        {
            double outWeight = outWeights[source];

            if (outWeight <= 0)
                continue;

            double score = scores[source];
            double[] row = weights[source];

            for (int destination = 0; destination < count; destination++)
            {
                if (destination == source)
                    continue;

                double weight = row[destination];

                if (weight > 0)
                    next[destination] += score * (weight / outWeight);
            }
        }

        double totalChange = 0;

        for (int i = 0; i < count; i++)
        {
            next[i] = baseScore + (_damping * next[i]);
            totalChange += Math.Abs(next[i] - scores[i]);
        }

        return totalChange;
    }
}
