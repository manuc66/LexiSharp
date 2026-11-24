using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LexiSharp.Core;

namespace LexiSharp.Onnx;

/// <summary>
/// Persists text embeddings on disk, keyed by a hash of the text and role, one file per vector
/// space (provider, model, tokenizer version). It wraps an <see cref="IEmbeddingProvider"/> and
/// computes missing vectors through it.
/// </summary>
/// <remarks>
/// <para>
/// The cache is reconstructible: deleting the file only forces the next run to embed the learned
/// examples again. Writes are atomic (temporary file then rename) and throttled. A process-wide
/// registry ensures one cache per (path, signature).
/// </para>
/// <para>
/// This is the disk-backed counterpart of the in-memory <see cref="OnnxEmbeddingCache"/>. Use it
/// when embeddings must survive process restarts without re-running the model.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("JSON serialization of the embedding cache requires unreferenced code.")]
[System.Diagnostics.CodeAnalysis.RequiresDynamicCode("JSON serialization of the embedding cache requires dynamic code.")]
public class PersistentEmbeddingCache : IEmbeddingProvider, IDisposable
{
    private static readonly ConcurrentDictionary<string, PersistentEmbeddingCache> CacheInstances =
        new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, float[]> _vectors;
    private readonly IEmbeddingProvider _inner;
    private readonly string _path;
    private int _pending;
    private bool _disposed;

    /// <summary>Opens the process-wide cache for a path, wrapping the provider.</summary>
    /// <param name="path">Cache file path. Blank disables the cache and returns <paramref name="inner"/>.</param>
    /// <param name="inner">Provider that computes missing vectors.</param>
    /// <param name="signature">
    /// Identity of the vector space (provider, model, tokenizer version). Two providers that share
    /// a cache file must not share keys, or a vector computed by one is served to the other.
    /// </param>
    public static IEmbeddingProvider Open(string? path, IEmbeddingProvider inner, string? signature = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (string.IsNullOrWhiteSpace(path))
        {
            return inner;
        }

        string fullPath = AdjustPath(path, signature);
        return CacheInstances.GetOrAdd(fullPath, key => new PersistentEmbeddingCache(key, inner));
    }

    /// <summary>Creates a cache around <paramref name="inner"/> for the given path.</summary>
    public PersistentEmbeddingCache(string? path, IEmbeddingProvider inner, string? signature = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Cache path must not be blank.", nameof(path));
        }

        _inner = inner;
        _path = AdjustPath(path, signature);
        _vectors = Load(_path);
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

        string key = Hash(text, use);
        lock (_gate)
        {
            if (_vectors.TryGetValue(key, out float[]? cached))
            {
                return new ReadOnlyMemory<float>(cached);
            }
        }

        ReadOnlyMemory<float> vector = await _inner.GetTextEmbeddingAsync(text, use, cancellationToken).ConfigureAwait(false);

        float[] stored = vector.IsEmpty ? Array.Empty<float>() : vector.ToArray();
        lock (_gate)
        {
            if (_disposed)
            {
                return new ReadOnlyMemory<float>(stored);
            }

            _vectors[key] = stored;
            _pending++;
            if (_pending >= 32)
            {
                SaveLocked();
            }
        }

        return new ReadOnlyMemory<float>(stored);
    }

    /// <summary>Flushes pending vectors to disk.</summary>
    public void Save()
    {
        lock (_gate)
        {
            SaveLocked();
        }
    }

    /// <summary>
    /// Flushes the vectors, forgets this cache in the process-wide registry and releases the
    /// wrapped provider.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            SaveLocked();
            _disposed = true;
        }

        CacheInstances.TryRemove(new KeyValuePair<string, PersistentEmbeddingCache>(_path, this));
        (_inner as IDisposable)?.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("JSON serialization of the embedding cache requires unreferenced code.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("JSON serialization of the embedding cache requires dynamic code.")]
    private void SaveLocked()
    {
        if (_disposed && _pending == 0)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_vectors, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            _pending = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache failure must never break ranking.
        }
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("JSON deserialization of the embedding cache requires unreferenced code.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("JSON deserialization of the embedding cache requires dynamic code.")]
    private static Dictionary<string, float[]> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, float[]>(StringComparer.Ordinal);
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, float[]>>(json, JsonOptions)
                ?? new Dictionary<string, float[]>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, float[]>(StringComparer.Ordinal);
        }
    }

    private static string AdjustPath(string path, string? signature)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.IsNullOrEmpty(signature))
        {
            string tag = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..8];
            fullPath = fullPath + "." + tag;
        }

        return fullPath;
    }

    private static string Hash(string text, EmbeddingUse use)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text + "\u0000" + use));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>
/// Backwards-friendly alias for <see cref="PersistentEmbeddingCache"/>, matching the name used by
/// WindowTracker's original implementation.
/// </summary>
[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("JSON serialization of the embedding cache requires unreferenced code.")]
[System.Diagnostics.CodeAnalysis.RequiresDynamicCode("JSON serialization of the embedding cache requires dynamic code.")]
public sealed class EmbeddingCache : PersistentEmbeddingCache
{
    /// <summary>Creates a cache around <paramref name="inner"/> for the given path.</summary>
    public EmbeddingCache(string? path, IEmbeddingProvider inner, string? signature = null)
        : base(path, inner, signature)
    {
    }

    /// <summary>Opens the process-wide cache for a path, wrapping the provider.</summary>
    public new static IEmbeddingProvider Open(string? path, IEmbeddingProvider inner, string? signature = null) =>
        PersistentEmbeddingCache.Open(path, inner, signature);
}
