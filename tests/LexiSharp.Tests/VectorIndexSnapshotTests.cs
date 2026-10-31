using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public sealed class VectorIndexSnapshotTests
{
    private sealed class SignedProvider : IEmbeddingProvider, IEmbeddingSignatureProvider
    {
        public SignedProvider(string? signature) => EmbeddingSignature = signature;

        public string? EmbeddingSignature { get; }

        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new[] { 1f, 0f });
    }

    private static InMemoryVectorSearchEngine Engine(string? signature)
    {
        var engine = new InMemoryVectorSearchEngine(new SignedProvider(signature));
        engine.Index([new SearchDocument("1", "one")]);
        return engine;
    }

    [Fact]
    public void ExportSnapshot_CarriesTheSignature()
    {
        var engine = Engine("model-a");

        VectorIndexSnapshot snapshot = engine.ExportSnapshot();

        Assert.Equal("model-a", snapshot.EmbeddingSignature);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public void ImportSnapshot_RejectsADifferentSignature()
    {
        var source = Engine("model-a");
        var target = Engine("model-b");
        VectorIndexSnapshot snapshot = source.ExportSnapshot();

        Assert.Throws<InvalidOperationException>(() => target.ImportSnapshot(snapshot));
    }

    [Fact]
    public void ImportSnapshot_AllowsAMismatchWhenAsked()
    {
        var source = Engine("model-a");
        var target = Engine("model-b");

        target.ImportSnapshot(source.ExportSnapshot(), allowSignatureMismatch: true);

        Assert.Equal(1, target.Count);
    }

    [Fact]
    public void ImportSnapshot_AcceptsASnapshotWithoutASignature()
    {
        var source = Engine(null);
        var target = Engine("model-b");

        target.ImportSnapshot(source.ExportSnapshot());

        Assert.Equal(1, target.Count);
    }

    [Fact]
    public void ExportSnapshot_IsNullWhenTheProviderCannotIdentifyItself()
    {
        var engine = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider());

        Assert.Null(engine.EmbeddingSignature);
        Assert.Null(engine.ExportSnapshot().EmbeddingSignature);
    }
}
