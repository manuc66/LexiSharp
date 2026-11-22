using System.Diagnostics;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What the vocabulary costs at scale: the index's own bytes, the ordered set's node overhead, and
/// the fuzzy trie's bytes and build time.
/// </summary>
/// <remarks>
/// <para>
/// The fuzzy trie is declined above 250,000 terms because its node-per-character cost was never
/// measured at that size; this lane forces the build so the ceiling can be read instead of guessed.
/// The ordered set is measured as nodes over the index's own strings, so the byte figure is the
/// structure's overhead and not the terms it points at.
/// </para>
/// <para>
/// <c>GC.GetTotalMemory(true)</c> after a full collection, one size per process. The host is
/// shared and the collector is concurrent, so read the bytes per term and the direction between
/// sizes, not the last digit.
/// </para>
/// </remarks>
internal static class VocabularyMemoryLane
{
    private static readonly int[] DefaultSizes = [200_000, 1_000_000];

    public static void Run(string[] args)
    {
        int[] sizes = args.Length == 0
            ? DefaultSizes
            : [.. args.Select(argument => ParsePositive(argument, "terms"))];

        Console.WriteLine("LexiSharp vocabulary-memory lane — index, ordered set and fuzzy trie at scale");
        Console.WriteLine($"host  {Environment.ProcessorCount} logical processors, {Environment.Version}");

        foreach (int size in sizes)
            Measure(size);
    }

    private static void Measure(int terms)
    {
        long beforeIndex = GC.GetTotalMemory(forceFullCollection: true);
        var build = Stopwatch.StartNew();
        var index = new InMemoryTextIndex { FuzzyTrieTermLimit = int.MaxValue };

        index.Index(OneTermPerDocument(terms));
        build.Stop();

        long afterIndex = GC.GetTotalMemory(forceFullCollection: true);

        // The strings are the index's own, so the delta is the ordered set's nodes and nothing else.
        long beforeSet = GC.GetTotalMemory(forceFullCollection: true);
        var ordered = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string term in index.Vocabulary)
            ordered.Add(term);

        long afterSet = GC.GetTotalMemory(forceFullCollection: true);
        int orderedCount = ordered.Count;
        ordered.Clear();

        long beforeTrie = GC.GetTotalMemory(forceFullCollection: true);
        var trieBuild = Stopwatch.StartNew();
        bool built = ((IVocabularyFuzzyIndex)index).TryGetTermsWithinEdits("term0000000", 0, out var matches);
        trieBuild.Stop();
        long afterTrie = GC.GetTotalMemory(forceFullCollection: true);

        Console.WriteLine();
        Console.WriteLine($"--- {Number(terms)} terms ---");
        Console.WriteLine(
            $"index        {Time(build.Elapsed.TotalMilliseconds * 1_000)} build, " +
            $"{Megabytes(afterIndex - beforeIndex)} total, " +
            $"{BytesPerTerm(afterIndex - beforeIndex, index.VocabularySize)}/term, " +
            $"{Number(index.VocabularySize)} terms");
        Console.WriteLine(
            $"ordered set  {Megabytes(afterSet - beforeSet)} for {Number(orderedCount)} nodes, " +
            $"{BytesPerTerm(afterSet - beforeSet, orderedCount)}/term");
        Console.WriteLine(
            $"fuzzy trie   {Time(trieBuild.Elapsed.TotalMilliseconds * 1_000)} build, " +
            $"{Megabytes(afterTrie - beforeTrie)}, " +
            $"{BytesPerTerm(afterTrie - beforeTrie, index.VocabularySize)}/term, " +
            $"served {built}, {Number(matches.Count)} matches");
    }

    /// <summary>One document per term, so the vocabulary is exactly the corpus's terms.</summary>
    private static IEnumerable<SearchDocument> OneTermPerDocument(int count)
    {
        for (int i = 0; i < count; i++)
        {
            string term = "term" + i.ToString("D7", CultureInfo.InvariantCulture);
            yield return new SearchDocument(term, term);
        }
    }

    private static int ParsePositive(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"vocabulary-memory expects a positive {name}, got '{value}'.");

        return parsed;
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MiB";

    private static string BytesPerTerm(long bytes, int terms) =>
        terms == 0 ? "n/a" : (bytes / (double)terms).ToString("N0", CultureInfo.InvariantCulture) + " B";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Time(double microseconds) => microseconds >= 1_000
        ? (microseconds / 1_000).ToString("N1", CultureInfo.InvariantCulture) + " ms"
        : microseconds.ToString("N1", CultureInfo.InvariantCulture) + " µs";
}
