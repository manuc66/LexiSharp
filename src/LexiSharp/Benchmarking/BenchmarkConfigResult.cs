namespace LexiSharp.Benchmarking;

/// <summary>
/// The outcome of benchmarking one <see cref="BenchmarkConfig"/> against a corpus and a query
/// set: the retrieval metrics averaged over the judged queries, plus the latency observed while
/// running them (the engine is warm and the search costs dominate).
/// </summary>
/// <param name="Name">The configuration name, as printed in CLI tables and JSON reports.</param>
/// <param name="Metrics">Mean metrics over the <paramref name="JudgedQueries"/> queries.</param>
/// <param name="TotalMilliseconds">Wall-clock time spent searching all judged queries.</param>
/// <param name="MillisecondsPerQuery">Mean latency per judged query.</param>
/// <param name="JudgedQueries">Number of queries with a non-empty relevant set that were averaged.</param>
public sealed record BenchmarkConfigResult(
    string Name,
    BenchmarkMetrics Metrics,
    double TotalMilliseconds,
    double MillisecondsPerQuery,
    int JudgedQueries);