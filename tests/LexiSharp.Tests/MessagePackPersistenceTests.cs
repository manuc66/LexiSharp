using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.MessagePack;
using LexiSharp.Ranking;
using MessagePack;
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
    public void Roundtrip_WithShippedStemmer_RestoresAQueryableIndex()
    {
        // The happy path behind the guard above, with the stemmer the core actually ships: the
        // index is rebuilt from stemmed terms, so loading it with a different pipeline would
        // silently answer the wrong queries.
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new PorterStemmer() });

        var original = new InMemoryTextIndex(tokenizer);
        original.Index(new[]
        {
            new SearchDocument("1", "indexing the documents"),
            new SearchDocument("2", "italian cuisine pasta"),
        });

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;

        var restored = MessagePackTextIndexPersistence.Load(stream, tokenizer);

        Assert.Equal(["index", "the", "document"], restored.GetTerms("1"));

        var engine = new RankedTextSearchEngine(restored, new Bm25Scorer(), tokenizer);

        Assert.Equal(["1"], engine.Search("indexes").Select(result => result.DocumentId).ToArray());
    }

    [Fact]
    public void Load_RejectsCorruptPayloads()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<InvalidOperationException>(() => MessagePackTextIndexPersistence.Load(stream));
    }

    [Fact]
    public void Roundtrip_PreservesTextFieldsAndTheirStatistics()
    {
        var original = new InMemoryTextIndex();
        original.Index(new[]
        {
            new SearchDocument("1", "the body of the article", TextFields: new Dictionary<string, string>
            {
                ["title"] = "refresher guide",
            }),
            new SearchDocument("2", "another body", TextFields: new Dictionary<string, string>
            {
                ["title"] = "second article",
            }),
        });

        using var stream = new MemoryStream();
        MessagePackTextIndexPersistence.Save(original, stream);
        stream.Position = 0;
        var reloaded = MessagePackTextIndexPersistence.Load(stream);

        // A text field lost on the way out would silently change every field-weighted ranking, so
        // assert on the reconstructed documents, not only on the reloaded index.
        Assert.True(reloaded.TryGetDocument("1", out var first));
        Assert.Equal("refresher guide", first!.TextFields!["title"]);

        Assert.Equal([TextFields.Default, "title"], reloaded.Fields);
        Assert.Equal(1, reloaded.FieldTermFrequency("1", "title", "guide"));
        Assert.Equal(2, reloaded.FieldLength("1", "title"));
        Assert.Equal(2.0, reloaded.AverageFieldLength("title"), 6);

        // The flat view must survive too, or the reloaded index would score differently.
        Assert.Equal(7, reloaded.DocumentLength("1"));
        Assert.Equal(
            new Bm25Scorer().Score("1", ["guide"], original),
            new Bm25Scorer().Score("1", ["guide"], reloaded),
            12);
    }

    [Fact]
    public void Load_StillReadsAVersion1PayloadWhichHadNoTextFields()
    {
        // Hand-built version 1 payload: the shape this library wrote before TextFields existed.
        // It must load, with no text fields, rather than being rejected as an unknown version.
        var stored = new StoredIndex(
            1,
            new StoredTokenizer(typeof(Tokenizer).FullName!, false, 1, 1, false, null, false),
            [new StoredDocument("1", "the body of the article", null, null, null)]);

        using var stream = new MemoryStream();
        MessagePackSerializer.Serialize(
            stream,
            stored,
            MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
        stream.Position = 0;

        var reloaded = MessagePackTextIndexPersistence.Load(stream);

        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.TryGetDocument("1", out var document));
        Assert.Null(document!.TextFields);
        Assert.Equal([TextFields.Default], reloaded.Fields);
        Assert.Equal(5, reloaded.DocumentLength("1"));
    }

    [Fact]
    public void Load_StillRejectsAVersionThisBuildDoesNotKnow()
    {
        var stored = new StoredIndex(
            0,
            new StoredTokenizer(typeof(Tokenizer).FullName!, false, 1, 1, false, null, false),
            []);

        using var stream = new MemoryStream();
        MessagePackSerializer.Serialize(
            stream,
            stored,
            MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
        stream.Position = 0;

        Assert.Throws<NotSupportedException>(() => MessagePackTextIndexPersistence.Load(stream));
    }

    private sealed class PassthroughTokenizer : ITokenizer
    {
        public IReadOnlyList<string> Tokenize(string text) => [text];
    }
}