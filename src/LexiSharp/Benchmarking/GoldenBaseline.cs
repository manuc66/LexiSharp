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
    public const int CurrentVersion = 2;

    private const int MetricDecimals = 4;

    /// <summary>
    /// Renders a score as its exact bit pattern, so a recorded value and a re-computed one are
    /// compared as the doubles they are rather than as rounded text.
    /// </summary>
    /// <remarks>
    /// Round-trip decimal ("R") would also be exact. Hexadecimal is used because it stays exact
    /// through <em>editing</em>: a reviewer who reformats a value, or a tool that normalises a line
    /// ending, cannot silently shorten a mantissa. It also shows a NaN or an infinity as such
    /// instead of as a word.
    /// </remarks>
    public static string FormatScore(double score) =>
        BitConverter.DoubleToUInt64Bits(score).ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>
    /// Folds a query's exact score bits into one 16-digit token, for the on-disk format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scores are stored folded rather than written out because the file exists to be read in a
    /// diff, and this repository has already established what an unreadable baseline is worth: the
    /// reference corpus is 42 documents whose ids run to 28 characters, so writing every score out
    /// took the longest line from 267 to 539 characters. A reviewer does not read those, and a
    /// baseline nobody reads catches nothing while looking like evidence. One token per line keeps
    /// the diff the same shape it was.
    /// </para>
    /// <para>
    /// The pairs are sorted by document id before folding, which is what keeps a tie from being
    /// reported as a score change: two documents with equal scores that swap places produce the same
    /// set of pairs, and folding them in rank order would give a different token and turn a
    /// legitimate re-ordering into a failure. A document whose score really moved still changes the
    /// set, and is still caught.
    /// </para>
    /// <para>
    /// FNV-1a, over the id and the eight raw bytes of each score, so the token depends on the bits
    /// and not on a rendering of them. It can in principle collide — 2^-64 for an unrelated pair set
    /// — which is the honest cost of a 16-character token: a difference could be missed, and it
    /// would be missed silently. That is far below the rate at which this repository would notice a
    /// broken checksum, and far below the rate of the effect being looked for, which is systematic
    /// rather than random. The alternative, no folding, loses 132 reviews a year instead.
    /// </para>
    /// </remarks>
    public static string FoldScores(IReadOnlyList<string> documentIds, IReadOnlyList<double>? scores)
    {
        if (scores is null || scores.Count != documentIds.Count)
            return "-";

        var pairs = new (string Id, ulong Bits)[documentIds.Count];

        for (int i = 0; i < pairs.Length; i++)
            pairs[i] = (documentIds[i], BitConverter.DoubleToUInt64Bits(scores[i]));

        Array.Sort(pairs, static (left, right) => string.CompareOrdinal(left.Id, right.Id));

        const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;
        ulong hash = Offset;

        foreach ((string id, ulong bits) in pairs)
        {
            foreach (char character in id)
            {
                hash ^= character;
                hash *= Prime;
            }

            for (int octet = 0; octet < 8; octet++)
            {
                hash ^= (byte)(bits >> (octet * 8));
                hash *= Prime;
            }
        }

        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>Renders a run's scores for a failure message, where length costs nothing.</summary>
    public static string DescribeScores(IReadOnlyList<string> documentIds, IReadOnlyList<double>? scores) =>
        scores is null || scores.Count != documentIds.Count
            ? "unknown"
            : string.Join(" ", documentIds.Select((id, i) => $"{id}={FormatScore(scores[i])}"));

    /// <summary>One query of one configuration, as recorded.</summary>
    /// <param name="Configuration">The configuration the query was evaluated under.</param>
    /// <param name="QueryId">The query's id.</param>
    /// <param name="DocumentIds">The returned document ids, in rank order.</param>
    /// <param name="Metrics">That query's own metrics.</param>
    /// <param name="ScoreFingerprint">
    /// A 16-digit fold of this query's exact score bits, as described on
    /// <see cref="FoldScores"/>. Null only for a baseline written before version 2.
    /// </param>
    internal sealed record Entry(
        string Configuration,
        string QueryId,
        IReadOnlyList<string> DocumentIds,
        BenchmarkMetrics Metrics,
        string? ScoreFingerprint = null);

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
        // The score bits are checked, bit for bit, because this is the only check in the repository
        // that can see a floating-point difference at all.
        //
        // Everything else here is rounded or tolerant: metrics to 5e-5, document ids compared as
        // text. A score that moved in its last bits - a reassociated sum, a fused multiply-add, a
        // vectorised reduction - would pass all of it, because the rounding is coarser than the
        // difference and the ranking is decided by gaps far larger than a mantissa bit. Nothing else
        // in the build would notice either: the unit suite pins contracts, and the pinned nDCG
        // figures have a tolerance of +/-0.002, roughly a hundred million times the width of the
        // thing being looked for.
        //
        // So the claim "the same corpus gives the same scores" is one this repository was making
        // without ever checking. Recording the bits makes it checked, and it is checkable across
        // machines: the baseline is committed, so a re-run on another CPU, another SDK or another
        // operating system is a comparison of one machine's doubles against another's, rather than a
        // comparison of rounded text. A difference here means the summation order is not fixed by
        // the data, which is the one thing the sorted posting lists are there to guarantee.
        //
        // It is computed first but *reported* last, which is the whole subtlety. A run whose ranking
        // moved has a more specific and more useful diagnosis than "the bits differ", and reporting
        // the fingerprint would throw it away. The fingerprint earns its place exactly where nothing
        // else speaks: the ranking, the membership and the metrics all identical while the doubles
        // underneath them are not.
        string? scoresMoved = ScoreFingerprintMismatch(recorded, actual);

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
            return scoresMoved is null ? (GoldenVerdict.Match, null) : (GoldenVerdict.Changed, scoresMoved);

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

        // A tie is a score coincidence, so a run that re-ordered tied documents and also moved some
        // score has two facts, and the one that is not a coincidence is the one worth reading. The
        // fingerprint is insensitive to the permutation itself, so it fires only when something else
        // moved too.
        if (scoresMoved is not null)
            return (GoldenVerdict.Changed, scoresMoved);

        return (GoldenVerdict.TieReordered, "documents swapped among equal scores");
    }

    /// <summary>
    /// Describes how this run's score bits differ from the recorded ones, or null when they are
    /// identical or the baseline predates the column.
    /// </summary>
    private static string? ScoreFingerprintMismatch(Entry recorded, BenchmarkQueryResult actual)
    {
        if (recorded.ScoreFingerprint is null || actual.RetrievedScores is null)
            return null;

        // The run's own ids, not the recorded ones. Folding pairs a score with a document, and the
        // run's scores are positionally aligned with the run's ranking: pairing them with the
        // recorded ids would attribute each score to the wrong document whenever the two orders
        // differ, which is exactly the case where the pairing matters. Mutation testing found this
        // by leaving it wrong and watching the re-ordering test pass anyway.
        string actualFingerprint = FoldScores(actual.RetrievedIds, actual.RetrievedScores);

        return string.Equals(recorded.ScoreFingerprint, actualFingerprint, StringComparison.Ordinal)
            ? null
            : $"scores moved, and nothing else did (fingerprint {recorded.ScoreFingerprint} -> {actualFingerprint}; " +
              $"this run: {DescribeScores(actual.RetrievedIds, actual.RetrievedScores)})";
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
                $"r {entry.Metrics.RecallAtK:0.0000} p {entry.Metrics.PrecisionAtK:0.0000} f1 {entry.Metrics.F1AtK:0.0000}");

            if (entry.ScoreFingerprint is not null)
                builder.Append(CultureInfo.InvariantCulture, $" | scores {entry.ScoreFingerprint}");

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses the folded-score column.
    /// </summary>
    /// <remarks>
    /// Only the shape is checked, not the value: the token is a fold, so there is nothing in it to
    /// verify beyond being sixteen hexadecimal digits, or the "-" that a run with no scores records.
    /// </remarks>
    private static string ParseFingerprint(string path, string column, string line)
    {
        string value = column.Trim();

        if (value == "-")
            return "-";

        if (value.Length != 16 || !ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"{path}: '{value}' is not 16 hexadecimal digits in: {line}");

        return value;
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

            if (columns.Length is not (3 or 4))
                throw new FormatException($"{path}: expected 'queryId | docIds | metrics [| id=score ...]' but got: {line}");

            string queryId = columns[0].Trim();
            string documentIds = columns[1].Trim();
            var metrics = ParseMetrics(path, columns[2], line);
            var ids = documentIds.Length == 0
                ? Array.Empty<string>()
                : documentIds.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            string? fingerprint = columns.Length == 4 && columns[3].Trim().StartsWith("scores ", StringComparison.Ordinal)
                ? ParseFingerprint(path, columns[3]["scores ".Length..], line)
                : null;

            entries.Add(new Entry(configuration, queryId, ids, metrics, fingerprint));
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
