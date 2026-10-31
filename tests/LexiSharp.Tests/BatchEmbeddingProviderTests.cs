using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public sealed class BatchEmbeddingProviderTests
{
    private sealed class BatchProvider : IBatchEmbeddingProvider
    {
        public int BatchCalls { get; private set; }

        public int SingleCalls { get; private set; }

        public int LastBatchSize { get; private set; }

        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            SingleCalls++;
            return Task.FromResult(Vector(text));
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            LastBatchSize = texts.Count;

            IReadOnlyList<ReadOnlyMemory<float>> vectors = texts.Select(Vector).ToArray();
            return Task.FromResult(vectors);
        }

        private static ReadOnlyMemory<float> Vector(string text) => new[] { (float)text.Length, 1f };
    }

    private sealed class SingleProvider : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ReadOnlyMemory<float>>(new[] { (float)text.Length, 1f });
        }
    }

    private sealed class SwitchableBatchProvider : IBatchEmbeddingProvider
    {
        public bool Throw { get; set; }

        public List<int> BatchSizes { get; } = [];

        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new[] { (float)text.Length, 1f });

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(texts.Count);

            if (Throw)
                throw new InvalidOperationException("provider failed");

            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                texts.Select(text => (ReadOnlyMemory<float>)new[] { (float)text.Length, 1f }).ToArray());
        }
    }

    private sealed class ShortBatchProvider : IBatchEmbeddingProvider
    {
        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new[] { 1f, 1f });

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                new[] { new ReadOnlyMemory<float>(new[] { 1f, 1f }) });
    }

    [Fact]
    public void Index_BatchesWhenTheProviderSupportsIt()
    {
        var provider = new BatchProvider();
        var engine = new InMemoryVectorSearchEngine(provider);

        engine.Index(
        [
            new SearchDocument("1", "one"),
            new SearchDocument("2", "two"),
            new SearchDocument("3", "three"),
        ]);

        Assert.Equal(1, provider.BatchCalls);
        Assert.Equal(3, provider.LastBatchSize);
        Assert.Equal(0, provider.SingleCalls);
    }

    [Fact]
    public void Index_FallsBackToSingleCalls()
    {
        var provider = new SingleProvider();
        var engine = new InMemoryVectorSearchEngine(provider);

        engine.Index(
        [
            new SearchDocument("1", "one"),
            new SearchDocument("2", "two"),
        ]);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void Index_RejectsAShortBatch()
    {
        var engine = new InMemoryVectorSearchEngine(new ShortBatchProvider());

        Assert.Throws<InvalidOperationException>(
            () => engine.Index(
            [
                new SearchDocument("1", "one"),
                new SearchDocument("2", "two"),
            ]));
    }

    [Fact]
    public void IndexAsync_LeavesThePreviousCorpusIntactWhenEmbeddingFails()
    {
        var provider = new SwitchableBatchProvider();
        var engine = new InMemoryVectorSearchEngine(provider);

        engine.Index([new SearchDocument("old", "old document")]);
        provider.Throw = true;

        Assert.Throws<InvalidOperationException>(
            () => engine.Index([new SearchDocument("new", "new document")]));

        Assert.Contains(engine.Search("old"), result => result.DocumentId == "old");
        Assert.DoesNotContain(engine.Search("new"), result => result.DocumentId == "new");
    }

    [Fact]
    public void IndexAsync_ChunksTheCorpusAtTheConfiguredBatchSize()
    {
        var provider = new SwitchableBatchProvider();
        var engine = new InMemoryVectorSearchEngine(
            provider,
            new InMemoryVectorSearchOptions { EmbeddingBatchSize = 2 });

        engine.Index(
        [
            new SearchDocument("1", "one"),
            new SearchDocument("2", "two"),
            new SearchDocument("3", "three"),
            new SearchDocument("4", "four"),
            new SearchDocument("5", "five"),
        ]);

        Assert.Equal(new[] { 2, 2, 1 }, provider.BatchSizes);
    }

    [Fact]
    public async Task IndexAsync_BatchesAndSearches()
    {
        var provider = new BatchProvider();
        var engine = new InMemoryVectorSearchEngine(provider);

        await engine.IndexAsync(
        [
            new SearchDocument("1", "one"),
            new SearchDocument("2", "two"),
        ]);

        IReadOnlyList<SearchResult> results = engine.Search("one");

        Assert.Equal(1, provider.BatchCalls);
        Assert.Equal(2, results.Count);
    }
}
