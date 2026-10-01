using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// The regression net: replays every pinned configuration and reports which ones drifted.
/// </summary>
/// <remarks>
/// Each pin is re-measured from scratch — its own index, its own analyzer, its own BM25 parameters —
/// so a change anywhere between the tokenizer and the metric surfaces as a number that no longer
/// matches. The check is a tolerance band rather than an exact value because a nDCG@10 averaged over
/// 300 to 1406 queries is a double whose last digits are not a contract; the tolerance is the part
/// that is asserted, and it is tight enough to catch a real change.
/// <para>
/// Index fingerprints are checked first and independently, because they cost no retrieval and say
/// something a score cannot: whether this run and the reference indexed the same thing. A drift
/// there invalidates every parity comparison at once, so it is worth catching even when the
/// effectiveness pins happen to hold.
/// </para>
/// </remarks>
internal static class ReferenceVerifier
{
    public static async Task<int> RunAsync(
        PinnedReference pinned, string dataBaseDir, bool write, CancellationToken cancellationToken)
    {
        var regressions = new List<(bool Ok, string Line)>();
        var fingerprints = new List<(bool Ok, string Line)>();

        foreach (var pin in pinned.RegressionPins)
        {
            var dataset = BeirDataset.Resolve(pin.Corpus);
            var corpus = await BeirLoader.LoadOrDownloadAsync(dataBaseDir, dataset);
            var index = new InMemoryTextIndex(BuildTokenizer(pin.Analyzer));
            index.Index(Evaluation.BuildDocuments(corpus));

            var measured = Measure(corpus, index, pin, cancellationToken);
            bool ok = pin.Accepts(measured);
            double delta = measured - pin.ExpectedNdcg;

            string line =
                $"{(ok ? "ok  " : "DRIFT")}  {pin.Config,-40} nDCG@10 {measured.ToString("0.0000", CultureInfo.InvariantCulture)}"
                + $"  expected {pin.ExpectedNdcg.ToString("0.0000", CultureInfo.InvariantCulture)}"
                + $"  delta {delta:+0.0000;-0.0000;+0.0000}"
                + $"  tolerance ±{pin.Tolerance.ToString("0.000", CultureInfo.InvariantCulture)}";

            if (!ok)
            {
                line += Environment.NewLine + "      reproduce: " + pin.CommandLine(dataBaseDir);
                line += Environment.NewLine
                    + "      --write does not re-record: it says so and changes nothing. Read what moved"
                    + " and why, then edit expectedNdcg in the pinned file as a reviewed change -"
                    + " the same reasoning as `lexisharp baseline` in the CLI.";
            }

            regressions.Add((ok, line));
        }

        foreach (var pin in pinned.IndexFingerprints)
        {
            var dataset = BeirDataset.Resolve(pin.Corpus);
            var corpus = await BeirLoader.LoadOrDownloadAsync(dataBaseDir, dataset);
            var index = new InMemoryTextIndex(BuildTokenizer(pin.Analyzer));
            index.Index(Evaluation.BuildDocuments(corpus));

            var measured = IndexFingerprint.Of(index);
            double difference = Math.Abs(measured.TotalTerms - pin.ReferenceTotalTerms) / (double)pin.ReferenceTotalTerms;
            bool ok = difference <= pin.Tolerance
                      && measured.Documents == pin.Documents
                      && measured.NonEmptyDocuments == pin.NonEmptyDocuments;

            fingerprints.Add((ok,
                $"{(ok ? "ok  " : "DRIFT")}  {pin.Corpus + "/" + pin.Analyzer,-40}"
                + $" terms {measured.TotalTerms} vs reference {pin.ReferenceTotalTerms}"
                + $"  difference {difference:P1}  tolerance {pin.Tolerance:P0}"
                + (ok ? string.Empty : "  — the indexes are not the same, so parity below is not a comparison")));
        }

        Console.WriteLine($"Index fingerprints ({pinned.Metric} parity depends on these)");
        foreach (var (_, line) in fingerprints)
            Console.WriteLine("  " + line);

        Console.WriteLine();
        Console.WriteLine("Regression pins (values this repository must keep producing)");
        foreach (var (_, line) in regressions)
            Console.WriteLine("  " + line);

        Console.WriteLine();
        Console.WriteLine("Parity against the published BM25 baselines (evidence, not assertions)");
        foreach (var row in pinned.Parity)
        {
            // A row whose recorded figure the harness no longer produces prints the measured one and
            // says so. Printing the difference against a figure that cannot be reproduced would read as
            // a parity result, and it is not one.
            string ours = (row.NotReproduced ? row.LexisharpNdcgMeasured!.Value : row.LexisharpNdcg)
                .ToString("0.0000", CultureInfo.InvariantCulture);
            double difference = (row.NotReproduced ? row.LexisharpNdcgMeasured!.Value : row.LexisharpNdcg)
                - row.ReferenceNdcg;

            Console.WriteLine(
                $"  {row.Corpus,-40} LexiSharp {ours}"
                + (row.NotReproduced ? " (measured today, not the recorded figure)" : string.Empty)
                + $"  reference {row.ReferenceNdcg.ToString("0.0000", CultureInfo.InvariantCulture)}"
                + $"  difference {difference:+0.0000;-0.0000;+0.0000}"
                + (row.AlignedParameters
                    ? "  (same BM25 parameters)"
                    : $"  (reference at k1={row.ReferenceK1.ToString("0.##", CultureInfo.InvariantCulture)},"
                      + $" b={row.ReferenceB.ToString("0.##", CultureInfo.InvariantCulture)} — not the parameters measured here)"));

            if (row.NotReproduced && !string.IsNullOrWhiteSpace(row.Variants))
                Console.WriteLine("      measured across the reachable configurations: " + row.Variants);

            if (!string.IsNullOrWhiteSpace(row.Note))
                Console.WriteLine("      " + row.Note);
        }

        bool clean = regressions.All(entry => entry.Ok) && fingerprints.All(entry => entry.Ok);

        if (write)
        {
            Console.WriteLine();
            Console.WriteLine("--write was passed: the file was not modified. Re-recording is a separate, " +
                              "deliberate act — run it only after reading what moved and why.");
        }

        Console.WriteLine();
        Console.WriteLine(clean
            ? "OK: every pinned configuration reproduced, and every index matches its reference."
            : "FAILED: a pinned configuration drifted. Do not re-record to make this pass; find out what moved first.");

        return clean ? 0 : 1;
    }

    /// <summary>
    /// Measures one pinned configuration. Built here rather than reused from the table so a pin
    /// measures exactly what it names and nothing the table happens to add.
    /// </summary>
    private static double Measure(
        BeirCorpus corpus, ITextIndex index, PinnedReference.RegressionPin pin, CancellationToken cancellationToken)
    {
        var engine = new RankedTextSearchEngine(
            index,
            new Bm25Scorer(
                pin.Bm25.K1, pin.Bm25.B,
                pin.QueryTermFrequency ? QueryTermWeighting.QueryFrequency : QueryTermWeighting.Distinct),
            BuildTokenizer(pin.Analyzer));

        var gain = pin.NdcgGain == "linear" ? NdcgGain.Linear : NdcgGain.Exponential;
        var documentIds = corpus.Documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);

        var queries = corpus.Queries
            .Where(query => corpus.TestRelevance.ContainsKey(query.Id))
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .Take(pin.Queries == 0 ? int.MaxValue : pin.Queries)
            .ToList();

        double total = 0;

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlySet<string>? excluded = pin.ExcludeQueryDocument && documentIds.Contains(query.Id)
                ? new HashSet<string>(StringComparer.Ordinal) { query.Id }
                : null;

            // The query syntax is a named setting of the pin, not something inherited. A pin that
            // left it unnamed would replay the library default (ParseQuerySyntax = true, a double
            // quote is a phrase delimiter) against a table that runs literal-text queries, because
            // on BEIR queries a straight quote is ordinary prose and a query language collapses it
            // (146 of ArguAna's 1,406 test queries carry one). The field exists so the pin says
            // which of the two it was recorded under.
            var retrieved = engine
                .Search(query.Text, new SearchOptions(
                    10,
                    ExcludedDocumentIds: excluded,
                    ParseQuerySyntax: pin.QuerySyntax))
                .Select(result => result.DocumentId)
                .ToArray();

            total += RetrievalMetrics.NdcgAtK(retrieved, corpus.TestRelevance[query.Id], 10, gain);
        }

        return queries.Count == 0 ? 0 : total / queries.Count;
    }

    private static ITokenizer BuildTokenizer(string analyzer) => analyzer switch
    {
        "default" => Tokenizer.Default,
        "porter" => new Tokenizer(new TokenizerOptions
        {
            Stemmer = new PorterStemmer(),
            RemoveStopWords = true,
        }),
        "english" => new Tokenizer(new TokenizerOptions
        {
            Stemmer = new PorterStemmer(),
            RemoveStopWords = true,
            StopWords = StopWords.EnglishFunction,
            KeepSingleCharTerms = true,
        }),
        _ => throw new ArgumentException($"Unknown analyzer '{analyzer}' in the pinned reference file."),
    };
}
