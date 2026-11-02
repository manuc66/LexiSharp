using LexiSharp.Core;
using LexiSharp.Embeddings;
using Xunit;

namespace LexiSharp.Tests;

public class DiskCachedEmbeddingProviderTests
{
    [Fact]
    public async Task ASingleTextIsComputedOnceAndReusedByANewWrapper()
    {
        string directory = NewDirectory();

        try
        {
            var inner = new CountingEmbeddings();
            var first = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");
            var vector = await first.GetTextEmbeddingAsync("invoice", EmbeddingUse.Query);

            Assert.Equal(1, inner.Calls);
            Assert.Equal(3, vector.Length);

            var second = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");
            var cached = await second.GetTextEmbeddingAsync("invoice", EmbeddingUse.Query);

            Assert.Equal(1, inner.Calls);
            Assert.Equal(vector.ToArray(), cached.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheRoleAndTheCacheKeySeparateEntries()
    {
        string directory = NewDirectory();

        try
        {
            var inner = new CountingEmbeddings();
            var provider = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");

            await provider.GetTextEmbeddingAsync("same text", EmbeddingUse.Query);
            await provider.GetTextEmbeddingAsync("same text", EmbeddingUse.Passage);

            Assert.Equal(2, inner.Calls);

            var otherKey = new DiskCachedEmbeddingProvider(inner, directory, "model-v2");
            await otherKey.GetTextEmbeddingAsync("same text", EmbeddingUse.Query);

            Assert.Equal(3, inner.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ABatchCachesEachDistinctText()
    {
        string directory = NewDirectory();

        try
        {
            var inner = new CountingEmbeddings();
            var provider = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");
            string[] texts = { "one", "two", "one" };

            var first = await provider.GetTextEmbeddingsAsync(texts, EmbeddingUse.Passage);

            Assert.Equal(2, inner.Calls);
            Assert.Equal(3, first.Count);

            var second = await provider.GetTextEmbeddingsAsync(texts, EmbeddingUse.Passage);

            Assert.Equal(2, inner.Calls);
            Assert.Equal(first.Select(vector => vector.ToArray()), second.Select(vector => vector.ToArray()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ABatchFallsBackToSingleCallsWhenTheProviderIsNotBatchable()
    {
        string directory = NewDirectory();

        try
        {
            var inner = new SingleEmbeddings();
            var provider = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");

            await provider.GetTextEmbeddingsAsync(new[] { "one", "two" }, EmbeddingUse.Query);
            Assert.Equal(2, inner.Calls);

            await provider.GetTextEmbeddingsAsync(new[] { "one", "two" }, EmbeddingUse.Query);
            Assert.Equal(2, inner.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ACorruptFileIsARecoverableMiss()
    {
        string directory = NewDirectory();

        try
        {
            var inner = new CountingEmbeddings();
            var provider = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");
            await provider.GetTextEmbeddingAsync("invoice", EmbeddingUse.Query);

            string file = Directory.GetFiles(directory, "*.bin", SearchOption.AllDirectories).Single();
            File.WriteAllText(file, "not a vector");

            var vector = await provider.GetTextEmbeddingAsync("invoice", EmbeddingUse.Query);

            Assert.Equal(2, inner.Calls);
            Assert.Equal(3, vector.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AProviderOfAnotherDimensionIsRejected()
    {
        string directory = NewDirectory();

        try
        {
            var inner = new WrongDimensionEmbeddings();
            var provider = new DiskCachedEmbeddingProvider(inner, directory, "model-v1");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.GetTextEmbeddingAsync("text", EmbeddingUse.Query));

            Assert.Contains("Dimension=3", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheConstructorValidatesItsArguments()
    {
        var inner = new CountingEmbeddings();

        Assert.Throws<ArgumentNullException>(() =>
            new DiskCachedEmbeddingProvider(null!, "cache", "key"));
        Assert.Throws<ArgumentException>(() =>
            new DiskCachedEmbeddingProvider(inner, string.Empty, "key"));
        Assert.Throws<ArgumentException>(() =>
            new DiskCachedEmbeddingProvider(inner, "cache", string.Empty));
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lexisharp-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private abstract class Embeddings : IEmbeddingProvider
    {
        public int Dimension => 3;

        public int Calls { get; protected set; }

        public abstract Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default);

        protected static float[] Vector(string text) =>
            new[] { (float)text.Length, 1f, 2f };
    }

    private sealed class CountingEmbeddings : Embeddings, IBatchEmbeddingProvider
    {
        public override Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ReadOnlyMemory<float>>(Vector(text));
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Calls += texts.Count;
            IReadOnlyList<ReadOnlyMemory<float>> vectors = texts
                .Select(text => (ReadOnlyMemory<float>)Vector(text))
                .ToArray();

            return Task.FromResult(vectors);
        }
    }

    private sealed class SingleEmbeddings : Embeddings
    {
        public override Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ReadOnlyMemory<float>>(Vector(text));
        }
    }

    private sealed class WrongDimensionEmbeddings : IEmbeddingProvider
    {
        public int Dimension => 3;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new[] { 1f, 2f });
    }
}
