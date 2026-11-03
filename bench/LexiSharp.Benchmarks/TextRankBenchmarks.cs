using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using LexiSharp.Keywords;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Keyword extraction, whose cost is the PageRank sweep over the term graph.
/// </summary>
/// <remarks>
/// The graph has one node per distinct term in the text, so a long document with a large vocabulary
/// is a graph of hundreds of nodes; the sweep visits every edge once per iteration, up to a hundred
/// iterations. The representation decides what that costs: a dense <c>nodes²</c> matrix is 720 KB at
/// 300 terms and 8 MB at 1 000, and the sweep walks its zeroes, while an adjacency list holds only
/// the co-occurrences a text actually produces. Both the time and the allocation of an extraction
/// therefore track the graph's shape rather than the node count alone, which is what the three rows
/// below are for.
/// <para>
/// The text is fixed-seed and drawn uniformly from a fixed vocabulary, so each row's graph has the
/// size its label says. Uniform terms are denser than natural language — every pair in the window
/// is a real edge — which makes the rows an upper bound on the edges and a fair measure of the walk.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class TextRankBenchmarks
{
    /// <summary>Distinct terms in the text, and therefore nodes in the graph.</summary>
    [Params(300, 600, 1000)]
    public int Vocabulary { get; set; }

    /// <summary>Tokens in the text: ten per distinct term, so every node is reached repeatedly.</summary>
    private const int TokensPerTerm = 10;

    private TextRankKeywordExtractor _extractor = null!;
    private string _text = null!;

    [GlobalSetup]
    public void Setup()
    {
        _text = BuildText(Vocabulary, Vocabulary * TokensPerTerm);
        _extractor = new TextRankKeywordExtractor();

        // Warm the embedding-free path once so the measured row is the steady state.
        _extractor.Extract(_text, Vocabulary);
    }

    /// <summary>The whole extraction: tokenize, build the co-occurrence graph, then iterate it.</summary>
    [Benchmark]
    public int Extract() => _extractor.Extract(_text, Vocabulary).Count;

    private static string BuildText(int vocabulary, int tokens)
    {
        var terms = new string[vocabulary];

        for (int i = 0; i < vocabulary; i++)
            terms[i] = "term" + i.ToString(CultureInfo.InvariantCulture);

        var random = new Random(23); // NOSONAR:S2245 (fixed-seed synthetic corpus for reproducible benchmarks)
        var builder = new StringBuilder(tokens * 9);

        for (int i = 0; i < tokens; i++)
            builder.Append(terms[random.Next(vocabulary)]).Append(' ');

        return builder.ToString();
    }
}
