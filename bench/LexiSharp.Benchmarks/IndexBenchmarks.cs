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
            if (term.EndsWith("s", StringComparison.Ordinal))
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
        var random = new Random(42);
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
}