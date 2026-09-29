using System.Globalization;
using System.Text;

namespace LexiSharp.Benchmarking;

/// <summary>What a comparison against a golden master found for one query of one configuration.</summary>
internal enum GoldenVerdict
{
    /// <summary>Identical ranking and identical metrics.</summary>
    Match = 0,

    /// <summary>
    /// Same set of documents with the same scores, different order — two documents that tie. The
    /// ranking breaks ties on corpus order, so this is not a behaviour change and a report that
    /// flagged it as one would teach people to ignore the report.
    /// </summary>
    TieReordered = 1,

    /// <summary>Different membership, different order, or different metrics: a real change.</summary>
    Changed = 2,
}

/// <summary>One query's verdict in a golden-master comparison.</summary>
/// <param name="Configuration">The configuration the query was evaluated under.</param>
/// <param name="QueryId">The query's id.</param>
/// <param name="Verdict">What the comparison found.</param>
/// <param name="Expected">The recorded ranking, or an empty list when the query was not recorded.</param>
/// <param name="Actual">The ranking produced by this run, or an empty list.</param>
/// <param name="Detail">A human-readable explanation, present for anything other than a match.</param>
internal sealed record GoldenQueryVerdict(
    string Configuration,
    string QueryId,
    GoldenVerdict Verdict,
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Actual,
    string? Detail);

/// <summary>The outcome of checking a run against a golden master.</summary>
/// <param name="Matches">Queries whose ranking and metrics are unchanged.</param>
/// <param name="TieReorders">Queries whose documents merely swapped places among equal scores.</param>
/// <param name="Changes">Queries that genuinely differ.</param>
/// <param name="Missing">Queries present in the run but absent from the baseline.</param>
/// <param name="Extra">Queries present in the baseline but absent from the run.</param>
internal sealed record GoldenComparison(
    IReadOnlyList<GoldenQueryVerdict> Matches,
    IReadOnlyList<GoldenQueryVerdict> TieReorders,
    IReadOnlyList<GoldenQueryVerdict> Changes,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra)
{
    /// <summary>True when nothing changed and nothing is out of sync.</summary>
    public bool IsClean => Changes.Count == 0 && Missing.Count == 0 && Extra.Count == 0;

    /// <summary>Everything worth printing: real changes first, then desync, then ties.</summary>
    public IEnumerable<GoldenQueryVerdict> Report =>
        Changes.Concat(Missing.Select(id => new GoldenQueryVerdict("?", id, GoldenVerdict.Changed, [], [], "in the run, absent from the baseline")))
            .Concat(Extra.Select(id => new GoldenQueryVerdict("?", id, GoldenVerdict.Changed, [], [], "in the baseline, absent from the run")))
            .Concat(TieReorders);
}

/// <summary>
/// A committed record of what a set of configurations returns for a corpus and a query set: the
/// per-query ranking and the per-query metrics, in a format meant to be read in a diff.
/// </summary>
/// <remarks>
/// <para>
/// What is stored is deliberately small. A vector of raw scores for every query is unreadable, and
/// an unreadable baseline is one nobody reviews — which is the same as no baseline, except it looks
/// like evidence. One line per query, ids in rank order, metrics to four decimals.
/// </para>
/// <para>
/// Metrics are stored because they are the part a change moves silently: a re-ordering among tied
/// documents leaves every rank intact and still shifts nDCG.
/// </para>
/// <para>
/// <b>Internal, not public API.</b> A golden master is this repository's own regression net: it
/// is written by <c>lexisharp baseline</c>, read by <c>lexisharp verify</c>, and consumed by
/// nothing else — not by the evaluation harness, not by a library consumer, and not by the
/// documentation. It was public because the types are declared in <c>src/</c>, which is not by
/// itself a reason: the reader who installs the package was given a golden-master surface along
/// with the retrieval one, and a second way to compare rankings that the first way already covers.
/// The golden master is the <c>LexiSharp.Cli</c> command's business, so it stays
/// <see langword="internal"/> and the two assemblies that legitimately drive it — the CLI and the
/// test suite — are named as friends.
/// </para>
/// </remarks>
internal sealed record GoldenBaseline
{
    /// <summary>Format version, so a future layout change is a loud error rather than a silent skip.</summary>
    public const int CurrentVersion = 1;

    private const int MetricDecimals = 4;

    /// <summary>One query of one configuration, as recorded.</summary>
    /// <param name="Configuration">The configuration the query was evaluated under.</param>
    /// <param name="QueryId">The query's id.</param>
    /// <param name="DocumentIds">The returned document ids, in rank order.</param>
    /// <param name="Metrics">That query's own metrics.</param>
    internal sealed record Entry(
        string Configuration,
        string QueryId,
        IReadOnlyList<string> DocumentIds,
        BenchmarkMetrics Metrics);

    /// <summary>Retrieval depth the baseline was recorded at.</summary>
    public int TopK { get; init; }

    /// <summary>How many documents the corpus had when the baseline was recorded.</summary>
    public int CorpusDocuments { get; init; }

    /// <summary>The recorded runs, keyed by configuration then query id.</summary>
    public IReadOnlyList<Entry> Entries { get; init; } = [];

    /// <summary>
    /// Checks a run against this baseline. Every query of every configuration in the run is
    /// classified; anything the baseline does not know about is reported as a desync rather than
    /// passed, because a baseline that silently ignores new queries reports a false clean.
    /// </summary>
    /// <param name="results">The runs to check.</param>
    public GoldenComparison Compare(IReadOnlyList<BenchmarkConfigResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var expected = new Dictionary<(string, string), Entry>();

        foreach (var entry in Entries)
            expected[(entry.Configuration, entry.QueryId)] = entry;

        var matches = new List<GoldenQueryVerdict>();
        var ties = new List<GoldenQueryVerdict>();
        var changes = new List<GoldenQueryVerdict>();
        var missing = new List<string>();
        var extra = new List<string>(expected.Keys.Select(key => $"{key.Item1} / {key.Item2}"));
        var seen = new HashSet<(string, string)>();

        foreach (var result in results)
        {
            foreach (var query in result.PerQuery)
            {
                var key = (result.Name, query.QueryId);

                if (!seen.Add(key))
                    continue;

                extra.Remove($"{key.Item1} / {key.Item2}");

                if (!expected.TryGetValue(key, out var recorded))
                {
                    missing.Add($"{key.Item1} / {key.Item2}");
                    continue;
                }

                (GoldenVerdict verdict, string? detail) = Classify(recorded, query);

                var entry = new GoldenQueryVerdict(
                    key.Item1, key.Item2, verdict, recorded.DocumentIds, query.RetrievedIds, detail);

                switch (verdict)
                {
                    case GoldenVerdict.Match:
                        matches.Add(entry);
                        break;
                    case GoldenVerdict.TieReordered:
                        ties.Add(entry);
                        break;
                    default:
                        changes.Add(entry);
                        break;
                }
            }
        }

        return new GoldenComparison(matches, ties, changes, missing, extra);
    }

    private static (GoldenVerdict, string?) Classify(Entry recorded, BenchmarkQueryResult actual)
    {
        // Metrics first: they are what a change moves most quietly.
        if (Math.Abs(recorded.Metrics.NdcgAtK - actual.Metrics.NdcgAtK) > 5e-5
            || Math.Abs(recorded.Metrics.MapAtK - actual.Metrics.MapAtK) > 5e-5
            || Math.Abs(recorded.Metrics.MrrAtK - actual.Metrics.MrrAtK) > 5e-5
            || Math.Abs(recorded.Metrics.RecallAtK - actual.Metrics.RecallAtK) > 5e-5
            || Math.Abs(recorded.Metrics.PrecisionAtK - actual.Metrics.PrecisionAtK) > 5e-5
            || Math.Abs(recorded.Metrics.F1AtK - actual.Metrics.F1AtK) > 5e-5)
        {
            return (GoldenVerdict.Changed,
                $"metrics moved (nDCG {recorded.Metrics.NdcgAtK:0.####} -> {actual.Metrics.NdcgAtK:0.####})");
        }

        if (recorded.DocumentIds.Count != actual.RetrievedIds.Count)
        {
            return (GoldenVerdict.Changed,
                $"page size {recorded.DocumentIds.Count} -> {actual.RetrievedIds.Count}");
        }

        bool sameOrder = true;
        bool sameSet = true;

        for (int i = 0; i < recorded.DocumentIds.Count; i++)
        {
            if (!string.Equals(recorded.DocumentIds[i], actual.RetrievedIds[i], StringComparison.Ordinal))
                sameOrder = false;

            if (!actual.RetrievedIds.Contains(recorded.DocumentIds[i], StringComparer.Ordinal))
                sameSet = false;
        }

        if (sameOrder)
            return (GoldenVerdict.Match, null);

        if (!sameSet)
        {
            var added = actual.RetrievedIds.Where(id => !recorded.DocumentIds.Contains(id, StringComparer.Ordinal)).ToList();
            var dropped = recorded.DocumentIds.Where(id => !actual.RetrievedIds.Contains(id, StringComparer.Ordinal)).ToList();

            return (GoldenVerdict.Changed,
                $"page membership changed (+{string.Join(", ", added)} -{string.Join(", ", dropped)})");
        }

        // Same documents, same metrics, different order. Acceptable only when every document that
        // moved was tied with a neighbour: a tie is decided by corpus order, so its relative
        // position carries no information. Anything else is a real re-ordering, and calling it a tie
        // would hide it.
        for (int i = 0; i < recorded.DocumentIds.Count; i++)
        {
            if (string.Equals(recorded.DocumentIds[i], actual.RetrievedIds[i], StringComparison.Ordinal))
                continue;

            if (!actual.TiesWithNeighbour(i))
            {
                return (GoldenVerdict.Changed,
                    $"re-ordered {recorded.DocumentIds[i]} and {actual.RetrievedIds[i]} without a tie");
            }
        }

        return (GoldenVerdict.TieReordered, "documents swapped among equal scores");
    }

    /// <summary>Renders the baseline in the on-disk format.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();

        builder.Append("# LexiSharp golden master - do not edit by hand.\n");
        builder.Append("# Regenerate with: lexisharp baseline <corpus> --queries <file> --qrels <file> --out <this file>\n");
        builder.Append(CultureInfo.InvariantCulture, $"# version: {CurrentVersion}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# topK: {TopK}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# corpusDocuments: {CorpusDocuments}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# entries: {Entries.Count}\n");

        string? configuration = null;

        foreach (var entry in Entries)
        {
            if (!string.Equals(configuration, entry.Configuration, StringComparison.Ordinal))
            {
                configuration = entry.Configuration;
                builder.Append('\n');
                builder.Append(CultureInfo.InvariantCulture, $"[{configuration}]\n");
            }

            builder.Append(CultureInfo.InvariantCulture, $"{entry.QueryId} | {string.Join(" ", entry.DocumentIds)} | ");
            builder.Append(CultureInfo.InvariantCulture,
                $"ndcg {entry.Metrics.NdcgAtK:0.0000} map {entry.Metrics.MapAtK:0.0000} mrr {entry.Metrics.MrrAtK:0.0000} ");
            builder.Append(CultureInfo.InvariantCulture,
                $"r {entry.Metrics.RecallAtK:0.0000} p {entry.Metrics.PrecisionAtK:0.0000} f1 {entry.Metrics.F1AtK:0.0000}\n");
        }

        return builder.ToString();
    }

    /// <summary>Parses the on-disk format.</summary>
    /// <param name="text">The file contents.</param>
    /// <param name="path">Used only in error messages.</param>
    public static GoldenBaseline Parse(string text, string path = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);

        int version = 0;
        int topK = 0;
        int documents = 0;
        string? configuration = null;
        var entries = new List<Entry>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.Length == 0)
                continue;

            if (line.StartsWith('#'))
            {
                // Headers are "# name: value"; the two free-form lines (what this is, how to
                // regenerate it) carry no colon and are skipped.
                string body = line.TrimStart('#').Trim();
                int separator = body.IndexOf(':');

                if (separator <= 0)
                    continue;

                string name = body[..separator].Trim();
                string value = body[(separator + 1)..].Trim();

                if (name.Equals("version", StringComparison.Ordinal) && int.TryParse(value, out version))
                    continue;

                if (name.Equals("topK", StringComparison.Ordinal) && int.TryParse(value, out topK))
                    continue;

                if (name.Equals("corpusDocuments", StringComparison.Ordinal) && int.TryParse(value, out documents))
                    continue;

                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                configuration = line[1..^1].Trim();
                continue;
            }

            if (configuration is null)
                throw new FormatException($"{path}: a query line appears before any [configuration] header.");

            string[] columns = line.Split('|');

            if (columns.Length != 3)
                throw new FormatException($"{path}: expected 'queryId | docIds | metrics' but got: {line}");

            string queryId = columns[0].Trim();
            string documentIds = columns[1].Trim();
            var metrics = ParseMetrics(path, columns[2], line);

            entries.Add(new Entry(
                configuration,
                queryId,
                documentIds.Length == 0 ? [] : documentIds.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                metrics));
        }

        if (version != CurrentVersion)
        {
            throw new FormatException(
                $"{path}: baseline format version {version}, expected {CurrentVersion}. Regenerate it.");
        }

        if (topK <= 0)
            throw new FormatException($"{path}: missing or invalid '# topK:' header.");

        return new GoldenBaseline { TopK = topK, CorpusDocuments = documents, Entries = entries };
    }

    private static BenchmarkMetrics ParseMetrics(string path, string column, string line)
    {
        // The writer emits "name value" pairs, two tokens each, in a fixed order.
        string[] tokens = column.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length % 2 != 0)
            throw new FormatException($"{path}: metrics must be 'name value' pairs, got: {line}");

        var values = new Dictionary<string, double>(StringComparer.Ordinal);

        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (!double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormatException($"{path}: '{tokens[i]} {tokens[i + 1]}' is not a metric pair in: {line}");

            values[tokens[i]] = value;
        }

        foreach (string required in new[] { "ndcg", "map", "mrr", "r", "p", "f1" })
        {
            if (!values.ContainsKey(required))
                throw new FormatException($"{path}: missing metric '{required}' in: {line}");
        }

        return new BenchmarkMetrics(
            values["ndcg"], values["map"], values["mrr"], values["r"], values["p"], values["f1"]);
    }
}
