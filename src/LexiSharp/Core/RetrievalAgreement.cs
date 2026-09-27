namespace LexiSharp.Core;

/// <summary>
/// How much the sources of a federated search agree about one document.
/// </summary>
/// <remarks>
/// The categories are structural rather than named, because source labels are the caller's choice:
/// a setup that calls them <c>"lexical"</c> and <c>"dense"</c> and one that calls them
/// <c>"bm25"</c> and <c>"cosine"</c> describe the same shape, and a hard-coded "LexicalOnly" would
/// be wrong for half of them. Read <see cref="RetrievalAgreementReport.StrongSources"/> to get the
/// names back.
/// </remarks>
public enum RetrievalAgreement
{
    /// <summary>No source returned the document. Should not occur on a result page.</summary>
    None = 0,

    /// <summary>
    /// Exactly one source returned it. This is the case worth surfacing: the document is on the
    /// page on the word of one signal, and a reader looking only at the final score cannot tell.
    /// </summary>
    SingleSource = 1,

    /// <summary>
    /// Several sources returned it and none of them is enthusiastic. Not the same as
    /// <see cref="Disputed"/>: here the sources do not disagree with each other, they are all
    /// lukewarm, which usually means the document is on the page through a merger rather than
    /// through anyone's conviction.
    /// </summary>
    Lukewarm = 2,

    /// <summary>
    /// Several sources returned it and they do not agree: some found it convincing, the rest did
    /// not. The document is in the page, but the reason it is there is contested.
    /// </summary>
    Disputed = 3,

    /// <summary>
    /// Several sources returned it and every one of them found it convincing.
    /// </summary>
    Unanimous = 4,
}

/// <summary>
/// One document's agreement across the sources that produced the page, with the per-source
/// strengths it was derived from.
/// </summary>
/// <param name="DocumentId">The document this is about.</param>
/// <param name="Agreement">The structural verdict.</param>
/// <param name="Sources">Every source that returned the document.</param>
/// <param name="StrongSources">The subset scoring at or above the strength threshold.</param>
/// <param name="AbsentSources">The sources that did not return it at all — outside their own depth.</param>
/// <param name="Strengths">
/// Per-source strength, each normalized to the best score that source gave on this page. Raw
/// contributions are <b>not</b> comparable across sources — a BM25 of 4.8 and a cosine of 0.48
/// mean nothing side by side — so the ratio to that source's own maximum is what makes them
/// comparable. A source absent from the map was not scored here.
/// </param>
public sealed record RetrievalAgreementReport(
    string DocumentId,
    RetrievalAgreement Agreement,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> StrongSources,
    IReadOnlyList<string> AbsentSources,
    IReadOnlyDictionary<string, double> Strengths);

/// <summary>
/// Classifies each document of a federated page by how much its sources agree, from the
/// per-source scores in <see cref="DetailedSearchResult.Contributions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why normalization is required.</b> Sources rank on their own scales. LexiSharp's own dense
/// lane returns a cosine in [0, 1] while its BM25 lane returns values around 1 to 10, so "is this
/// source's signal strong" cannot be read off the raw number. Dividing by the best score that
/// source gave on the same page makes the values scale-free without assuming anything about the
/// scoring function — the top document of each source is 1.0 by construction.
/// </para>
/// <para>
/// <b>The threshold is a heuristic, not a calibrated value.</b> A document is "strong" for a
/// source when it scores at least <c>strongThreshold</c> of that source's best. The default is a
/// round number chosen for readability, not a threshold that has been validated against relevance
/// data; treat it as a knob and expect to tune it against your own corpus. Nothing here has been
/// measured against a qrels file.
/// </para>
/// <para>
/// A source that returned no document at all is reported as <em>absent</em> rather than weak,
/// because being outside a source's depth is a different fact from being ranked low by it.
/// </para>
/// </remarks>
public static class RetrievalAgreementAnalyzer
{
    /// <summary>Default share of a source's best score at which a document counts as strongly supported.</summary>
    public const double DefaultStrongThreshold = 0.5;

    /// <summary>Classifies every document of the page.</summary>
    /// <param name="page">The federated page, as returned by <see cref="IDetailedSearchEngine"/>.</param>
    /// <param name="strongThreshold">
    /// Share of a source's best score at or above which a document is strongly supported by it.
    /// Must be in (0, 1]. Default <see cref="DefaultStrongThreshold"/>.
    /// </param>
    /// <param name="sourceNames">
    /// Every source the engine federates, including any that returned nothing on this page. When
    /// <c>null</c>, only the sources seen in the page are considered — which is the right default
    /// for a single engine, and the wrong one when a source silently contributed nothing.
    /// </param>
    public static IReadOnlyList<RetrievalAgreementReport> Analyze(
        IReadOnlyList<DetailedSearchResult> page,
        double strongThreshold = DefaultStrongThreshold,
        IReadOnlyList<string>? sourceNames = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (strongThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strongThreshold), strongThreshold, "The strong threshold must be in (0, 1].");
        }

        if (page.Count == 0)
            return [];

        // Every source the engine federates: from the page if not told otherwise, plus any the
        // caller names, so a source that returned nothing still shows up as absent rather than
        // being silently forgotten.
        var sources = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var result in page)
        {
            foreach (string source in result.Contributions.Keys)
                sources.Add(source);
        }

        if (sourceNames is not null)
        {
            foreach (string source in sourceNames)
                sources.Add(source);
        }

        // Best score each source gave anywhere on the page: the denominator that makes the
        // strengths comparable without knowing the source's scoring scale.
        var best = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (string source in sources)
            best[source] = double.NegativeInfinity;

        foreach (var result in page)
        {
            foreach ((string source, double score) in result.Contributions)
            {
                if (score > best[source])
                    best[source] = score;
            }
        }

        var reports = new List<RetrievalAgreementReport>(page.Count);

        foreach (var result in page)
        {
            var strengths = new Dictionary<string, double>(StringComparer.Ordinal);
            var strong = new List<string>();
            var present = new List<string>();

            foreach (string source in sources)
            {
                if (!result.Contributions.TryGetValue(source, out double score))
                    continue;

                present.Add(source);

                double ceiling = best[source];

                // A source whose best on this page is not above zero cannot be normalized; every
                // document it returned is treated as weakly supported rather than as a strong 1.0.
                double strength = ceiling > 0 ? Math.Clamp(score / ceiling, 0, 1) : 0;

                strengths[source] = strength;

                if (strength >= strongThreshold)
                    strong.Add(source);
            }

            var absent = sources.Where(source => !present.Contains(source, StringComparer.Ordinal)).ToArray();

            var agreement = present.Count switch
            {
                0 => RetrievalAgreement.None,
                1 => RetrievalAgreement.SingleSource,
                _ when strong.Count == present.Count => RetrievalAgreement.Unanimous,
                _ when strong.Count == 0 => RetrievalAgreement.Lukewarm,
                _ => RetrievalAgreement.Disputed,
            };

            reports.Add(new RetrievalAgreementReport(
                result.DocumentId,
                agreement,
                present,
                strong,
                absent,
                strengths));
        }

        return reports;
    }

    /// <summary>
    /// The share of the page each agreement category accounts for, keyed by category. Useful as a
    /// one-line summary of whether a strategy is mostly finding what every retriever agrees on.
    /// </summary>
    /// <param name="reports">The analyzed page.</param>
    public static IReadOnlyDictionary<RetrievalAgreement, int> Summarize(
        IReadOnlyList<RetrievalAgreementReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var counts = new Dictionary<RetrievalAgreement, int>();

        foreach (var report in reports)
        {
            counts.TryGetValue(report.Agreement, out int count);
            counts[report.Agreement] = count + 1;
        }

        return counts;
    }
}
