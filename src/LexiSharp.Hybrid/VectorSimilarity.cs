namespace LexiSharp.Hybrid;

/// <summary>
/// Pure vector math helpers used to compare embeddings. These are arithmetic utilities, not
/// an embedding pipeline: vectors must already exist (see <see cref="IEmbeddingProvider"/>).
/// </summary>
public static class VectorSimilarity
{
    /// <summary>
    /// Cosine similarity between two equal-length vectors, in <c>[−1, 1]</c>
    /// (<c>1</c> = same direction). Returns <c>0</c> when either vector is zero.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vectors must have the same length (got {a.Length} and {b.Length}).");

        if (a.Length == 0)
            return 0;

        float dot = 0, normA = 0, normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            float x = a[i];
            float y = b[i];

            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        normA = MathF.Sqrt(normA);
        normB = MathF.Sqrt(normB);

        if (normA == 0 || normB == 0)
            return 0;

        return dot / (normA * normB);
    }
}