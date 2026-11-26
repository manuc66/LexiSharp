using System.Globalization;

using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// Measures the ceiling a query router could reach on one corpus, before any router exists.
/// </summary>
/// <remarks>
/// <para>
/// The question this answers is not "does routing work" but "is there anything to route". For
/// every query it scores a handful of scoring pipelines over one shared index — same documents,
/// same analysis, different scorer — and compares the best fixed pipeline with a per-query oracle
/// that may pick a different pipeline for each query. The difference is the gap a router would
/// have to learn; if it is small, no gate can pay for itself and the experiment stops here.
/// </para>
/// <para>
/// This is deliberately a probe and not a library feature: it adds no public surface, runs from
/// the evaluation harness, and reuses <see cref="Evaluation.RunEngine"/> so its per-query numbers
/// are the harness's own. What it adds is the shape of the distribution — which pipeline wins on
/// which query, how often the best fixed pipeline is already optimal, and how correlated the
/// pipelines' errors are, which is the quantity that decides whether an ensemble can help at all.
/// </para>
/// <para>
/// The length-bucket table is <b>in-sample</b> and therefore optimistic: it shows whether a
/// one-feature rule <i>could</i> beat the best fixed pipeline, not whether one does. A learned gate
/// has to be cross-validated before any of it is believed.
/// </para>
/// </remarks>
internal static class RoutingProbeCommand
{
    private const double Epsilon = 1e-9;

    public static async Task<int> RunAsync(string[] args)
    {
        string dataBaseDir = Path.Combine(AppContext.BaseDirectory, "../../../data");
        string datasetName = "nfcorpus";
        int? limit = null;
        int topK = 10;
        int jobs = Math.Max(1, Environment.ProcessorCount - 1);
        bool gate = true;
        bool directional = true;
        bool guarded = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    dataBaseDir = args[++i];
                    break;
                case "--dataset" when i + 1 < args.Length:
                    datasetName = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--top" when i + 1 < args.Length:
                    topK = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--jobs" when i + 1 < args.Length:
                    jobs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--no-gate":
                    gate = false;
                    break;
                case "--no-directional":
                    directional = false;
                    break;
                case "--no-guarded":
                    guarded = false;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "usage: routing-probe [--dataset nfcorpus|scifact|arguana|fiqa] [--data DIR]\n"
                        + "                      [--limit N] [--top K] [--jobs N]");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                    return 2;
            }
        }

        dataBaseDir = Path.GetFullPath(dataBaseDir);

        BeirCorpus corpus;
        try
        {
            corpus = await BeirLoader.LoadOrDownloadAsync(dataBaseDir, BeirDataset.Resolve(datasetName));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not load '{datasetName}': {exception.Message}");
            return 1;
        }

        (ITokenizer tokenizer, string analysis, _) = Program.BuildAnalysis("default", stem: false);
        IReadOnlyList<SearchDocument> documents = Evaluation.BuildDocuments(corpus);
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(documents);

        List<Route> routes = BuildRoutes(index, tokenizer, documents);

        Console.WriteLine();
        int testQueries = corpus.Queries.Count(query => corpus.TestRelevance.ContainsKey(query.Id));

        Console.WriteLine(
            $"== Routing probe — {corpus.Name} ({documents.Count:N0} documents, "
            + $"{testQueries:N0} test queries, analysis {analysis}, top {topK}) ==");
        Console.WriteLine();

        var names = new List<string>(routes.Count);
        var metrics = new List<double[]>(routes.Count);
        var rankings = new List<string[][]>(routes.Count);
        List<string> queryIds = [];

        foreach (Route route in routes)
        {
            ConfigResult result = Evaluation.RunEngine(
                corpus,
                route.Engine,
                route.Name,
                topK,
                limit,
                NdcgGain.Exponential,
                jobs: jobs,
                captureOutcomes: true);

            IReadOnlyList<QueryOutcome>? outcomes = result.Outcomes
                ?? throw new InvalidOperationException("The probe asked for outcomes and got none.");

            if (queryIds.Count == 0)
                queryIds = outcomes.Select(outcome => outcome.QueryId).ToList();
            else if (!queryIds.SequenceEqual(outcomes.Select(outcome => outcome.QueryId)))
                throw new InvalidOperationException("Routes scored different query sets.");

            names.Add(route.Name);
            metrics.Add(outcomes.Select(outcome => outcome.NdcgAt10).ToArray());
            rankings.Add(outcomes.Select(outcome => outcome.Retrieved).ToArray());

            Console.WriteLine(
                $"  {route.Name,-30} nDCG@{topK} = {result.NdcgAt10.ToString("0.0000", CultureInfo.InvariantCulture)}"
                + $"  ({result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)");
        }

        Console.WriteLine();
        PrintCeiling(names, metrics, queryIds.Count, topK);
        PrintRegret(names, metrics);
        PrintComplementarity(names, metrics, rankings);
        PrintLengthBuckets(names, metrics, queryIds, tokenizer, corpus);

        if (gate && names.Count > 1)
            PrintGate(names, metrics, queryIds, tokenizer, corpus, index, routes, directional, guarded, topK);

        PrintVerdict(names, metrics);

        return 0;
    }

    private static List<Route> BuildRoutes(
        ITextIndex index,
        ITokenizer tokenizer,
        IReadOnlyList<SearchDocument> documents)
    {
        var routes = new List<Route>
        {
            new("BM25 (1.5, 0.75)", new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer)),
            new("BM25 (0.9, 0.4)", new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4), tokenizer)),
            new("BM25+ (delta=1.0)", new RankedTextSearchEngine(index, new Bm25PlusScorer(1.5, 0.75, 1.0), tokenizer)),
            new("BM25L (delta=0.5)", new RankedTextSearchEngine(index, new Bm25LScorer(1.5, 0.75, 0.5), tokenizer)),
            new("TF-IDF", new RankedTextSearchEngine(index, new TfIdfScorer(), tokenizer)),
            new("QueryLikelihood (lambda=0.2)", new RankedTextSearchEngine(index, new QueryLikelihoodScorer(0.2), tokenizer)),
        };

        // A field-weighted route needs documents that actually carry the field; a corpus of plain
        // text would only add a duplicate BM25 row to the ceiling.
        bool anyTitle = documents.Any(document =>
            document.TextFields is { Count: > 0 } fields && fields.ContainsKey("title"));

        if (anyTitle)
        {
            var weights = new Dictionary<string, double>(StringComparer.Ordinal) { ["title"] = 2.0 };
            routes.Add(new Route(
                "BM25F (title=2.0)",
                new RankedTextSearchEngine(index, new Bm25FScorer(1.2, 0.75, weights), tokenizer)));
        }

        return routes;
    }

    private static void PrintCeiling(
        IReadOnlyList<string> names,
        IReadOnlyList<double[]> metrics,
        int queries,
        int topK)
    {
        double[] means = metrics.Select(row => row.Average()).ToArray();
        int bestIndex = Array.IndexOf(means, means.Max());
        double[] oracle = Enumerable.Range(0, queries)
            .Select(i => metrics.Max(row => row[i]))
            .ToArray();

        Console.WriteLine($"== Ceiling ({queries} queries) ==");
        Console.WriteLine($"  best fixed pipeline      {names[bestIndex]}  {means[bestIndex]:0.0000}");
        Console.WriteLine($"  per-query oracle                            {oracle.Average():0.0000}");
        Console.WriteLine(
            $"  oracle gap                                  {oracle.Average() - means[bestIndex]:0.0000}"
            + $"  ({(oracle.Average() - means[bestIndex]) / Math.Max(means[bestIndex], Epsilon):P1} of the best fixed)");

        // A tie is not a win: on this corpus most pipelines return the same ranking for most
        // queries, and counting ties as wins makes every row look competitive. Strict wins are
        // queries where the pipeline alone is best; ties are queries where it shares the best score.
        var wins = new int[names.Count];
        var ties = new int[names.Count];

        for (int i = 0; i < queries; i++)
        {
            double best = metrics.Max(row => row[i]);
            int atBest = metrics.Count(row => best - row[i] <= Epsilon);

            for (int r = 0; r < names.Count; r++)
            {
                if (best - metrics[r][i] > Epsilon)
                    continue;

                if (atBest == 1)
                    wins[r]++;
                else
                    ties[r]++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {"pipeline",-30} {"nDCG",8} {"win",6} {"tie",6}  {"win share",9}");
        for (int r = 0; r < names.Count; r++)
        {
            Console.WriteLine(
                $"  {names[r],-30} {means[r],8:0.0000} {wins[r],6} {ties[r],6}  {(double)wins[r] / queries,9:P1}");
        }

        Console.WriteLine();
    }

    private static void PrintRegret(IReadOnlyList<string> names, IReadOnlyList<double[]> metrics)
    {
        int queries = metrics[0].Length;
        double[] means = metrics.Select(row => row.Average()).ToArray();
        int bestIndex = Array.IndexOf(means, means.Max());

        double[] regret = Enumerable.Range(0, queries)
            .Select(i => metrics.Max(row => row[i]) - metrics[bestIndex][i])
            .OrderBy(value => value)
            .ToArray();

        Console.WriteLine("== Regret of the best fixed pipeline ==");
        Console.WriteLine(
            $"  mean {regret.Average():0.0000} · p50 {Quantile(regret, 0.50):0.0000}"
            + $" · p90 {Quantile(regret, 0.90):0.0000} · max {regret[^1]:0.0000}");
        Console.WriteLine(
            $"  queries with regret > 0.05: {regret.Count(value => value > 0.05) / (double)queries:P0}"
            + $" · > 0.10: {regret.Count(value => value > 0.10) / (double)queries:P0}"
            + $" · > 0.25: {regret.Count(value => value > 0.25) / (double)queries:P0}");
        Console.WriteLine();
    }

    private static void PrintComplementarity(
        IReadOnlyList<string> names,
        IReadOnlyList<double[]> metrics,
        IReadOnlyList<string[][]> rankings)
    {
        var correlations = new List<double>();
        var overlaps = new List<double>();

        for (int a = 0; a < names.Count; a++)
        {
            for (int b = a + 1; b < names.Count; b++)
            {
                correlations.Add(Pearson(metrics[a], metrics[b]));

                double shared = 0;
                for (int i = 0; i < metrics[a].Length; i++)
                    shared += Jaccard(rankings[a][i], rankings[b][i]);

                overlaps.Add(shared / metrics[a].Length);
            }
        }

        Console.WriteLine("== Complementarity (the resource an ensemble spends) ==");
        Console.WriteLine($"  mean pairwise Pearson of per-query nDCG : {correlations.Average():0.00}");
        Console.WriteLine($"  mean pairwise top-10 Jaccard distance  : {1 - overlaps.Average():0.00}");
        Console.WriteLine();
    }

    private static void PrintLengthBuckets(
        IReadOnlyList<string> names,
        IReadOnlyList<double[]> metrics,
        IReadOnlyList<string> queryIds,
        ITokenizer tokenizer,
        BeirCorpus corpus)
    {
        var byId = corpus.Queries.ToDictionary(query => query.Id, query => query.Text, StringComparer.Ordinal);
        int[] lengths = queryIds.Select(id => tokenizer.Tokenize(byId[id]).Count).ToArray();

        (string Label, Func<int, bool> Match)[] buckets =
        [
            ("1-2 tokens", length => length <= 2),
            ("3-4 tokens", length => length is 3 or 4),
            ("5-8 tokens", length => length is >= 5 and <= 8),
            (">= 9 tokens", length => length >= 9),
        ];

        Console.WriteLine("== Best pipeline by query length (in-sample, optimistic) ==");

        foreach ((string label, Func<int, bool> match) in buckets)
        {
            int[] indices = Enumerable.Range(0, lengths.Length).Where(i => match(lengths[i])).ToArray();
            if (indices.Length == 0)
                continue;

            double[] means = names
                .Select((_, r) => indices.Average(i => metrics[r][i]))
                .ToArray();

            int best = Array.IndexOf(means, means.Max());
            double oracle = indices.Average(i => metrics.Max(row => row[i]));

            Console.WriteLine(
                $"  {label,-12} n = {indices.Length,4} · best {names[best],-24} {means[best]:0.0000}"
                + $" · oracle {oracle:0.0000} · gap {oracle - means[best]:0.0000}");
        }

        Console.WriteLine();
    }

    private static void PrintVerdict(IReadOnlyList<string> names, IReadOnlyList<double[]> metrics)
    {
        int queries = metrics[0].Length;
        double[] means = metrics.Select(row => row.Average()).ToArray();
        double best = means.Max();
        double oracle = Enumerable.Range(0, queries).Select(i => metrics.Max(row => row[i])).Average();
        double gap = oracle - best;

        Console.WriteLine("== Verdict ==");

        if (gap < 0.01)
        {
            Console.WriteLine(
                "  The oracle gap is below one nDCG point: no gate can pay for itself on this corpus.");
            Console.WriteLine("  Stop here, or change the pool of pipelines — not the router.");
        }
        else
        {
            Console.WriteLine(
                $"  The oracle gap is {gap:0.0000} nDCG. A gate is worth attempting only if it recovers"
                + " a measured share of it out of sample.");

            double meanCorrelation = MeanCorrelation(metrics);
            if (meanCorrelation > 0.9)
            {
                Console.WriteLine(
                    $"  But the pool is nearly redundant (mean per-query Pearson {meanCorrelation:0.00}):"
                    + " the gap lives in a tail of queries, so the gate must classify that tail, not"
                    + " average the pool.");
            }
            Console.WriteLine(
                "  Next step: cross-validate a <=5-feature gate (length, OOV rate, IDF entropy,"
                + " top-1/top-2 margin) against the best fixed pipeline, and report the worst decile.");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Trains and cross-validates a gate on the per-query outcomes, and compares it with the best
    /// fixed pipeline and with a one-feature rule.
    /// </summary>
    /// <remarks>
    /// Two baselines, because either one alone would flatter the gate. The best fixed pipeline is
    /// chosen on every query, so it is an in-sample winner and a hard bar; the length rule is
    /// cross-validated like the learned gate, so it is the honest "did a model add anything over a
    /// threshold" test. The gate itself is ridge regression per pipeline: it predicts each
    /// pipeline's nDCG from the query's features and runs the argmax, which handles ties without
    /// inventing a label the way a single-winner classifier does.
    /// </remarks>
    private static void PrintGate(
        IReadOnlyList<string> names,
        IReadOnlyList<double[]> metrics,
        IReadOnlyList<string> queryIds,
        ITokenizer tokenizer,
        BeirCorpus corpus,
        ITextIndex index,
        IReadOnlyList<Route> routes,
        bool directional,
        bool guarded,
        int topK)
    {
        const int Folds = 5;
        const double Ridge = 1.0;

        var byId = corpus.Queries.ToDictionary(query => query.Id, query => query.Text, StringComparer.Ordinal);
        int count = queryIds.Count;
        int routeCount = names.Count;
        ITextSearchEngine baseline = routes[0].Engine;

        // Fold assignment from the query id, not from its position: the query list is ordered by
        // id, and a modulo over that order would put neighbouring ids in the same fold.
        var folds = new int[count];
        for (int i = 0; i < count; i++)
            folds[i] = (int)(Fnv1a(queryIds[i]) % Folds);

        var features = new double[count][];
        var lengths = new int[count];

        for (int i = 0; i < count; i++)
        {
            string text = byId[queryIds[i]];
            IReadOnlyList<SearchResult> hits = baseline.Search(text, new SearchOptions(topK));

            double margin = hits.Count >= 2 && hits[0].Score > 0
                ? (hits[0].Score - hits[1].Score) / hits[0].Score
                : 1.0;

            int tokens = tokenizer.Tokenize(text).Count;
            lengths[i] = tokens;
            features[i] = Features(text, index, tokenizer, margin);
        }

        double[] learned = CrossValidate(
            folds, features, metrics, (train, test) =>
            {
                double[][] weights = Enumerable.Range(0, routeCount)
                    .Select(route => RidgeFit(train, features, metrics[route], Ridge))
                    .ToArray();

                var predicted = new double[test.Length];
                for (int q = 0; q < test.Length; q++)
                {
                    double bestScore = double.NegativeInfinity;
                    for (int route = 0; route < routeCount; route++)
                    {
                        double score = Dot(weights[route], features[test[q]]);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            predicted[q] = metrics[route][test[q]];
                        }
                    }
                }

                return predicted;
            });

        double[] rule = CrossValidate(
            folds, features, metrics, (train, test) =>
            {
                var bucketBest = new int[4];
                for (int bucket = 0; bucket < 4; bucket++)
                {
                    int[] inBucket = train.Where(i => Bucket(lengths[i]) == bucket).ToArray();
                    int[] pool = inBucket.Length > 0 ? inBucket : train.ToArray();
                    bucketBest[bucket] = Enumerable.Range(0, routeCount)
                        .OrderByDescending(route => pool.Average(i => metrics[route][i]))
                        .First();
                }

                return test.Select(i => metrics[bucketBest[Bucket(lengths[i])]][i]).ToArray();
            });

        double[] fixedBest = Enumerable.Range(0, count)
            .Select(i => metrics[Array.IndexOf(
                Enumerable.Range(0, routeCount).Select(r => metrics[r].Average()).ToArray(),
                Enumerable.Range(0, routeCount).Select(r => metrics[r].Average()).Max())][i])
            .ToArray();

        double[] oracle = Enumerable.Range(0, count).Select(i => metrics.Max(row => row[i])).ToArray();
        double bestMean = fixedBest.Average();
        double gap = oracle.Average() - bestMean;

        Console.WriteLine("== Gate (5-fold cross-validation by query) ==");
        Console.WriteLine($"  {"best fixed pipeline",-24} {bestMean:0.0000}");
        Console.WriteLine(
            $"  {"length rule (CV)",-24} {rule.Average():0.0000}"
            + $"  {Signed(rule.Average() - bestMean)}  ({Share(rule.Average() - bestMean, gap)} of the gap)"
            + $"  p={SignificanceTests.PairedBootstrap(rule, fixedBest):0.000}");
        Console.WriteLine(
            $"  {"ridge gate (CV)",-24} {learned.Average():0.0000}"
            + $"  {Signed(learned.Average() - bestMean)}  ({Share(learned.Average() - bestMean, gap)} of the gap)"
            + $"  p={SignificanceTests.PairedBootstrap(learned, fixedBest):0.000}");
        var captures = directional || guarded
            ? routes.Select(route => Capture(route.Engine, queryIds, byId, topK)).ToArray()
            : null;

        if (directional)
        {
            double[] directionalScores = CrossValidate(
                folds, features, metrics, (train, test) =>
                {
                    // One model per pipeline, trained to predict its *gain over the baseline*, not
                    // its raw nDCG. Route identity is then the intercept of its own model, and the
                    // decision is "switch only when the predicted gain is positive".
                    //
                    // This is the naive form on purpose: no minimum-gain guard, no shrinkage
                    // towards the route's average, no constraint tying the prediction to the
                    // route's measured quality. It answers "do route-output features carry a
                    // direction at all", not "what is the best possible directional gate".
                    var weights = new double[routeCount][];
                    for (int route = 0; route < routeCount; route++)
                    {
                        var target = new double[count];
                        foreach (int q in train)
                            target[q] = metrics[route][q] - metrics[0][q];

                        weights[route] = RidgeFit(
                            train,
                            Enumerable.Range(0, count)
                                .Select(q => RouteFeatures(q, route, features, captures!))
                                .ToArray(),
                            target,
                            Ridge);
                    }

                    var predicted = new double[test.Length];
                    for (int i = 0; i < test.Length; i++)
                    {
                        int q = test[i];
                        int chosen = 0;
                        double bestGain = 0;

                        for (int route = 1; route < routeCount; route++)
                        {
                            double gain = Dot(weights[route], RouteFeatures(q, route, features, captures!));
                            if (gain > bestGain)
                            {
                                bestGain = gain;
                                chosen = route;
                            }
                        }

                        predicted[i] = metrics[chosen][q];
                    }

                    return predicted;
                });

            Console.WriteLine(
                $"  {"directional (naive, CV)",-24} {directionalScores.Average():0.0000}"
                + $"  {Signed(directionalScores.Average() - bestMean)}  ({Share(directionalScores.Average() - bestMean, gap)} of the gap)"
                + $"  p={SignificanceTests.PairedBootstrap(directionalScores, fixedBest):0.000}");

            int directionalWins = 0;
            int directionalLosses = 0;
            for (int i = 0; i < count; i++)
            {
                if (directionalScores[i] > fixedBest[i] + Epsilon)
                    directionalWins++;
                else if (directionalScores[i] < fixedBest[i] - Epsilon)
                    directionalLosses++;
            }

            Console.WriteLine(
                $"  {"",-24}  switches +{directionalWins} / -{directionalLosses}"
                + $" / ={count - directionalWins - directionalLosses}");
        }

        if (guarded)
        {
            (double[] scores, List<(double Kappa, double Tau)> choices) = GuardedGate(
                folds, queryIds, features, captures!, metrics);

            Console.WriteLine(
                $"  {"guarded gate (CV)",-24} {scores.Average():0.0000}"
                + $"  {Signed(scores.Average() - bestMean)}  ({Share(scores.Average() - bestMean, gap)} of the gap)"
                + $"  p={SignificanceTests.PairedBootstrap(scores, fixedBest):0.000}");

            int guardedWins = 0;
            int guardedLosses = 0;
            for (int i = 0; i < count; i++)
            {
                if (scores[i] > fixedBest[i] + Epsilon)
                    guardedWins++;
                else if (scores[i] < fixedBest[i] - Epsilon)
                    guardedLosses++;
            }

            string kappas = string.Join(
                ", ",
                choices.Select(choice => choice.Kappa.ToString("0.##", CultureInfo.InvariantCulture)));
            string taus = string.Join(
                ", ",
                choices.Select(choice => choice.Tau.ToString("0.##", CultureInfo.InvariantCulture)));

            Console.WriteLine($"  {"",-24}  κ per fold: {kappas} · τ per fold: {taus}");
            Console.WriteLine(
                $"  {"",-24}  switches +{guardedWins} / -{guardedLosses}"
                + $" / ={count - guardedWins - guardedLosses}");
        }

        Console.WriteLine($"  {"oracle",-24} {oracle.Average():0.0000}");

        int wins = 0;
        int losses = 0;
        for (int i = 0; i < count; i++)
        {
            if (learned[i] > fixedBest[i] + Epsilon)
                wins++;
            else if (learned[i] < fixedBest[i] - Epsilon)
                losses++;
        }

        double[] regret = Enumerable.Range(0, count).Select(i => oracle[i] - learned[i]).OrderBy(v => v).ToArray();
        int worstDecileStart = (int)(count * 0.9);

        Console.WriteLine(
            $"  gate vs best fixed: +{wins} / -{losses} / ={count - wins - losses}"
            + $" · regret p90 {Quantile(regret, 0.90):0.0000}"
            + $" · worst-decile mean {regret.Skip(worstDecileStart).Average():0.0000}");

        double recovery = gap <= Epsilon ? 0 : (learned.Average() - bestMean) / gap;
        Console.WriteLine(
            recovery < 0.30
                ? "  Verdict: the gate recovers less than 30 % of the gap — a negative result, and a publishable one."
                : recovery > 0.50
                    ? "  Verdict: the gate recovers more than 50 % of the gap — a router is worth implementing behind IQueryRouter."
                    : "  Verdict: the gate recovers 30–50 % of the gap — promising but not decisive; inspect the tail before committing.");
        Console.WriteLine();
    }

    private static double[] CrossValidate(
        int[] folds,
        double[][] features,
        IReadOnlyList<double[]> metrics,
        Func<int[], int[], double[]> fold)
    {
        var output = new double[folds.Length];

        for (int f = 0; f < folds.Max() + 1; f++)
        {
            int[] train = Enumerable.Range(0, folds.Length).Where(i => folds[i] != f).ToArray();
            int[] test = Enumerable.Range(0, folds.Length).Where(i => folds[i] == f).ToArray();

            if (test.Length == 0)
                continue;

            double[] predicted = fold(train, test);
            for (int q = 0; q < test.Length; q++)
                output[test[q]] = predicted[q];
        }

        return output;
    }

    /// <summary>Ridge regression with an intercept, fitted on standardized features.</summary>
    private static double[] RidgeFit(int[] rows, double[][] features, double[] target, double ridge)
    {
        int dimensions = features[0].Length;
        var mean = new double[dimensions];
        var deviation = new double[dimensions];

        foreach (int row in rows)
            for (int d = 0; d < dimensions; d++)
                mean[d] += features[row][d];

        for (int d = 0; d < dimensions; d++)
            mean[d] /= rows.Length;

        foreach (int row in rows)
            for (int d = 0; d < dimensions; d++)
            {
                double delta = features[row][d] - mean[d];
                deviation[d] += delta * delta;
            }

        for (int d = 0; d < dimensions; d++)
            deviation[d] = Math.Sqrt(deviation[d] / rows.Length) is var sd && sd > Epsilon ? sd : 1.0;

        int size = dimensions + 1;
        var xtx = new double[size, size];
        var xty = new double[size];

        foreach (int row in rows)
        {
            var x = new double[size];
            x[0] = 1;

            for (int d = 0; d < dimensions; d++)
                x[d + 1] = (features[row][d] - mean[d]) / deviation[d];

            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                    xtx[a, b] += x[a] * x[b];

                xty[a] += x[a] * target[row];
            }
        }

        // The intercept is not penalized: shrinking it would move every prediction towards zero,
        // which is not a prior anyone holds about nDCG.
        for (int d = 1; d < size; d++)
            xtx[d, d] += ridge;

        return Solve(xtx, xty);
    }

    private static double Dot(double[] weights, double[] features)
    {
        double score = weights[0];
        for (int d = 0; d < features.Length; d++)
            score += weights[d + 1] * features[d];

        return score;
    }

    private static double[] Features(string text, ITextIndex index, ITokenizer tokenizer, double margin)
    {
        IReadOnlyList<string> terms = tokenizer.Tokenize(text);
        int n = index.StatisticDocumentCount;

        double oov = 0;
        var idf = new double[terms.Count];
        double idfSum = 0;

        for (int t = 0; t < terms.Count; t++)
        {
            int df = index.DocumentFrequency(terms[t]);
            if (df == 0)
                oov++;

            idf[t] = Math.Log(1.0 + n / Math.Max(1, df));
            idfSum += idf[t];
        }

        double entropy = 0;
        if (terms.Count > 1 && idfSum > 0)
        {
            foreach (double value in idf)
            {
                double p = value / idfSum;
                if (p > 0)
                    entropy -= p * Math.Log(p);
            }

            entropy /= Math.Log(terms.Count);
        }

        return
        [
            Math.Log(1.0 + terms.Count),
            terms.Count == 0 ? 0 : oov / terms.Count,
            entropy,
            terms.Count == 0 ? 0 : idfSum / terms.Count / Math.Log(1.0 + n),
            margin,
        ];
    }

    private static int Bucket(int tokens) => tokens switch
    {
        <= 2 => 0,
        3 or 4 => 1,
        <= 8 => 2,
        _ => 3,
    };

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();

        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
                if (Math.Abs(m[row, column]) > Math.Abs(m[pivot, column]))
                    pivot = row;

            if (Math.Abs(m[pivot, column]) <= Epsilon)
                continue;

            if (pivot != column)
            {
                for (int k = 0; k < n; k++)
                    (m[column, k], m[pivot, k]) = (m[pivot, k], m[column, k]);

                (x[column], x[pivot]) = (x[pivot], x[column]);
            }

            for (int row = column + 1; row < n; row++)
            {
                double factor = m[row, column] / m[column, column];
                if (factor == 0)
                    continue;

                for (int k = column; k < n; k++)
                    m[row, k] -= factor * m[column, k];

                x[row] -= factor * x[column];
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = x[row];
            for (int k = row + 1; k < n; k++)
                sum -= m[row, k] * x[k];

            x[row] = Math.Abs(m[row, row]) <= Epsilon ? 0 : sum / m[row, row];
        }

        return x;
    }

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash;
    }

    private static string Signed(double value) => value >= 0 ? $"+{value:0.0000}" : $"{value:0.0000}";

    private static string Share(double value, double gap) =>
        gap <= Epsilon ? "n/a" : $"{value / gap:P0}";

    /// <summary>
    /// The guarded gate: a directional model whose switch decision is learned under a grid of
    /// safety parameters, all selected by an inner cross-validation.
    /// </summary>
    /// <remarks>
    /// The naive directional gate failed for a specific, fixable reason: a per-route regression on
    /// raw features will happily predict a large gain for a pipeline that is worse on average, and
    /// one corpus (ArguAna) collapsed to TF-IDF. The guard is threefold: the prediction is shrunk
    /// towards the pipeline's measured average gain (κ), a switch must clear a minimum predicted
    /// gain (τ), and the ridge penalty is raised so the slope is a hint rather than a model. κ and τ
    /// are chosen on inner folds only — the outer test fold never sees them — which is what makes
    /// the result an out-of-sample figure rather than another in-sample one.
    /// </remarks>
    private static (double[] Scores, List<(double Kappa, double Tau)> Choices) GuardedGate(
        int[] folds,
        IReadOnlyList<string> queryIds,
        double[][] features,
        (double Top1, double Top2, string[] Ids)[][] captures,
        IReadOnlyList<double[]> metrics)
    {
        const double Ridge = 5.0;
        double[] kappas = [0, 0.25, 0.5, 0.75, 1.0];
        double[] taus = [0, 0.01, 0.02, 0.05];

        int count = folds.Length;
        int routeCount = metrics.Count;

        var routeFeatures = new double[routeCount][][];
        for (int route = 0; route < routeCount; route++)
            routeFeatures[route] = Enumerable.Range(0, count)
                .Select(q => RouteFeatures(q, route, features, captures!))
                .ToArray();

        var output = new double[count];
        var choices = new List<(double Kappa, double Tau)>(folds.Max() + 1);

        for (int f = 0; f <= folds.Max(); f++)
        {
            int[] train = Enumerable.Range(0, count).Where(i => folds[i] != f).ToArray();
            int[] test = Enumerable.Range(0, count).Where(i => folds[i] == f).ToArray();

            if (test.Length == 0)
                continue;

            // A second, salted fold assignment so the inner split is not the outer one rotated.
            var inner = new int[count];
            foreach (int q in train)
                inner[q] = (int)(Fnv1a(queryIds[q] + "|inner") % 5);

            double bestInner = double.NegativeInfinity;
            double bestKappa = 0;
            double bestTau = 0;

            for (int g = 0; g < 5; g++)
            {
                int[] innerTrain = train.Where(q => inner[q] != g).ToArray();
                int[] innerTest = train.Where(q => inner[q] == g).ToArray();

                if (innerTest.Length == 0)
                    continue;

                (double[][] weights, double[] means) = FitDirectional(innerTrain, routeFeatures, metrics, Ridge);

                foreach (double kappa in kappas)
                {
                    foreach (double tau in taus)
                    {
                        // At κ = 0 the prediction is the route's average gain, so τ has no effect.
                        if (kappa == 0 && tau > 0)
                            continue;

                        double sum = 0;
                        foreach (int q in innerTest)
                            sum += metrics[Choose(q, weights, means, routeFeatures, kappa, tau)][q];

                        double mean = sum / innerTest.Length;
                        if (mean > bestInner + Epsilon)
                        {
                            bestInner = mean;
                            bestKappa = kappa;
                            bestTau = tau;
                        }
                    }
                }
            }

            (double[][] outerWeights, double[] outerMeans) = FitDirectional(train, routeFeatures, metrics, Ridge);

            foreach (int q in test)
                output[q] = metrics[Choose(q, outerWeights, outerMeans, routeFeatures, bestKappa, bestTau)][q];

            choices.Add((bestKappa, bestTau));
        }

        return (output, choices);
    }

    private static (double[][] Weights, double[] Means) FitDirectional(
        int[] rows,
        double[][][] routeFeatures,
        IReadOnlyList<double[]> metrics,
        double ridge)
    {
        int routeCount = metrics.Count;
        var weights = new double[routeCount][];
        var means = new double[routeCount];
        var target = new double[routeFeatures[0].Length];

        for (int route = 0; route < routeCount; route++)
        {
            foreach (int q in rows)
                target[q] = metrics[route][q] - metrics[0][q];

            means[route] = rows.Average(q => target[q]);
            weights[route] = RidgeFit(rows, routeFeatures[route], target, ridge);
        }

        return (weights, means);
    }

    private static int Choose(
        int query,
        double[][] weights,
        double[] means,
        double[][][] routeFeatures,
        double kappa,
        double tau)
    {
        int chosen = 0;
        double best = tau;

        for (int route = 1; route < weights.Length; route++)
        {
            double fit = Dot(weights[route], routeFeatures[route][query]);
            double predicted = means[route] + kappa * (fit - means[route]);

            if (predicted > best)
            {
                best = predicted;
                chosen = route;
            }
        }

        return chosen;
    }

    /// <summary>
    /// The features that describe a (query, pipeline) pair rather than the query alone: what the
    /// pipeline actually did on this query. Raw scores are comparable only within one pipeline, and
    /// each pipeline gets its own model, so no cross-pipeline normalization is needed.
    /// </summary>
    private static double[] RouteFeatures(
        int query,
        int route,
        double[][] queryFeatures,
        IReadOnlyList<(double Top1, double Top2, string[] Ids)[]> captures)
    {
        (double top1, double top2, string[] ids) = captures[route][query];
        string[] baselineIds = captures[0][query].Ids;

        double margin = top1 > 0 && top2 > 0 ? (top1 - top2) / top1 : 1;
        double overlap = Jaccard(ids, baselineIds);
        double unique = ids.Count(id => !baselineIds.Contains(id, StringComparer.Ordinal))
            / (double)Math.Max(1, ids.Length);

        return
        [
            queryFeatures[query][0],
            queryFeatures[query][1],
            queryFeatures[query][2],
            queryFeatures[query][3],
            top1,
            margin,
            overlap,
            unique,
        ];
    }

    /// <summary>
    /// Captures each pipeline's top scores and ranking for every query, so the directional gate
    /// can describe what a pipeline did instead of only what the query looks like.
    /// </summary>
    private static (double Top1, double Top2, string[] Ids)[] Capture(
        ITextSearchEngine engine,
        IReadOnlyList<string> queryIds,
        IReadOnlyDictionary<string, string> textById,
        int topK)
    {
        var captured = new (double, double, string[])[queryIds.Count];

        for (int i = 0; i < queryIds.Count; i++)
        {
            IReadOnlyList<SearchResult> hits = engine.Search(textById[queryIds[i]], new SearchOptions(topK));
            double top1 = hits.Count > 0 ? hits[0].Score : 0;
            double top2 = hits.Count > 1 ? hits[1].Score : 0;
            captured[i] = (top1, top2, hits.Select(hit => hit.DocumentId).ToArray());
        }

        return captured;
    }

    private static double MeanCorrelation(IReadOnlyList<double[]> metrics)
    {
        var correlations = new List<double>();

        for (int a = 0; a < metrics.Count; a++)
            for (int b = a + 1; b < metrics.Count; b++)
                correlations.Add(Pearson(metrics[a], metrics[b]));

        return correlations.Count == 0 ? 0 : correlations.Average();
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
            return double.NaN;

        double position = (sorted.Length - 1) * q;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);

        return lower == upper
            ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Pearson(double[] left, double[] right)
    {
        double meanLeft = left.Average();
        double meanRight = right.Average();

        double covariance = 0;
        double varianceLeft = 0;
        double varianceRight = 0;

        for (int i = 0; i < left.Length; i++)
        {
            double dl = left[i] - meanLeft;
            double dr = right[i] - meanRight;
            covariance += dl * dr;
            varianceLeft += dl * dl;
            varianceRight += dr * dr;
        }

        double denominator = Math.Sqrt(varianceLeft * varianceRight);
        return denominator <= Epsilon ? 0 : covariance / denominator;
    }

    private static double Jaccard(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0)
            return 1;

        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);

        int shared = leftSet.Count(rightSet.Contains);
        int union = leftSet.Count + rightSet.Count - shared;

        return union == 0 ? 1 : (double)shared / union;
    }

    private sealed record Route(string Name, ITextSearchEngine Engine);
}
