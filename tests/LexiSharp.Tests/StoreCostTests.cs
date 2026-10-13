using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;
using Xunit.Abstractions;

namespace LexiSharp.Tests;

/// <summary>
/// What a store's operations cost, pinned in bytes.
/// </summary>
/// <remarks>
/// <para>
/// The regression net for cost rather than correctness. The tests beside this one assert what a commit
/// and a search <i>produce</i>; this asserts what they <i>cost</i>, and it exists because a change that
/// made a commit allocate 570x more — and another that made a phrase query allocate 314x more — passed
/// every correctness test there was.
/// </para>
/// <para>
/// It pins the <b>allocation</b>, not the time, because allocation is the one figure that does not
/// depend on the machine. <c>GC.GetAllocatedBytesForCurrentThread</c> is exact and deterministic: the
/// same corpus committed twice allocates the same bytes, on a loaded host or a quiet one. A wall-clock
/// budget would be a different number on every machine and would fail for reasons that are not the code,
/// which is a test nobody keeps.
/// </para>
/// <para>
/// The budgets are what the operations allocate now with headroom, so a change that adds work fails
/// loudly and a change that removes it does not. Every one of them was a regression in this repository
/// before it was a budget.
/// </para>
/// </remarks>
[Trait("Category", "Cost")]
public class StoreCostTests
{
    private readonly ITestOutputHelper _output;

    public StoreCostTests(ITestOutputHelper output) => _output = output;
    private const int DocumentCount = 20_000;
    private const int VocabularySize = 100_000;
    private const int WordsPerDocument = 124;

    private static readonly SearchDocument[] Documents = BuildDocuments();

    /// <summary>
    /// A second corpus with a small vocabulary, so a phrase's terms reach most of the corpus and the
    /// gate runs per candidate. On the 100,000-term corpus a phrase matches about twenty-five documents
    /// and the row measures nothing.
    /// </summary>
    private static readonly SearchDocument[] CommonDocuments = BuildCommonDocuments();

    private static SearchDocument[] BuildCommonDocuments()
    {
        var random = new Random(11);
        var words = new string[400];

        for (int i = 0; i < words.Length; i++)
            words[i] = "word" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var documents = new SearchDocument[DocumentCount];

        for (int i = 0; i < documents.Length; i++)
        {
            var builder = new System.Text.StringBuilder(WordsPerDocument * 8);

            for (int w = 0; w < WordsPerDocument; w++)
            {
                builder.Append(words[random.Next(words.Length)]);
                builder.Append(' ');
            }

            documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString());
        }

        return documents;
    }

    private static SearchDocument[] BuildDocuments()
    {
        var random = new Random(7);
        var words = new string[VocabularySize];

        for (int i = 0; i < words.Length; i++)
            words[i] = "term" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var documents = new SearchDocument[DocumentCount];

        for (int i = 0; i < documents.Length; i++)
        {
            var builder = new System.Text.StringBuilder(WordsPerDocument * 8);

            for (int w = 0; w < WordsPerDocument; w++)
            {
                builder.Append(words[random.Next(words.Length)]);
                builder.Append(' ');
            }

            documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString());
        }

        return documents;
    }

    [Fact]
    public void ACommitAllocatesWithinItsBudget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lexisharp-cost-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SegmentedTextIndex(directory);
            store.Index(Documents);

            long before = GC.GetAllocatedBytesForCurrentThread();
            using var committed = store.Commit();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            _output.WriteLine($"commit allocation: {allocated:N0} B");
            Assert.True(
                allocated < 340L * 1024 * 1024,
                $"a commit of {DocumentCount:N0} documents allocated {allocated / (1024 * 1024)} MB, "
                + "against a 340 MB pin. This corpus commits 287 MB; the pin is that plus headroom, "
                + "because allocation is deterministic and the reference is 1,736 MB at 100,000 "
                + "documents. A commit that allocates far more is the regression this exists for — one "
                + "measured at 191,805 MB once.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The overlay build, which is four times what the commit costs and was not pinned.
    /// </summary>
    /// <remarks>
    /// A commit excludes this: <c>Index</c> builds the overlay and <c>Commit</c> writes it, and the
    /// three pins beside this one only ever measured the second. Measured on a 100,000-document corpus,
    /// loading is 12,644 ms of `Index` against 2,373 ms of `Commit` — so the guard was watching the
    /// half that is 16% of the operation a caller actually performs. Like the others this pins bytes,
    /// because bytes do not depend on the machine.
    /// </remarks>
    [Fact]
    public void AnOverlayBuildAllocatesWithinItsBudget()
    {
        var store = new SegmentedTextIndex(
            Path.Combine(Path.GetTempPath(), "lexisharp-cost-" + Guid.NewGuid().ToString("N")));

        long before = GC.GetAllocatedBytesForCurrentThread();
        store.Index(Documents);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"index allocation: {allocated:N0} B");

        Assert.True(
            allocated < 800L * 1024 * 1024,
            $"building the overlay over {DocumentCount:N0} documents allocated {allocated / (1024 * 1024)} MB, "
            + "against an 800 MB pin. This corpus builds 721 MB — 2.5x the commit's 287 MB, and the side "
            + "of loading a corpus the commit's pin does not see. 304 B per token is the figure to "
            + "attack: the tokenizer allocates a string per token here, and Add hashes each one more "
            + "than once.");
    }

    [Fact]
    public void APhraseQueryAllocatesWithinItsBudget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lexisharp-cost-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SegmentedTextIndex(directory);
            store.Index(CommonDocuments);

            using var committed = store.Commit();
            var engine = new RankedTextSearchEngine(committed, new Bm25Scorer());
            var options = new SearchOptions(10, ParseQuerySyntax: true);
            string query = "\"word7 word11\"";

            engine.Search(query, options);

            long before = GC.GetAllocatedBytesForCurrentThread();
            engine.Search(query, options);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            _output.WriteLine($"phrase allocation: {allocated:N0} B");
            Assert.True(
                allocated < 1024L * 1024,
                $"a phrase query allocated {allocated} bytes, against a 1 MB pin. This corpus reads "
                + "362 KB; before the segment stored positions the same query allocated 1.45 GB, because "
                + "the gate re-tokenized the document for every candidate. The pin is that number's "
                + "tripwire.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void APrefixQueryAllocatesWithinItsBudget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lexisharp-cost-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SegmentedTextIndex(directory);
            store.Index(Documents);

            using var committed = store.Commit();
            var engine = new RankedTextSearchEngine(committed, new Bm25Scorer());
            var options = new SearchOptions(10, ParseQuerySyntax: true);
            string query = "term1234*";

            engine.Search(query, options);

            long before = GC.GetAllocatedBytesForCurrentThread();
            engine.Search(query, options);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            _output.WriteLine($"prefix allocation: {allocated:N0} B");
            Assert.True(
                allocated < 64L * 1024,
                $"a prefix query allocated {allocated} bytes, against a 64 KB pin. This corpus reads "
                + "31,104 B; walking the whole vocabulary and decoding every term cost 6.9 MB at a "
                + "100,000-term dictionary before the prefix range was added.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
