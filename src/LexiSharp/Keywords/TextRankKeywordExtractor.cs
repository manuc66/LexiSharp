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

        var (nodes, rows) = BuildGraph(tokens);

        if (nodes.Count == 1)
            return new[] { new Keyword(nodes[0], 1.0) };

        var scores = PageRank(rows, nodes.Count);

        // Node order = first appearance order, so ranking is deterministic: first appearance wins ties.
        return Enumerable.Range(0, nodes.Count)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i)
            .Take(topN)
            .Select(i => new Keyword(nodes[i], scores[i]))
            .ToList();
    }

    private (List<string> Nodes, Row[] Rows) BuildGraph(IReadOnlyList<string> tokens)
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

        // Symmetric weighted adjacency from the sliding co-occurrence window, aggregated per pair.
        // A text's graph is sparse — a term co-occurs with its window neighbours, not with the
        // vocabulary — so a dense n × n matrix makes the sweep walk zeroes: 8 MB of them at a
        // thousand terms, visited once per node per iteration.
        var weights = new Dictionary<long, double>();

        for (int i = 0; i < tokens.Count; i++)
        {
            int a = nodeIndex[tokens[i]];
            int max = Math.Min(i + _windowSize, tokens.Count);

            for (int j = i + 1; j < max; j++)
            {
                int b = nodeIndex[tokens[j]];

                if (a != b)
                {
                    AddEdge(weights, a, b);
                    AddEdge(weights, b, a);
                }
            }
        }

        var rows = new List<(int Target, double Weight)>[nodes.Count];

        for (int i = 0; i < rows.Length; i++)
            rows[i] = [];

        foreach (var (pair, weight) in weights)
            rows[(int)(pair >> 32)].Add(((int)(pair & 0xFFFFFFFF), weight));

        var built = new Row[rows.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            // Ascending destination, which is the order the dense form walked and the order the
            // out-degree is summed in — so the two forms produce the same doubles.
            rows[i].Sort(static (left, right) => left.Target.CompareTo(right.Target));

            var targets = new int[rows[i].Count];
            var values = new double[rows[i].Count];

            for (int e = 0; e < rows[i].Count; e++)
            {
                targets[e] = rows[i][e].Target;
                values[e] = rows[i][e].Weight;
            }

            built[i] = new Row(targets, values);
        }

        return (nodes, built);
    }

    /// <summary>Adds one occurrence to the edge <paramref name="source"/> → <paramref name="target"/>.</summary>
    private static void AddEdge(Dictionary<long, double> weights, int source, int target)
    {
        long pair = ((long)source << 32) | (uint)target;
        weights[pair] = weights.GetValueOrDefault(pair) + 1;
    }

    private double[] PageRank(Row[] rows, int nodeCount)
    {
        var outWeights = ComputeOutDegrees(rows);

        // PageRank from a uniform distribution.
        var scores = new double[nodeCount];
        Array.Fill(scores, 1.0 / nodeCount);
        var next = new double[nodeCount];

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            double totalChange = Sweep(rows, outWeights, scores, next);

            (scores, next) = (next, scores);

            if (totalChange < _tolerance)
                break;
        }

        return scores;
    }

    private static double[] ComputeOutDegrees(Row[] rows)
    {
        var outWeights = new double[rows.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            double total = 0;

            foreach (double weight in rows[i].Weights)
                total += weight;

            outWeights[i] = total;
        }

        return outWeights;
    }

    private double Sweep(Row[] rows, double[] outWeights, double[] scores, double[] next)
    {
        int count = scores.Length;
        double baseScore = (1 - _damping) / count;

        // Accumulated by source, over the edges each node actually has. For a given destination the
        // contributions still arrive in ascending source order, and each still multiplies the same
        // quotient by the same score, so the sums are the same doubles the dense walk produced.
        Array.Clear(next);

        for (int source = 0; source < count; source++)
        {
            double outWeight = outWeights[source];

            if (outWeight <= 0)
                continue;

            double score = scores[source];
            int[] targets = rows[source].Targets;
            double[] weights = rows[source].Weights;

            for (int e = 0; e < targets.Length; e++)
                next[targets[e]] += score * (weights[e] / outWeight);
        }

        double totalChange = 0;

        for (int i = 0; i < count; i++)
        {
            next[i] = baseScore + (_damping * next[i]);
            totalChange += Math.Abs(next[i] - scores[i]);
        }

        return totalChange;
    }

    /// <summary>One node's outgoing edges, ascending by destination.</summary>
    private readonly struct Row(int[] targets, double[] weights)
    {
        public int[] Targets => targets;

        public double[] Weights => weights;
    }
}
