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

/// <summary>One stage a document passed through, as recorded by <c>SearchTrace</c>.</summary>
public sealed record TraceStageDto(
    string Stage,
    string DocumentId,
    double Before,
    double After,
    string? Detail);

/// <summary>
/// How much the demo's lanes agree about a document, from
/// <c>LexiSharp.Core.RetrievalAgreementAnalyzer</c>. Null for lanes that have a single source and
/// therefore nothing to agree or disagree about.
/// </summary>
public sealed record AgreementDto(
    string Agreement,
    IReadOnlyDictionary<string, double> Strengths,
    IReadOnlyList<string> StrongSources,
    IReadOnlyList<string> AbsentSources);

/// <summary>
/// A unified explanation payload. <see cref="Terms"/> carries the per-term arithmetic when the
/// lane's scorer can explain itself, <see cref="Sources"/> the per-source scores of a federated
/// ranking, and <see cref="Stages"/> the full stage-by-stage chain — which is the only part
/// populated for lanes that are neither (a pure rerank, for instance).
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
    IReadOnlyDictionary<string, double> Parameters,
    IReadOnlyList<TraceStageDto> Stages,
    AgreementDto? Agreement = null);
