namespace LexiSharp.Demo;

/// <summary>Everything one search returns: the query and one entry per compared strategy.</summary>
public sealed record CompareResponse(string Query, IReadOnlyList<LaneResult> Lanes);

/// <summary>One retrieval strategy, its timing, and its ranked page.</summary>
public sealed record LaneResult(
    string Key,
    string Label,
    string Description,
    double ElapsedMs,
    IReadOnlyList<HitResult> Hits);

/// <summary>One ranked document inside a lane.</summary>
public sealed record HitResult(
    string Id,
    string Title,
    string Category,
    double Score,
    string Snippet,
    string Highlighted,
    IReadOnlyDictionary<string, double>? Sources);

/// <summary>A single query term's contribution to a document's score (term-mode explanation).</summary>
public sealed record TermContributionDto(
    string Term,
    double TermFrequency,
    double DocumentFrequency,
    double Idf,
    double Score);

/// <summary>
/// A unified explanation payload: either per-term contributions (BM25/TF-IDF style) or the
/// per-source scores that fed a federated ranking.
/// </summary>
public sealed record ExplanationDto(
    string DocumentId,
    string Mode,
    string? Algorithm,
    double Total,
    int? DocumentLength,
    double? AverageDocumentLength,
    IReadOnlyList<TermContributionDto> Terms,
    IReadOnlyDictionary<string, double> Sources,
    IReadOnlyDictionary<string, double> Parameters);
