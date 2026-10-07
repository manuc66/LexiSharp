using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// What one query is allowed to allocate, component by component.
/// </summary>
/// <remarks>
/// <c>docs/benchmarks.md</c> takes a search apart into the bytes it spends — a two-term
/// tokenizer call at 264 B, a page of ten results at 72 B a row, an accumulation pass at zero —
/// and this test is the refusal to let those figures come back up. Every budget below sits well
/// above the measured value (the widest is about 40 % over) so that a real regression fails by a
/// margin rather than marginally, and so that the assertion survives a measurement detail this
/// suite does not control.
/// <para>
/// It counts with <see cref="GC.GetAllocatedBytesForCurrentThread"/> rather than a stopwatch,
/// which is the same instrument the rest of this file uses: a byte count is deterministic on a
/// single thread, so no threshold here is a guess about how busy the host is.
/// </para>
/// </remarks>
public class QueryAllocationBreakdownTests
{
    private const int Documents = 10_000;

    /// <summary>184 B for the list the tokenizer builds into, plus 40 B for each token it emits.</summary>
    private const long TokenizerBudget = 512;

    /// <summary>Measured: 1,392 B for a two-term query returning a page of ten.</summary>
    private const long SearchBudget = 2 * 1024;

    /// <summary>Measured: 72 B — a window entry, a slot in the result array, and a SearchResult.</summary>
    private const long PerRowBudget = 128;

    private static RankedTextSearchEngine BuildEngine()
    {
        var index = new InMemoryTextIndex();
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < Documents; i++)
        {
            builder.Clear();

            for (int w = 0; w < 50; w++)
                builder.Append("body").Append(w % 97).Append(' ');

            builder.Append("needleterm ");

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString()));
        }

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        return engine;
    }

    private static long BytesPerCall(Action call, int warmup = 100, int iterations = 500)
    {
        for (int i = 0; i < warmup; i++)
            call();

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
            call();

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    [Fact]
    public void TheTokenizerStaysWithinItsBudget()
    {
        // One term, then two: the difference is the per-token cost, and both are asserted against
        // the same budget because a fixed cost that grew would show up just as clearly.
        long oneTerm = BytesPerCall(() => Tokenizer.Default.Tokenize("needleterm"));
        long twoTerms = BytesPerCall(() => Tokenizer.Default.Tokenize("needleterm ranking"));

        Assert.True(
            oneTerm <= TokenizerBudget,
            $"tokenizing one term allocated {oneTerm} bytes, over the {TokenizerBudget}-byte budget");
        Assert.True(
            twoTerms <= TokenizerBudget,
            $"tokenizing two terms allocated {twoTerms} bytes, over the {TokenizerBudget}-byte budget");
    }

    [Fact]
    public void AQueryStaysWithinItsBudget()
    {
        var engine = BuildEngine();
        var options = new SearchOptions(Limit: 10);

        long perSearch = BytesPerCall(() => engine.Search("needleterm body42", options));

        Assert.True(
            perSearch <= SearchBudget,
            $"a two-term search allocated {perSearch} bytes per query over {Documents} documents, " +
            $"over the {SearchBudget}-byte budget");
    }

    [Fact]
    public void AReturnedRowStaysWithinItsBudget()
    {
        var engine = BuildEngine();
        var smallPage = new SearchOptions(Limit: 1);
        var largePage = new SearchOptions(Limit: 100);

        // Two page sizes on the same query: the difference is what the returned page costs, and
        // nothing else changes with the limit. Measured: 72 B a row.
        long small = BytesPerCall(() => engine.Search("needleterm body42", smallPage));
        long large = BytesPerCall(() => engine.Search("needleterm body42", largePage));

        long perRow = (large - small) / 99;

        Assert.True(
            perRow <= PerRowBudget,
            $"a returned result row allocated {perRow} bytes, over the {PerRowBudget}-byte budget " +
            $"({large} bytes for 100 rows against {small} for one)");
    }

    [Fact]
    public void AQueryMatchingNothingStaysWithinItsBudget()
    {
        var engine = BuildEngine();
        var options = new SearchOptions(Limit: 10);

        // Parses, plans, sizes the window, returns nothing. Measured: 1,048 B.
        long perSearch = BytesPerCall(() => engine.Search("quokka wombat", options));

        Assert.True(
            perSearch <= SearchBudget,
            $"a query matching nothing allocated {perSearch} bytes per query, over the {SearchBudget}-byte budget");
    }
}
