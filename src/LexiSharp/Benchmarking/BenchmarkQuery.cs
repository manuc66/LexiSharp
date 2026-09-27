namespace LexiSharp.Benchmarking;

/// <summary>
/// One labeled query of a benchmark run: a raw query together with the relevance judgments a
/// good ranking should reproduce.
/// </summary>
/// <remarks>
/// <para>
/// Relevance is stored once, as a document id to gain map, and the binary view every other metric
/// needs is <em>derived</em> from it. A benchmark that keeps two views of the same judgments can
/// disagree with itself — and a record's <c>with</c> expression bypasses a constructor's
/// validation, so validating the pair is not enough to keep them in step. Deriving instead makes
/// the inconsistency unrepresentable.
/// </para>
/// <para>
/// A binary query is simply a graded one whose gains are all <c>1</c>. The two agree because
/// <c>2^1 - 1 = 1</c>, so the graded formula degenerates to the binary one and no previously
/// reported binary nDCG moves.
/// </para>
/// </remarks>
public sealed record BenchmarkQuery
{
    /// <summary>A binary query: an id, a text, and the ids of the relevant documents.</summary>
    /// <param name="id">Stable identifier, used to join the query with its qrels.</param>
    /// <param name="text">The raw query as a user would type it.</param>
    /// <param name="relevantDocumentIds">Ids of the relevant documents; may be empty.</param>
    public BenchmarkQuery(string id, string text, IReadOnlyCollection<string> relevantDocumentIds)
        : this(id, text, ToUnitGains(relevantDocumentIds))
    {
    }

    /// <summary>A query whose judgments carry relevance levels (NFCorpus ships three, for instance).</summary>
    /// <param name="id">Stable identifier, used to join the query with its qrels.</param>
    /// <param name="text">The raw query as a user would type it.</param>
    /// <param name="gradedRelevance">Document id to non-negative relevance gain; may be empty.</param>
    public BenchmarkQuery(string id, string text, IReadOnlyDictionary<string, double> gradedRelevance)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(gradedRelevance);

        foreach ((string documentId, double gain) in gradedRelevance)
        {
            ArgumentException.ThrowIfNullOrEmpty(documentId);

            // A gain that is not a number, or that cannot be a relevance level, would make the
            // metric meaningless (or throw deep inside RetrievalMetrics, far from the cause).
            if (double.IsNaN(gain) || double.IsInfinity(gain) || gain < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gradedRelevance),
                    gain,
                    $"Relevance gain for document '{documentId}' must be non-negative and finite.");
            }
        }

        Id = id;
        Text = text;
        GradedRelevance = gradedRelevance;

        // Derived once, not per access: the benchmark loop reads the binary view five times per
        // query, and Keys is an IEnumerable, not a collection.
        RelevantDocumentIds = gradedRelevance.Keys.ToArray();
    }

    /// <summary>Stable identifier, used to join the query with its qrels.</summary>
    public string Id { get; }

    /// <summary>The raw query as a user would type it.</summary>
    public string Text { get; }

    /// <summary>
    /// Document id to non-negative relevance gain — the single source of truth for this query's
    /// judgments. nDCG uses it with exponential gains; recall, MAP, MRR, precision and F1 use
    /// <see cref="RelevantDocumentIds"/>, which is derived from it.
    /// </summary>
    public IReadOnlyDictionary<string, double> GradedRelevance { get; }

    /// <summary>
    /// The binary projection of the judgments: every judged document is relevant, whatever its
    /// level. Queries with none are loaded but excluded from the metric averages, because there is
    /// nothing to score them against.
    /// </summary>
    public IReadOnlyCollection<string> RelevantDocumentIds { get; }

    /// <summary>
    /// True when at least one judgment carries a level other than <c>1</c>. Purely informational:
    /// it tells a report whether its nDCG is graded or binary, since the two are not comparable.
    /// </summary>
    // Exact, and deliberately so: this is a classification of the judgments as supplied, not a
    // comparison of two computed quantities. The gains come from a qrels file and are whole numbers
    // in practice, so 1.0 is exactly representable and the "binary or graded" question has an exact
    // answer. An epsilon here would make the answer depend on an arbitrary tolerance -- a gain of
    // 1.0000001 read from a file would be reported as binary, and the report would then call two
    // graded nDCG figures comparable when they are not. See BenchmarkQueryResult.TiesWithNeighbour
    // for the case where tolerance really is the wrong answer.
    public bool IsGraded => GradedRelevance.Values.Any(gain => gain != 1.0); // NOSONAR:S1244

    private static IReadOnlyDictionary<string, double> ToUnitGains(IReadOnlyCollection<string> relevantDocumentIds)
    {
        ArgumentNullException.ThrowIfNull(relevantDocumentIds);

        var gains = new Dictionary<string, double>(relevantDocumentIds.Count, StringComparer.Ordinal);

        foreach (string documentId in relevantDocumentIds)
        {
            ArgumentException.ThrowIfNullOrEmpty(documentId);

            // A duplicate would otherwise be silently collapsed, changing the judged count.
            if (!gains.TryAdd(documentId, 1.0))
            {
                throw new ArgumentException(
                    $"Document '{documentId}' is judged more than once; a query's judgments must not repeat.",
                    nameof(relevantDocumentIds));
            }
        }

        return gains;
    }
}
