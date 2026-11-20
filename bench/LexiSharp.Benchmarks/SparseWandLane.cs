using System.Diagnostics;
using System.Globalization;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The share of a learned-sparse query's posting entries a block bound could ever skip, on a
/// synthetic SPLADE-shaped corpus laid out the way a sparse index would be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers, and the one it does not.</b> A block-max WAND over sparse vectors is
/// worth building only if there is headroom to skip, and the headroom is a property of the posting
/// lists, not of the traversal. This lane measures the <b>ceiling</b>: for each 128-ordinal window it
/// computes the largest score any document in that window could reach from the query's terms —
/// <c>Σ_t w_q(t) · max_{d ∈ window} w_d(t)</c>, a true upper bound on a dot product — and counts the
/// entries that sit in windows whose bound is below the query's final top-10 threshold. That is the
/// most a traversal holding the final threshold could skip; a real WAND starts with a threshold of
/// zero, so it realizes less. A low ceiling is therefore conclusive — no traversal can do better —
/// while a high one only says a traversal is worth measuring next.
/// </para>
/// <para>
/// <b>Why contiguous arrays rather than <c>SparseTextSearchEngine</c>.</b> The engine in the library
/// stores a <c>Dictionary&lt;term, Dictionary&lt;doc, weight&gt;&gt;</c>, so a query costs dictionary
/// lookups and pointer chasing per entry. Measuring pruning there would measure the prototype's
/// indirection, not the pruning. The corpus here is what a sparse index would store — one
/// <c>(ordinal, weight)</c> array per term, postings sorted by ordinal — so the number is the ceiling
/// for the layout the feature would be built on. The output says so again.
/// </para>
/// <para>
/// <b>Why the corpus is synthetic.</b> A SPLADE encoding needs a model, and this repository ships
/// none; the harness's own sparse rows come from a script under <c>bench/LexiSharp.Eval/sparse/</c>.
/// The shape here is the one SPLADE produces, not a measurement of it: each document draws 200–300
/// terms from a Zipf popularity over a 30,000-term vocabulary and keeps the distinct ones — the
/// 100–200 positive terms of a real sparse vector — with weights skewed towards the low end. Queries
/// draw their terms by document frequency, which is the regime an LLM's expansion lands in, and the
/// keyword shape is the control.
/// </para>
/// </remarks>
internal static class SparseWandLane
{
    /// <summary>Ordinals per block, unless the run names another: <c>PostingBlocks</c> writes 128.</summary>
    private const int DefaultBlockSpan = 128;

    private const int VocabularySize = 30_000;
    private const int Limit = 10;

    private const int DefaultDocuments = 50_000;
    private const int DefaultQueries = 4;

    public static void Run(string[] args)
    {
        int documents = args.Length > 0 ? ParsePositive(args[0], "documents") : DefaultDocuments;
        int queries = args.Length > 1 ? ParsePositive(args[1], "queries") : DefaultQueries;
        int blockSpan = args.Length > 2 ? ParsePositive(args[2], "block span") : DefaultBlockSpan;

        Console.WriteLine(
            $"sparse-wand   {Number(documents)} documents, {Number(queries)} queries per shape, " +
            $"vocabulary {Number(VocabularySize)}, windows of {Number(blockSpan)} ordinals");
        Console.WriteLine(
            "layout        contiguous (ordinal, weight) arrays per term — the format, not the dictionary engine");

        var generation = Stopwatch.StartNew();
        var corpus = SparseCorpus.Generate(documents, VocabularySize, seed: 20_261);
        corpus.BlockSpan = blockSpan;
        generation.Stop();

        Console.WriteLine(
            $"corpus        {Number(corpus.DocumentCount)} documents, {Number(corpus.Terms)} terms, " +
            $"{Number(corpus.Postings)} postings ({Number(corpus.Postings / corpus.DocumentCount)} per document), " +
            $"generated in {generation.Elapsed.TotalSeconds:0.00}s");

        foreach (Shape shape in new[] { Shape.Expanded, Shape.Keyword })
            Measure(corpus, shape, queries);
    }

    private static void Measure(SparseCorpus corpus, Shape shape, int queries)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {Label(shape)} ---");

        double entries = 0;
        double skipped = 0;

        for (int q = 0; q < queries; q++)
        {
            var query = corpus.CreateQuery(shape, seed: 9_000 + q);

            var exhaustive = Stopwatch.StartNew();
            float[] scores = corpus.Score(query.Terms, query.Weights);
            exhaustive.Stop();

            double threshold = Threshold(scores);
            var pruning = corpus.MeasurePruning(query.Terms, query.Weights, threshold);

            entries += pruning.Entries;
            skipped += pruning.Skipped;

            Console.WriteLine(
                $"  q{q}  {Number(query.Terms.Length),3} terms  " +
                $"postings {Number(pruning.Entries),9}  " +
                $"windows {Number(pruning.OccupiedWindows),7}  " +
                $"entries/window {Fixed(pruning.Entries / (double)Math.Max(pruning.OccupiedWindows, 1), 6, 2)}  " +
                $"threshold {threshold.ToString("0.####", CultureInfo.InvariantCulture),7}  " +
                $"ceiling {Percent(pruning.Skipped / (double)Math.Max(pruning.Entries, 1), 1),7}  " +
                $"exhaustive {exhaustive.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        }

        Console.WriteLine(
            $"  mean ceiling over {Number(queries)} queries: " +
            $"{Percent(skipped / Math.Max(entries, 1), 1)} of {Number((long)entries)} entries");
    }

    /// <summary>
    /// The tenth-best score, which is the threshold a top-10 traversal ends at.
    /// </summary>
    private static double Threshold(float[] scores)
    {
        var best = new double[Limit];

        foreach (float score in scores)
        {
            if (score <= best[Limit - 1])
                continue;

            int position = Limit - 1;

            while (position > 0 && score > best[position - 1])
            {
                best[position] = best[position - 1];
                position--;
            }

            best[position] = score;
        }

        return best[Limit - 1];
    }

    private enum Shape
    {
        Expanded,
        Keyword,
    }

    private static string Label(Shape shape) => shape switch
    {
        Shape.Expanded => "expanded query (40 terms, weights 0.5–3, drawn by document frequency)",
        _ => "keyword query (10 terms, unit weights, drawn by document frequency)",
    };

    private static int ParsePositive(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"sparse-wand expects a positive {name}, got '{value}'.");

        return parsed;
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double fraction, int decimals) =>
        (fraction * 100).ToString($"F{decimals}", CultureInfo.InvariantCulture) + " %";

    private static string Fixed(double value, int width, int decimals) =>
        value.ToString($"F{decimals}", CultureInfo.InvariantCulture).PadLeft(width);

    private readonly record struct Query(int[] Terms, float[] Weights);

    private readonly record struct Pruning(long Entries, long OccupiedWindows, long Skipped);

    private sealed class SparseCorpus
    {
        private readonly int[] _start;
        private readonly int[] _count;
        private readonly int[] _ordinals;
        private readonly float[] _weights;
        private readonly double[] _byFrequency;

        private SparseCorpus(
            int documentCount,
            int[] start,
            int[] count,
            int[] ordinals,
            float[] weights)
        {
            DocumentCount = documentCount;
            Terms = start.Length;
            _start = start;
            _count = count;
            _ordinals = ordinals;
            _weights = weights;

            // A query draws its terms by document frequency: the words a model expands into are the
            // words the corpus contains, not the ones it does not.
            _byFrequency = new double[Terms];
            double total = 0;

            for (int term = 0; term < Terms; term++)
            {
                total += count[term];
                _byFrequency[term] = total;
            }
        }

        public int DocumentCount { get; }

        /// <summary>Ordinals per window; the lane varies it to see how much of the ceiling is the window's size.</summary>
        public int BlockSpan { get; set; } = DefaultBlockSpan;

        public int Terms { get; }

        public long Postings => _ordinals.LongLength;

        /// <summary>
        /// Builds the corpus by counting sort: one pass counts each term's postings, then the entries
        /// are written into per-term contiguous slices in document order.
        /// </summary>
        public static SparseCorpus Generate(int documents, int vocabulary, int seed)
        {
            double[] cumulative = Popularity(vocabulary);
            var counts = new int[vocabulary];

            for (int document = 0; document < documents; document++)
            {
                foreach (int term in DocumentTerms(document, cumulative))
                    counts[term]++;
            }

            var start = new int[vocabulary + 1];

            for (int term = 0; term < vocabulary; term++)
                start[term + 1] = start[term] + counts[term];

            var ordinals = new int[start[vocabulary]];
            var weights = new float[ordinals.Length];
            var cursor = new int[vocabulary];
            Array.Copy(start, cursor, vocabulary);

            for (int document = 0; document < documents; document++)
            {
                foreach (int term in DocumentTerms(document, cumulative))
                {
                    int index = cursor[term]++;
                    ordinals[index] = document;
                    weights[index] = Weight(document, term);
                }
            }

            return new SparseCorpus(documents, start[..vocabulary], counts, ordinals, weights);
        }

        /// <summary>
        /// Scores every document by the sparse dot product, so the lane has the answer the pruning
        /// must reproduce and the threshold it must respect.
        /// </summary>
        public float[] Score(int[] terms, float[] weights)
        {
            var scores = new float[DocumentCount];

            for (int i = 0; i < terms.Length; i++)
            {
                int start = _start[terms[i]];
                int count = _count[terms[i]];
                float weight = weights[i];

                for (int p = start; p < start + count; p++)
                    scores[_ordinals[p]] += weight * _weights[p];
            }

            return scores;
        }

        /// <summary>
        /// The ceiling: entries that sit in a window whose largest possible score is below
        /// <paramref name="threshold"/>.
        /// </summary>
        public Pruning MeasurePruning(int[] terms, float[] weights, double threshold)
        {
            var windowBound = new double[(DocumentCount / BlockSpan) + 1];
            var touched = new List<int>();

            for (int i = 0; i < terms.Length; i++)
            {
                int start = _start[terms[i]];
                int end = start + _count[terms[i]];
                float queryWeight = weights[i];

                int p = start;

                while (p < end)
                {
                    int window = _ordinals[p] / BlockSpan;
                    float max = 0;

                    while (p < end && _ordinals[p] / BlockSpan == window)
                    {
                        if (_weights[p] > max)
                            max = _weights[p];

                        p++;
                    }

                    if (windowBound[window] == 0)
                        touched.Add(window);

                    windowBound[window] += queryWeight * max;
                }
            }

            long entries = 0;
            long skipped = 0;

            for (int i = 0; i < terms.Length; i++)
            {
                int start = _start[terms[i]];
                int end = start + _count[terms[i]];

                for (int p = start; p < end; p++)
                {
                    entries++;

                    // Strictly below: a window whose bound ties the threshold can still place a
                    // document in the page through the tie-break, so it is not skippable evidence.
                    if (windowBound[_ordinals[p] / BlockSpan] < threshold)
                        skipped++;
                }
            }

            return new Pruning(entries, touched.Count, skipped);
        }

        public Query CreateQuery(Shape shape, int seed)
        {
            var random = new Random(seed);
            int wanted = shape == Shape.Expanded ? 40 : 10;
            var distinct = new HashSet<int>(wanted);

            // Drawn with replacement and deduplicated, which is what a head-heavy popularity
            // distribution can support without a coupon-collector loop.
            for (int draw = 0; draw < wanted * 2 && distinct.Count < wanted; draw++)
            {
                int term = Sample(random, _byFrequency);

                // A zero-frequency term cannot happen here: every vocabulary term is reachable, and
                // the probability of drawing none is zero.
                distinct.Add(term);
            }

            int[] terms = [.. distinct];
            Array.Sort(terms);
            var weights = new float[terms.Length];

            for (int i = 0; i < terms.Length; i++)
            {
                weights[i] = shape == Shape.Expanded
                    ? 0.5f + (3.0f - 0.5f) * (float)random.NextDouble()
                    : 1.0f;
            }

            return new Query(terms, weights);
        }

        /// <summary>
        /// Zipf popularity with exponent 1 over the vocabulary, normalized to a cumulative
        /// distribution.
        /// </summary>
        private static double[] Popularity(int vocabulary)
        {
            var cumulative = new double[vocabulary];
            double total = 0;

            for (int term = 0; term < vocabulary; term++)
                total += 1.0 / (term + 1.0);

            double running = 0;

            for (int term = 0; term < vocabulary; term++)
            {
                running += 1.0 / (term + 1.0) / total;
                cumulative[term] = running;
            }

            return cumulative;
        }

        /// <summary>
        /// One document's positive terms: 200–300 draws from the popularity, deduplicated, which is
        /// the ~100–200 of a real sparse vector. Deterministic per document, and replayed by both
        /// passes rather than materialized.
        /// </summary>
        private static IEnumerable<int> DocumentTerms(int document, double[] cumulative)
        {
            var state = new Random(unchecked(document * 397 + 1));
            int draws = 200 + state.Next(101);
            var terms = new HashSet<int>(draws);

            for (int draw = 0; draw < draws; draw++)
                terms.Add(Sample(state, cumulative));

            return terms;
        }

        /// <summary>
        /// A weight per (document, term), skewed towards the low end the way a ReLU'd logit is — many
        /// small values, a few large ones, which is what makes a window's maximum informative.
        /// </summary>
        private static float Weight(int document, int term)
        {
            uint hash = unchecked((uint)((document * 37_476_139) ^ (term * 668_265_263)));
            hash = unchecked((hash ^ (hash >> 13)) * 1_274_126_177);
            hash ^= hash >> 16;

            double u = hash / (double)uint.MaxValue;
            return 0.1f + (2.9f * (float)(u * u));
        }

        private static int Sample(Random random, double[] cumulative)
        {
            double target = random.NextDouble() * cumulative[^1];
            int index = Array.BinarySearch(cumulative, target);

            return index < 0 ? ~index : index;
        }
    }
}
