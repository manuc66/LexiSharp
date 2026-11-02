using System.Security.Cryptography;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Embeddings;

/// <summary>
/// Persists an embedding per distinct text on disk, so a restart does not embed the same corpus
/// again.
/// </summary>
/// <remarks>
/// <para>
/// This is a decorator around a consumer-supplied <see cref="IEmbeddingProvider"/>; it computes
/// nothing. The key includes the caller's <c>cacheKey</c>, the <see cref="EmbeddingUse"/> role and
/// the text, so two models (or a query and a passage of the same text) never share a vector. Change
/// <c>cacheKey</c> when the model, prefix or pooling options change; old files are left in place for
/// the caller to delete.
/// </para>
/// <para>
/// Files are written atomically (a temporary file in the same directory, then a rename), and a
/// corrupt or truncated file is treated as a miss and recomputed. There is no eviction: one file
/// per distinct text and role, under a two-character shard directory. A concurrent pair of misses
/// may compute the same text twice; the last write wins, and both callers get a valid vector.
/// </para>
/// <para>
/// A failed cache write is best-effort: the vector is still returned to the caller, because a full
/// or read-only disk must not make retrieval fail.
/// </para>
/// </remarks>
public sealed class DiskCachedEmbeddingProvider : IEmbeddingProvider, IBatchEmbeddingProvider
{
    private const int Version = 1;

    private static ReadOnlySpan<byte> Magic => "LXEM"u8;

    private readonly IEmbeddingProvider _inner;
    private readonly string _directory;
    private readonly string _cacheKey;

    /// <summary>Creates a disk cache around an embedding provider.</summary>
    /// <param name="inner">The provider that actually computes a miss; never null.</param>
    /// <param name="directory">Directory for the cache files; created on the first write.</param>
    /// <param name="cacheKey">
    /// A stable identity for the model and every option that changes a vector. Change it to
    /// invalidate the cache without deleting the old files.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    /// <exception cref="ArgumentException">The directory or cache key is null or empty.</exception>
    public DiskCachedEmbeddingProvider(
        IEmbeddingProvider inner,
        string directory,
        string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A cache directory is required.", nameof(directory));

        if (string.IsNullOrEmpty(cacheKey))
            throw new ArgumentException("A cache key is required.", nameof(cacheKey));

        _inner = inner;
        _directory = Path.GetFullPath(directory);
        _cacheKey = cacheKey;
    }

    /// <inheritdoc />
    public int Dimension => _inner.Dimension;

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text,
        EmbeddingUse use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        string path = PathFor(text, use);

        if (TryRead(path, out float[] cached))
            return cached;

        ReadOnlyMemory<float> vector = await _inner
            .GetTextEmbeddingAsync(text, use, cancellationToken)
            .ConfigureAwait(false);

        Validate(vector, text);
        Write(path, vector.Span);
        return vector;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
        IReadOnlyList<string> texts,
        EmbeddingUse use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var found = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);
        var misses = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string text in texts)
        {
            ArgumentNullException.ThrowIfNull(text);

            if (!seen.Add(text))
                continue;

            string path = PathFor(text, use);

            if (TryRead(path, out float[] cached))
                found[text] = cached;
            else
                misses.Add(text);
        }

        if (misses.Count > 0)
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors = await EmbedMissesAsync(
                misses,
                use,
                cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < misses.Count; i++)
            {
                Validate(vectors[i], misses[i]);
                Write(PathFor(misses[i], use), vectors[i].Span);
                found[misses[i]] = vectors[i];
            }
        }

        var result = new ReadOnlyMemory<float>[texts.Count];

        for (int i = 0; i < texts.Count; i++)
            result[i] = found[texts[i]];

        return result;
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedMissesAsync(
        IReadOnlyList<string> texts,
        EmbeddingUse use,
        CancellationToken cancellationToken)
    {
        if (_inner is IBatchEmbeddingProvider batch)
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors = await batch
                .GetTextEmbeddingsAsync(texts, use, cancellationToken)
                .ConfigureAwait(false);

            if (vectors.Count != texts.Count)
            {
                throw new InvalidOperationException(
                    $"The provider returned {vectors.Count} embeddings for {texts.Count} texts.");
            }

            return vectors;
        }

        var singles = new ReadOnlyMemory<float>[texts.Count];

        for (int i = 0; i < texts.Count; i++)
            singles[i] = await _inner.GetTextEmbeddingAsync(texts[i], use, cancellationToken).ConfigureAwait(false);

        return singles;
    }

    private string PathFor(string text, EmbeddingUse use)
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(
            _cacheKey + "\n" + (int)use + "\n" + text));

        string hex = Convert.ToHexString(key);
        return Path.Combine(_directory, hex[..2], hex + ".bin");
    }

    private bool TryRead(string path, out float[] vector)
    {
        vector = Array.Empty<float>();

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                return false;

            if (reader.ReadInt32() != Version)
                return false;

            int dimension = reader.ReadInt32();

            if (dimension != Dimension)
                return false;

            var values = new float[dimension];

            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.ReadSingle();

                if (!float.IsFinite(values[i]))
                    return false;
            }

            vector = values;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Write(string path, ReadOnlySpan<float> vector)
    {
        string? directory = Path.GetDirectoryName(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(directory!);

            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(vector.Length);

                foreach (float value in vector)
                    writer.Write(value);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temporary);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    private void Validate(ReadOnlyMemory<float> vector, string text)
    {
        if (vector.Length != Dimension)
        {
            throw new InvalidOperationException(
                $"The provider returned a {vector.Length}-dimension vector for '{text}' " +
                $"but declares Dimension={Dimension}.");
        }

        ReadOnlySpan<float> span = vector.Span;

        for (int i = 0; i < span.Length; i++)
        {
            if (!float.IsFinite(span[i]))
            {
                throw new InvalidOperationException(
                    $"The provider returned a non-finite value ({span[i]}) at index {i} for '{text}'.");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temporary file.
        }
    }
}
