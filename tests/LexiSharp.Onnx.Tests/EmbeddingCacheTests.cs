using LexiSharp.Core;
using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class EmbeddingCacheTests
{
    private sealed class FixedProvider : IEmbeddingProvider
    {
        public int Dimension => 2;
        public int Calls;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1f, 2f });
        }
    }

    [Fact]
    public async Task CachesVectorsToDisk()
    {
        var inner = new FixedProvider();
        string path = Path.Combine(Path.GetTempPath(), "lexisharp-cache-" + Guid.NewGuid() + ".json");
        try
        {
            var cache = EmbeddingCache.Open(path, inner, "sig");
            var v1 = await cache.GetTextEmbeddingAsync("hello", EmbeddingUse.Passage);
            var v2 = await cache.GetTextEmbeddingAsync("hello", EmbeddingUse.Passage);
            Assert.Equal(1, inner.Calls);
            Assert.Equal(v1.ToArray(), v2.ToArray());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
