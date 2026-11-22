using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What a prefix atom costs when the vocabulary can answer a range, against the whole-vocabulary
/// walk the resolver falls back to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="QueryExpansionResolver"/> resolves <c>term*</c> by asking the index for the terms with
/// that prefix. An index that only enumerates its vocabulary makes every atom a full scan; an index
/// that keeps the vocabulary ordered can binary-search the range. This lane runs the same resolver
/// against both views of the same 200,000-term corpus, so the difference is the lookup and not the
/// scoring.
/// </para>
/// <para>
/// The ordered vocabulary is built lazily on the first prefix query and invalidated by a write. The
/// lane reports the first query (which pays the sort) separately from the warm queries, because a
/// read-only corpus pays the sort once while a corpus that writes between queries pays it again.
/// </para>
/// <para>
/// Fuzzy atoms are unchanged: they still walk the vocabulary. Making edit-distance search sub-linear
/// needs a different index (a trie with a Levenshtein automaton, or a BK-tree), and this lane does not
/// claim one.
/// </para>
/// </remarks>
internal static class VocabularyLane
{
    private const int DefaultTerms = 200_000;
    private const int Iterations = 20;

    private static readonly string[] Prefixes = ["term1999", "term19999", "term199999"];

    private static long _sink;

    public static void Run(string[] args)
    {
        int terms = args.Length > 0 ? ParsePositive(args[0], "terms") : DefaultTerms;

        Console.WriteLine($"prefix-expansion   {Number(terms)} terms, {Prefixes.Length} prefixes, {Iterations} runs each");

        var generation = Stopwatch.StartNew();
        var index = new InMemoryTextIndex();

        for (int i = 0; i < terms; i++)
            index.Add(new SearchDocument(Term(i), Term(i)));

        generation.Stop();

        Console.WriteLine(
            $"index               {Number(index.Count)} documents, {Number(index.VocabularySize)} terms, " +
            $"built in {generation.Elapsed.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture)} ms");

        var fallback = new VocabularyOnly(index.Vocabulary, _ => 1);

        foreach (string prefix in Prefixes)
            Measure(index, fallback, prefix);

        foreach (string term in new[] { "term199999", "term19999" })
            MeasureFuzzy(index, fallback, term);
    }

    /// <summary>
    /// One fuzzy atom through the index's trie and through a vocabulary with no fuzzy structure.
    /// The cold column is the first query, which builds the trie.
    /// </summary>
    private static void MeasureFuzzy(InMemoryTextIndex index, VocabularyOnly fallback, string term)
    {
        var atom = new QueryExpansion(term, QueryExpansionKind.Fuzzy);

        long coldStarted = Stopwatch.GetTimestamp();
        int matches = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false).Resolve(atom).Length;
        double cold = Stopwatch.GetElapsedTime(coldStarted).TotalMicroseconds;

        double ordered = Median(
            () => new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false).Resolve(atom).Length,
            iterations: 5);

        double walk = Median(
            () => new QueryExpansionResolver(fallback, fuzzyOnlyOutOfVocabulary: false).Resolve(atom).Length,
            iterations: 3);

        Console.WriteLine(
            $"  {term + "~",-12} matches {Number(matches),5}  cold {Time(cold),9}  " +
            $"trie {Time(ordered),9}  walk {Time(walk),9}  " +
            $"speedup {Fixed(walk / Math.Max(ordered, 0.01), 6, 1)}×");
    }

    private static void Measure(InMemoryTextIndex index, VocabularyOnly fallback, string prefix)
    {
        var orderedResolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);
        var atom = new QueryExpansion(prefix, QueryExpansionKind.Prefix);

        long cold = Stopwatch.GetTimestamp();
        string[] first = orderedResolver.Resolve(atom);
        double coldMicroseconds = Stopwatch.GetElapsedTime(cold).TotalMicroseconds;

        double ordered = Median(() =>
            new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false).Resolve(atom).Length);

        double walking = Median(() =>
            new QueryExpansionResolver(fallback, fuzzyOnlyOutOfVocabulary: false).Resolve(atom).Length);

        _sink += first.Length;

        Console.WriteLine(
            $"  {prefix,-10} matches {Number(first.Length),5}  " +
            $"cold {Time(coldMicroseconds),8}  " +
            $"ordered {Time(ordered),8}  walk {Time(walking),8}  " +
            $"speedup {Fixed(walking / Math.Max(ordered, 0.01), 6, 1)}×");
    }

    private static double Median(Func<int> run, int iterations = Iterations)
    {
        var samples = new double[iterations];

        for (int i = 0; i < iterations; i++)
        {
            long started = Stopwatch.GetTimestamp();
            _sink += run();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        Array.Sort(samples);

        return samples[iterations / 2];
    }

    private static string Term(int index) => "term" + index.ToString("D6", CultureInfo.InvariantCulture);

    private static int ParsePositive(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"prefix-expansion expects a positive {name}, got '{value}'.");

        return parsed;
    }

    private static string Time(double microseconds) => microseconds >= 1_000
        ? (microseconds / 1_000).ToString("N2", CultureInfo.InvariantCulture) + " ms"
        : microseconds.ToString("N2", CultureInfo.InvariantCulture) + " µs";

    private static string Fixed(double value, int width, int decimals) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            .PadLeft(width);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The same vocabulary with no range capability — the shape the resolver's fallback reads. It is
    /// a view, not a second index: the terms are the index's own strings.
    /// </summary>
    private sealed class VocabularyOnly(IEnumerable<string> vocabulary, Func<string, int> documentFrequency)
        : IVocabularyIndex
    {
        private readonly string[] _terms = vocabulary.ToArray();

        public IEnumerable<string> Vocabulary => _terms;

        public IReadOnlyCollection<SearchDocument> Documents => Array.Empty<SearchDocument>();

        public int Count => _terms.Length;

        public double AverageDocumentLength => 1;

        public int VocabularySize => _terms.Length;

        public long CorpusTokenCount => _terms.Length;

        public bool Contains(string documentId) => false;

        public IReadOnlyList<string> GetTerms(string documentId) => Array.Empty<string>();

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) => Array.Empty<int>();

        public int DocumentFrequency(string term) => documentFrequency(term);

        public int CorpusFrequency(string term) => documentFrequency(term);

        public int TermFrequency(string documentId, string term) => 0;

        public int DocumentLength(string documentId) => 0;

        public bool TryGetDocument(
            string documentId,
            [NotNullWhen(true)] out SearchDocument? document)
        {
            document = null;
            return false;
        }
    }
}
