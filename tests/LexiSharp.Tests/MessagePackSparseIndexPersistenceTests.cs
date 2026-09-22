using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.MessagePack;
using Xunit;

namespace LexiSharp.Tests;

public class MessagePackSparseIndexPersistenceTests
{
    private sealed class StubSparseProvider : ISparseEmbeddingProvider
    {
        private readonly Func<string, IReadOnlyDictionary<string, float>> _lookup;

        public StubSparseProvider(Func<string, IReadOnlyDictionary<string, float>> lookup)
            => _lookup = lookup;

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(_lookup(text));
    }

    private static SparseTextSearchEngine CreateEngine()
    {
        var provider = new StubSparseProvider(text => text switch
        {
            "red apple" => new Dictionary<string, float> { ["apple"] = 1.5f, ["red"] = 1f },
            "green apple" => new Dictionary<string, float> { ["apple"] = 1.5f, ["green"] = 0.8f },
            "blue sky" => new Dictionary<string, float> { ["blue"] = 1.5f, ["sky"] = 1f },
            "q" => new Dictionary<string, float> { ["apple"] = 1f, ["red"] = 1f },
            _ => new Dictionary<string, float>(),
        });

        var engine = new SparseTextSearchEngine(provider);
        engine.Index(new[]
        {
            new SearchDocument("1", "red apple",
                new Dictionary<string, string> { ["kind"] = "fruit" }, "food"),
            new SearchDocument("2", "green apple"),
            new SearchDocument("3", "blue sky"),
        });

        return engine;
    }

    [Fact]
    public void Roundtrip_PreservesDocumentsVectorsAndSearchOrder()
    {
        var original = CreateEngine();

        using var stream = new MemoryStream();
        MessagePackSparseIndexPersistence.Save(original, stream);

        stream.Position = 0;
        var restored = MessagePackSparseIndexPersistence.Load(
            stream, new StubSparseProvider(_ => new Dictionary<string, float> { ["apple"] = 1f, ["red"] = 1f }));

        Assert.Equal(original.Count, restored.Count);
        Assert.Equal(original.VocabularySize, restored.VocabularySize);

        foreach (var (beforeEntry, afterEntry) in original.Export().Zip(restored.Export(), (a, b) => (a, b)))
        {
            Assert.Equal(beforeEntry.Document.Id, afterEntry.Document.Id);
            Assert.Equal(beforeEntry.Document.Text, afterEntry.Document.Text);
            Assert.Equal(beforeEntry.Document.Category, afterEntry.Document.Category);
            Assert.Equal(beforeEntry.Weights, afterEntry.Weights);
        }

        // The provider embeds queries on both sides, so the restored order+score equals the
        // original's for the same query.
        var originalRanking = original.Search("q");
        var restoredRanking = restored.Search("q");

        Assert.Equal(
            originalRanking.Select(r => (r.DocumentId, r.Score)),
            restoredRanking.Select(r => (r.DocumentId, r.Score)));
    }

    [Fact]
    public void Roundtrip_EmptyEngine_StaysEmpty()
    {
        var original = new SparseTextSearchEngine(new StubSparseProvider(_ => new Dictionary<string, float>()));

        using var stream = new MemoryStream();
        MessagePackSparseIndexPersistence.Save(original, stream);
        stream.Position = 0;

        var restored = MessagePackSparseIndexPersistence.Load(
            stream, new StubSparseProvider(_ => new Dictionary<string, float>()));

        Assert.Equal(0, restored.Count);
        Assert.Equal(0, restored.VocabularySize);
    }

    [Fact]
    public void Roundtrip_FilePathOverload_WritesAndReadsBack()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.lexisharp-sparse");
        try
        {
            var original = CreateEngine();

            MessagePackSparseIndexPersistence.Save(original, filePath);
            var restored = MessagePackSparseIndexPersistence.Load(
                filePath, new StubSparseProvider(_ => new Dictionary<string, float>()));

            Assert.Equal(
                original.Export().Select(e => e.Weights),
                restored.Export().Select(e => e.Weights));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Roundtrip_DocumentsAreNeverReEmbedded()
    {
        var original = CreateEngine();
        var embeddingCalls = 0;

        var provider = new StubSparseProvider(text =>
        {
            embeddingCalls++;
            return new Dictionary<string, float>();
        });

        using var stream = new MemoryStream();
        MessagePackSparseIndexPersistence.Save(original, stream);
        stream.Position = 0;

        var restored = MessagePackSparseIndexPersistence.Load(stream, provider);

        Assert.Equal(3, restored.Count);
        Assert.Equal(0, embeddingCalls); // corpus restored from stored weights, provider untouched

        restored.Search("anything");
        Assert.Equal(1, embeddingCalls); // only the query used the provider
    }

    [Fact]
    public void Load_RejectsCorruptPayloads()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<InvalidOperationException>(() =>
            MessagePackSparseIndexPersistence.Load(stream, new StubSparseProvider(_ => new Dictionary<string, float>())));
    }

    [Fact]
    public void Validation_Throws()
    {
        var engine = CreateEngine();
        Validate();
        return;

        void Validate()
        {
            Assert.Throws<ArgumentNullException>(() => MessagePackSparseIndexPersistence.Save(null!, new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => MessagePackSparseIndexPersistence.Save(engine, (Stream)null!));
            using var stream = new MemoryStream();
            MessagePackSparseIndexPersistence.Save(engine, stream);
            stream.Position = 0;
            Assert.Throws<ArgumentNullException>(() => MessagePackSparseIndexPersistence.Load(stream, null!));
        }
    }
}