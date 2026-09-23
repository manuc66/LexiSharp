using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Embeddings;

/// <summary>
/// A deterministic, dependency-free embedding provider built on the <b>hashing trick</b>
/// (feature hashing): every token is hashed to one dimension of a fixed-size vector with a
/// signed contribution, and the vector is L2-normalized. Texts that share tokens therefore get
/// a positive cosine similarity, which is enough to exercise — and test — the whole
/// embedding-backed stack (in-memory vector search, MMR, MaxSim, Postgres vector) with no model,
/// no network and no external service.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a semantic model.</b> It captures lexical overlap, not meaning: two
/// paraphrases with disjoint vocabulary stay orthogonal. It exists to make the embedding seams
/// usable out of the box — a baseline, a fixture for tests, and a placeholder the consumer
/// swaps for a real ONNX/HTTP model behind the same <see cref="IEmbeddingProvider"/> /
/// <see cref="ITokenEmbeddingProvider"/> interfaces.
/// </para>
/// <para>
/// The encoding is <b>symmetric</b>: <see cref="EmbeddingUse"/> is accepted but ignored, since a
/// per-role salt would make a query token stop matching the very same document token. The hash
/// is a plain FNV-1a over the token's UTF-16 code units (never <see cref="string.GetHashCode()"/>,
/// which is randomized per process), so the same text maps to the same vector on every run and
/// every machine. Empty or stop-word-only texts yield the zero vector, which every cosine-based
/// consumer treats as "no match".
/// </para>
/// </remarks>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider, ITokenEmbeddingProvider
{
    /// <summary>Default vector length: a compromise between collision rate and footprint.</summary>
    public const int DefaultDimension = 256;

    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private readonly int _dimension;
    private readonly ITokenizer _tokenizer;

    /// <param name="dimension">
    /// Length of every returned vector; must be positive. Defaults to
    /// <see cref="DefaultDimension"/>.
    /// </param>
    /// <param name="tokenizer">
    /// Tokenizer used to split text into terms; defaults to <see cref="Tokenizer.Default"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimension"/> is not positive.</exception>
    public HashingEmbeddingProvider(int dimension = DefaultDimension, ITokenizer? tokenizer = null)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Dimension must be positive.");

        _dimension = dimension;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public int Dimension => _dimension;

    /// <summary>Human readable name of the provider, e.g. <c>"Hashing(256)"</c>.</summary>
    public string Name => $"Hashing({_dimension})";

    /// <inheritdoc />
    /// <remarks>The role is ignored: the encoding is symmetric (see the class remarks).</remarks>
    public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text,
        EmbeddingUse use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.FromResult<ReadOnlyMemory<float>>(Embed(text));
    }

    /// <inheritdoc />
    /// <remarks>One normalized vector per token, in token order — the input of
    /// <see cref="Hybrid.MaxSimReranker"/>.</remarks>
    public IReadOnlyList<ReadOnlyMemory<float>> GetTokenEmbeddings(string text, EmbeddingUse use)
    {
        ArgumentNullException.ThrowIfNull(text);

        var terms = _tokenizer.Tokenize(text);

        if (terms.Count == 0)
            return Array.Empty<ReadOnlyMemory<float>>();

        var vectors = new ReadOnlyMemory<float>[terms.Count];

        for (int i = 0; i < terms.Count; i++)
            vectors[i] = EmbedSingle(terms[i]);

        return vectors;
    }

    /// <summary>Synchronous counterpart of <see cref="GetTextEmbeddingAsync"/>.</summary>
    public ReadOnlyMemory<float> GetEmbedding(string text, EmbeddingUse use = EmbeddingUse.Passage)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Embed(text);
    }

    /// <summary>Hashes every token of the text into one normalized vector.</summary>
    private ReadOnlyMemory<float> Embed(string text)
    {
        var terms = _tokenizer.Tokenize(text);
        var vector = new float[_dimension];

        for (int i = 0; i < terms.Count; i++)
            AddToken(vector, terms[i]);

        Normalize(vector);
        return vector;
    }

    /// <summary>Hashes one token into its own normalized vector (late-interaction building block).</summary>
    private ReadOnlyMemory<float> EmbedSingle(string term)
    {
        var vector = new float[_dimension];
        AddToken(vector, term);
        Normalize(vector);
        return vector;
    }

    /// <summary>Accumulates a signed contribution for one token (sign hashing halves the bias).</summary>
    private void AddToken(float[] vector, string term)
    {
        ulong hash = Fnv1a(term);
        int index = (int)(hash % (ulong)_dimension);
        vector[index] += (hash >> 63) == 0 ? 1f : -1f;
    }

    /// <summary>Scales the vector to unit L2 norm so cosine degenerates to a dot product.</summary>
    private static void Normalize(float[] vector)
    {
        float norm = 0;

        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];

        if (norm == 0)
            return;

        float inverse = 1f / MathF.Sqrt(norm);

        for (int i = 0; i < vector.Length; i++)
            vector[i] *= inverse;
    }

    /// <summary>Deterministic 64-bit FNV-1a over the token's UTF-16 code units (little-endian).</summary>
    private static ulong Fnv1a(string term)
    {
        ulong hash = FnvOffsetBasis;

        foreach (char c in term)
        {
            hash ^= (byte)(c & 0xFF);
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        return hash;
    }
}
