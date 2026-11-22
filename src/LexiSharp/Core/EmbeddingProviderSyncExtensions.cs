using System.Runtime.CompilerServices;

namespace LexiSharp.Core;

/// <summary>
/// Synchronous adapters over <see cref="IEmbeddingProvider"/> for callers that cannot await.
/// </summary>
public static class EmbeddingProviderSyncExtensions
{
    /// <summary>Embeds a text synchronously.</summary>
    public static ReadOnlyMemory<float> GetTextEmbedding(
        this IEmbeddingProvider provider,
        string text,
        EmbeddingUse use = EmbeddingUse.Query)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(text);

        return provider.GetTextEmbeddingAsync(text, use, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Embeds a batch of texts synchronously.</summary>
    public static IReadOnlyList<ReadOnlyMemory<float>> GetTextEmbeddings(
        this IBatchEmbeddingProvider provider,
        IReadOnlyList<string> texts,
        EmbeddingUse use = EmbeddingUse.Query)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(texts);

        if (provider is IBatchEmbeddingProvider batch)
        {
            return batch.GetTextEmbeddingsAsync(texts, use, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        var results = new ReadOnlyMemory<float>[texts.Count];

        for (int i = 0; i < texts.Count; i++)
            results[i] = provider.GetTextEmbeddingAsync(texts[i], use, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        return results;
    }
}
