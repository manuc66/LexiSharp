using System.Text;
using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

[MemoryDiagnoser]
public class IndexBenchmarks
{
    private SearchDocument[] _documents = Array.Empty<SearchDocument>();

    [Params(1_000, 10_000)]
    public int DocumentCount { get; set; }

    [Params(50)]
    public int WordsPerDocument { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _documents = CorpusFactory.CreateDocuments(DocumentCount, WordsPerDocument);
    }

    [Benchmark]
    public int BuildInvertedIndex()
    {
        var index = new InMemoryTextIndex();
        index.Index(_documents);
        return index.Count;
    }

    [Benchmark]
    public int BuildIndexWithStopWordsAndStemmer()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = true,
            Stemmer = new SuffixStripStemmer(),
        });
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(_documents);
        return index.Count;
    }

    private sealed class SuffixStripStemmer : IStemmer
    {
        public string Stem(string term)
        {
            if (term.EndsWith("ing", StringComparison.Ordinal))
                return term[..^3];
            if (term.EndsWith("ed", StringComparison.Ordinal))
                return term[..^2];
            if (term.EndsWith('s'))
                return term[..^1];
            return term;
        }
    }
}

internal static class CorpusFactory
{
    private static readonly string[] Words =
    {
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
        "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
        "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey",
        "xray", "yankee", "zulu", "search", "engine", "index", "query", "score",
        "rank", "document", "token", "term", "corpus", "collection", "language",
    };

    public static SearchDocument[] CreateDocuments(int count, int wordsPerDocument)
    {
        var random = new Random(42); // NOSONAR:S2245 (fixed-seed synthetic corpus for reproducible benchmarks)
        var documents = new SearchDocument[count];

        for (int i = 0; i < count; i++)
        {
            var builder = new StringBuilder(wordsPerDocument * 8);
            for (int w = 0; w < wordsPerDocument; w++)
            {
                builder.Append(Words[random.Next(Words.Length)]);
                builder.Append(' ');
            }

            documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString());
        }

        return documents;
    }

    /// <summary>Zipf exponent, a common value for English unigram frequencies.</summary>
    private const double ZipfExponent = 1.07;

    /// <summary>
    /// A corpus with a realistic term distribution: <paramref name="count"/> documents of
    /// <paramref name="wordsPerDocument"/> words drawn Zipfianly from a
    /// <paramref name="vocabulary"/>-word vocabulary, so the head terms sit in most documents and
    /// the tail terms in a handful.
    /// </summary>
    /// <remarks>
    /// This exists for selectivity. <see cref="CreateDocuments"/> draws from 38 words, which puts
    /// every term in roughly 74% of documents, so the search engine's « score candidates instead
    /// of scanning the corpus » path never triggers there and no benchmark on that corpus can say
    /// anything about it. A tail term here lands in a few percent of documents, which is the
    /// regime real queries are in.
    /// </remarks>
    public static SearchDocument[] CreateZipfDocuments(
        int count = 10_000, int wordsPerDocument = 50, int vocabulary = 30_000)
    {
        var words = new string[vocabulary];
        for (int i = 0; i < vocabulary; i++)
            words[i] = "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x";

        var cumulative = new double[vocabulary];
        double total = 0;

        for (int i = 0; i < vocabulary; i++)
        {
            total += 1.0 / Math.Pow(i + 1, ZipfExponent);
            cumulative[i] = total;
        }

        for (int i = 0; i < vocabulary; i++)
            cumulative[i] /= total;

        var random = new Random(7); // NOSONAR:S2245 (fixed-seed synthetic corpus for reproducible benchmarks)
        var documents = new SearchDocument[count];

        for (int i = 0; i < count; i++)
        {
            var builder = new StringBuilder(wordsPerDocument * 9);

            for (int w = 0; w < wordsPerDocument; w++)
            {
                double sample = random.NextDouble();
                int slot = Array.BinarySearch(cumulative, sample);

                // BinarySearch returns the complement of the insertion point when the sample falls
                // between two entries, which is the bucket this term falls in.
                if (slot < 0)
                    slot = ~slot;

                if (slot >= vocabulary)
                    slot = vocabulary - 1;

                builder.Append(words[slot]).Append(' ');
            }

            documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString());
        }

        return documents;
    }
}