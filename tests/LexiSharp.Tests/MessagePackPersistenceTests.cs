using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.MessagePack;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class MessagePackPersistenceTests
{
    private static InMemoryTextIndex CreateIndex()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("1", "the search engine uses BM25 to rank the results",
                new Dictionary<string, string> { ["title"] = "Engine", ["priority"] = "high" }, "tech"),
            new SearchDocument("2", "Café résumé — naïve tokenizer", Category: "linguistics"),
            new SearchDocument("3", "italian cuisine pasta"),
        });

        return index;
    }

    [Fact]
    public void Roundtrip_PreservesDocumentsStatisticsAndSearchOrder()
    {
        var original = CreateIndex();
        var query = new SearchOptions(5);

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);

        stream.Position = 0;
        var restored = MessagePackTextIndexPersistence.Load(stream);

        Assert.Equal(original.Count, restored.Count);
        Assert.Equal(original.VocabularySize, restored.VocabularySize);
        Assert.Equal(original.CorpusTokenCount, restored.CorpusTokenCount);
        Assert.Equal(original.AverageDocumentLength, restored.AverageDocumentLength, 12);
        Assert.Equal(original.GetStatistics(), restored.GetStatistics());

        // Document contents, including fields and category, survive the roundtrip.
        foreach (var (beforeDoc, afterDoc) in original.Documents.Zip(restored.Documents, (a, b) => (a, b)))
        {
            Assert.Equal(beforeDoc.Id, afterDoc.Id);
            Assert.Equal(beforeDoc.Text, afterDoc.Text);
            Assert.Equal(beforeDoc.Category, afterDoc.Category);
            Assert.Equal(
                beforeDoc.Fields is null ? null : new Dictionary<string, string>(beforeDoc.Fields),
                afterDoc.Fields is null ? null : new Dictionary<string, string>(afterDoc.Fields));
        }

        var originalEngine = new RankedTextSearchEngine(original, new Bm25Scorer());
        var restoredEngine = new RankedTextSearchEngine(restored, new Bm25Scorer());

        var originalRanking = originalEngine.Search("bm25 rank", query);
        var restoredRanking = restoredEngine.Search("bm25 rank", query);

        Assert.Equal(
            originalRanking.Select(r => (r.DocumentId, r.Score)),
            restoredRanking.Select(r => (r.DocumentId, r.Score)));
    }

    [Fact]
    public void Roundtrip_EmptyIndex_StaysEmpty()
    {
        var original = new InMemoryTextIndex();

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;
        var restored = MessagePackTextIndexPersistence.Load(stream);

        Assert.Equal(0, restored.Count);
        Assert.Equal(0, restored.VocabularySize);
        Assert.Equal(0, restored.GetStatistics().TokenCount);
    }

    [Fact]
    public void Roundtrip_ReconstructsStopWordAndNgramConfiguration()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = true,
            NGramMin = 2,
            NGramMax = 2,
        });

        var original = new InMemoryTextIndex(tokenizer);
        original.Add(new SearchDocument("1", "the machine learning engine"));

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;
        var restored = MessagePackTextIndexPersistence.Load(stream);

        Assert.Equal(original.VocabularySize, restored.VocabularySize);
        Assert.Equal(
            original.GetTerms("1"),
            restored.GetTerms("1")); // stopwords dropped, "machine learning" bigram present in both
    }

    [Fact]
    public void Roundtrip_PreservesCustomStopWords()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = true,
            StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zorglub" },
        });

        var original = new InMemoryTextIndex(tokenizer);
        original.Add(new SearchDocument("1", "zorglub alpha"));

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;
        var restored = MessagePackTextIndexPersistence.Load(stream);

        Assert.Equal(["alpha"], restored.GetTerms("1"));
    }

    [Fact]
    public void Roundtrip_FilePathOverload_WritesAndReadsBack()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.lexisharp");
        try
        {
            var original = CreateIndex();

            MessagePackTextIndexPersistence.Save(original, filePath);
            var restored = MessagePackTextIndexPersistence.Load(filePath);

            Assert.Equal(original.GetStatistics(), restored.GetStatistics());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Load_RejectsAMismatchedExplicitTokenizer()
    {
        var original = new InMemoryTextIndex();
        original.Add(new SearchDocument("1", "alpha"));

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;

        Assert.Throws<InvalidOperationException>(() =>
            MessagePackTextIndexPersistence.Load(stream, new PassthroughTokenizer()));
    }

    [Fact]
    public void Load_RequiresTheCustomTokenizerToBeSupplied()
    {
        var original = new InMemoryTextIndex(new PassthroughTokenizer());
        original.Add(new SearchDocument("1", "ALPHA beta"));

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;

        Assert.Throws<InvalidOperationException>(() => MessagePackTextIndexPersistence.Load(stream));
    }

    [Fact]
    public void Load_WithTheSameCustomTokenizer_RestoresTheIndex()
    {
        var original = new InMemoryTextIndex(new PassthroughTokenizer());
        original.Add(new SearchDocument("1", "ALPHA beta"));

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;
        var restored = MessagePackTextIndexPersistence.Load(stream, new PassthroughTokenizer());

        Assert.Equal(1, restored.Count);
        Assert.Equal(["ALPHA beta"], restored.GetTerms("1"));
    }

    [Fact]
    public void Load_RequiresTheStemmerToBeSupplied()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            Stemmer = new SuffixStrippingStemmer(),
        });

        var original = new InMemoryTextIndex(tokenizer);
        original.Add(new SearchDocument("1", "running runners"));

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;

        Assert.Throws<InvalidOperationException>(() => MessagePackTextIndexPersistence.Load(stream));
    }

    [Fact]
    public void Load_RejectsCorruptPayloads()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<InvalidOperationException>(() => MessagePackTextIndexPersistence.Load(stream));
    }

    private sealed class PassthroughTokenizer : ITokenizer
    {
        public IReadOnlyList<string> Tokenize(string text) => [text];
    }
}