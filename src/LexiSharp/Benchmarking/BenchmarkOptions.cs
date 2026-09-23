using LexiSharp.Linguistics;

namespace LexiSharp.Benchmarking;

/// <summary>
/// Tuning knobs of a <see cref="CorpusBenchmark.Run"/>.
/// </summary>
public sealed record BenchmarkOptions
{
    /// <summary>Retrieval depth used for every metric; must be positive. Default: <c>10</c>.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>
    /// Tokenizer used to build the shared index (and therefore every engine). Defaults to
    /// <see cref="Tokenizer.Default"/> when null.
    /// </summary>
    public ITokenizer? Tokenizer { get; init; }
}