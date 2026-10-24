using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>
/// Serves the already-computed vectors, so a measurement is of the index and the network and never
/// of an ONNX session.
/// </summary>
/// <remarks>
/// Documents and queries are told apart by a caller-supplied lookup rather than by the
/// <see cref="EmbeddingUse"/> role, because the dense lane encodes passages and queries with
/// different prefixes and the cache holds both. A miss throws rather than returning zeros: a zero
/// vector would rank last and quietly read as "the ANN missed it".
/// </remarks>
internal sealed class CachedEmbeddingProvider : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _byText;
    private readonly int _dimension;

    public CachedEmbeddingProvider(
        IReadOnlyList<(string Text, float[] Vector)> documents,
        IReadOnlyList<(string Text, float[] Vector)> queries,
        int dimension)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(queries);

        _dimension = dimension;
        _byText = new Dictionary<string, float[]>(StringComparer.Ordinal);

        for (int i = 0; i < documents.Count; i++)
        {
            // A duplicate document text would collapse two documents onto one vector here. The
            // dense lane's cache has the same property, so the pairing is already the cache's.
            _byText[documents[i].Text] = documents[i].Vector;
        }

        for (int i = 0; i < queries.Count; i++)
        {
            _byText[queries[i].Text] = queries[i].Vector;
        }
    }

    public int Dimension => _dimension;

    public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text, EmbeddingUse use, CancellationToken cancellationToken = default)
    {
        if (_byText.TryGetValue(text, out float[]? vector))
            return Task.FromResult<ReadOnlyMemory<float>>(vector);

        // A miss is thrown rather than answered with zeros: a zero vector ranks last, and a
        // last-ranked placeholder reads exactly like an ANN that failed to retrieve.
        throw new InvalidOperationException(
            $"No cached embedding for {(use == EmbeddingUse.Query ? "a query" : "a passage")}. " +
            "The dense lane's cache and the texts being indexed must come from the same corpus " +
            "in the same order.");
    }
}
