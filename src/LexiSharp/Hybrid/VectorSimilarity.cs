using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LexiSharp.Hybrid;

/// <summary>
/// Pure vector math helpers used to compare embeddings. These are arithmetic utilities, not
/// an embedding pipeline: vectors must already exist (see <see cref="Core.IEmbeddingProvider"/>).
/// </summary>
/// <remarks>
/// The kernels here are <see cref="Vector{T}"/>-based, so they run on the widest registers the
/// host offers (AVX2/AVX-512 where available) and degrade to the scalar path on hardware without
/// them. Two shapes are exposed because a full scan over a corpus rarely needs both halves of a
/// cosine: <see cref="DotProduct"/> and <see cref="Norm"/> are the two halves, and a caller that
/// can cache a document's norm across queries (see
/// <see cref="Indexing.InMemoryVectorSearchEngine"/>) then pays for the cheaper one only.
/// </remarks>
public static class VectorSimilarity
{
    /// <summary>Elements per vector step. Two chains of this width hide the FMA latency.</summary>
    private const int Unroll = 2;

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

        // Three fused passes rather than one loop holding three accumulator sets: at any realistic
        // embedding width (hundreds of floats, a few KiB) the vectors are already in L1, so the
        // extra loads are cheaper than the register pressure of a fused kernel, which spills on
        // AVX2's 16 registers.
        float dot = DotProduct(a, b);
        float normA = MathF.Sqrt(NormSquared(a));
        float normB = MathF.Sqrt(NormSquared(b));

        if (normA == 0 || normB == 0)
            return 0;

        return dot / (normA * normB);
    }

    /// <summary>The dot product of two equal-length vectors.</summary>
    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vectors must have the same length (got {a.Length} and {b.Length}).");

        return DotProductUnchecked(a, b);
    }

    /// <summary>The Euclidean norm of a vector.</summary>
    public static float Norm(ReadOnlySpan<float> vector) => MathF.Sqrt(NormSquared(vector));

    /// <summary>The squared Euclidean norm of a vector — the quantity actually accumulated.</summary>
    public static float NormSquared(ReadOnlySpan<float> vector) => DotProductUnchecked(vector, vector);

    /// <summary>
    /// Multiplies two equal-length spans elementwise and accumulates, vectorized with
    /// <see cref="Unroll"/> independent chains so the FMA latency is not the loop's critical path.
    /// Lengths are assumed equal: every public entry point has already checked.
    /// </summary>
    private static float DotProductUnchecked(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int width = Vector<float>.Count;
        int step = width * Unroll;
        int i = 0;

        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);

        Vector<float> sum0 = Vector<float>.Zero;
        Vector<float> sum1 = Vector<float>.Zero;

        for (; i <= a.Length - step; i += step)
        {
            sum0 += Vector.LoadUnsafe(ref Unsafe.Add(ref ra, i))
                  * Vector.LoadUnsafe(ref Unsafe.Add(ref rb, i));
            sum1 += Vector.LoadUnsafe(ref Unsafe.Add(ref ra, i + width))
                  * Vector.LoadUnsafe(ref Unsafe.Add(ref rb, i + width));
        }

        for (; i <= a.Length - width; i += width)
            sum0 += Vector.LoadUnsafe(ref Unsafe.Add(ref ra, i)) * Vector.LoadUnsafe(ref Unsafe.Add(ref rb, i));

        float sum = Vector.Sum(sum0 + sum1);

        for (; i < a.Length; i++)
            sum += a[i] * b[i];

        return sum;
    }
}
