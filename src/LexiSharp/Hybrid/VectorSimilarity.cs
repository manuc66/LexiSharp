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
/// <para>
/// <see cref="Mean(IEnumerable{ReadOnlyMemory{float}})"/>, <see cref="Center"/>,
/// <see cref="Normalize"/> and
/// <see cref="CenterAndNormalize"/> are the centreing pair — the subtraction of a shared offset
/// from a corpus, usually before cosine comparison. They exist so a caller does not reimplement
/// them: the arithmetic was previously reachable only through a provider's own channel handling,
/// and a caller wanting it had to copy it.
/// </para>
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
    /// The elementwise mean of a set of embeddings — the offset <see cref="Center"/> subtracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two overloads, because the two input shapes are both real and C# covariance will not apply
    /// an implicit conversion across them: a caller holding this library's own
    /// <see cref="Core.IEmbeddingProvider"/> output has <see cref="ReadOnlyMemory{T}"/>s, and one
    /// loading embeddings from a file or a model has <c>float[]</c> rows. Without the second,
    /// that caller gets a conversion error on <c>IEnumerable{ReadOnlyMemory{float}}</c> suggesting
    /// nothing useful.
    /// </para>
    /// <para>
    /// The only member here that allocates: the mean is a new vector, and it is meant to be
    /// computed once per corpus rather than per query. A caller that already holds a mean — a
    /// provider that centres internally, a model that ships one — never needs this.
    /// </para>
    /// <para>
    /// Every vector must have the same length; the first is taken as the reference. An empty set
    /// throws, since there is no mean of nothing.
    /// </para>
    /// </remarks>
    /// <param name="vectors">The embeddings to average. Enumerated exactly once.</param>
    public static float[] Mean(IEnumerable<ReadOnlyMemory<float>> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        int dimension = 0;
        var sum = Array.Empty<float>();
        int count = 0;

        foreach (ReadOnlyMemory<float> vector in vectors)
        {
            if (count == 0)
            {
                dimension = vector.Length;
                sum = new float[dimension];
            }
            else if (vector.Length != dimension)
            {
                throw new ArgumentException(
                    $"All vectors must have the same length (got {vector.Length} after {dimension}).",
                    nameof(vectors));
            }

            ReadOnlySpan<float> span = vector.Span;

            for (int i = 0; i < dimension; i++)
                sum[i] += span[i];

            count++;
        }

        if (count == 0)
            throw new ArgumentException("The mean of an empty set of vectors is undefined.", nameof(vectors));

        for (int i = 0; i < dimension; i++)
            sum[i] /= count;

        return sum;
    }

    /// <summary>
    /// <c>float[]</c> rows to <c>ReadOnlyMemory</c>s: same arithmetic, one element conversion.
    /// </summary>
    /// <param name="vectors">The embeddings to average. Enumerated exactly once.</param>
    public static float[] Mean(IEnumerable<float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return Mean(vectors.Select(static v => (ReadOnlyMemory<float>)v));
    }

    /// <summary>
    /// Writes <c>vector - mean</c> into <paramref name="destination"/>, elementwise.
    /// </summary>
    /// <remarks>
    /// All three must have the same length, and <paramref name="destination"/> may be the same
    /// span as <paramref name="vector"/> — each element is read before it is written and nothing
    /// reads ahead, so an in-place call is well defined.
    /// </remarks>
    public static void Center(
        ReadOnlySpan<float> vector,
        ReadOnlySpan<float> mean,
        Span<float> destination)
    {
        if (vector.Length != mean.Length || vector.Length != destination.Length)
            throw new ArgumentException(
                $"Vector ({vector.Length}), mean ({mean.Length}) and destination ({destination.Length}) must match.");

        for (int i = 0; i < vector.Length; i++)
            destination[i] = vector[i] - mean[i];
    }

    /// <summary>
    /// Scales <paramref name="vector"/> to unit length into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// A zero vector has no direction, so it is copied through as all zeros rather than becoming
    /// NaN — the convention <see cref="CosineSimilarity"/> already uses, which reports <c>0</c>
    /// for a zero input. In-place with <paramref name="destination"/> is well defined.
    /// </remarks>
    public static void Normalize(ReadOnlySpan<float> vector, Span<float> destination)
    {
        if (vector.Length != destination.Length)
            throw new ArgumentException(
                $"Vector ({vector.Length}) and destination ({destination.Length}) must match.");

        float norm = Norm(vector);

        if (norm == 0)
        {
            vector.CopyTo(destination);
            return;
        }

        float scale = 1 / norm;

        for (int i = 0; i < vector.Length; i++)
            destination[i] = vector[i] * scale;
    }

    /// <summary>
    /// The one call for the usual case: writes <c>(vector - mean) / ||vector - mean||</c> into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are fused so the subtraction is not materialized and read back. The result is
    /// unit length, which <see cref="CosineSimilarity"/> would normalize to anyway — the
    /// difference is that the normalization now happens against the centered vector rather than
    /// the raw one, which is the whole point of the pair.
    /// </para>
    /// <para>
    /// What this does <b>not</b> promise: that centreing improves retrieval on any corpus. It is
    /// the arithmetic of removing a shared offset, and whether that offset was in the way is a
    /// property of the embedding model and the corpus, not of this method.
    /// </para>
    /// </remarks>
    public static void CenterAndNormalize(
        ReadOnlySpan<float> vector,
        ReadOnlySpan<float> mean,
        Span<float> destination)
    {
        if (vector.Length != mean.Length || vector.Length != destination.Length)
            throw new ArgumentException(
                $"Vector ({vector.Length}), mean ({mean.Length}) and destination ({destination.Length}) must match.");

        float normSquared = 0;

        for (int i = 0; i < vector.Length; i++)
        {
            float centered = vector[i] - mean[i];
            destination[i] = centered;
            normSquared += centered * centered;
        }

        if (normSquared == 0)
            return;

        float scale = 1 / MathF.Sqrt(normSquared);

        for (int i = 0; i < destination.Length; i++)
            destination[i] *= scale;
    }

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
