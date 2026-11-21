using LexiSharp.Core;
using LexiSharp.ParadeDB;
using LexiSharp.Postgres;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="TieBreak.InsertionOrder"/> orders equal scores by the document's ordinal — the position
/// it holds in the index's ordinal space. A SQL table has no ordinal space the engine owns, so the
/// backends cannot honour the option and refuse it rather than returning an id-ordered page under a
/// name that promises insertion order.
/// </summary>
/// <remarks>
/// These need no server: the refusal is a configuration gate, so it runs before a connection is
/// opened. The connection string points at a closed port on purpose — if a guard moves below the
/// connect, the test fails with the connection error instead of passing.
/// </remarks>
public class PostgresTieBreakRefusalTests
{
    private const string Unused = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

    private static readonly SearchOptions Insertion =
        new(Limit: 10, TieBreak: TieBreak.InsertionOrder);

    [Fact]
    public void TextEngine_RefusesInsertionOrder()
    {
        using var engine = new PostgresTextSearchEngine(
            Unused, new PostgresIndexOptions(), autoCreateSchema: false);

        Assert.Throws<NotSupportedException>(() => engine.Search("query", Insertion));
    }

    [Fact]
    public void FuzzyEngine_RefusesInsertionOrder()
    {
        using var engine = new PostgresFuzzySearchEngine(
            Unused, new PostgresFuzzyOptions(), autoCreateSchema: false);

        Assert.Throws<NotSupportedException>(() => engine.Search("query", Insertion));
    }

    [Fact]
    public void SparseEngine_RefusesInsertionOrder()
    {
        using var engine = new PostgresSparseSearchEngine(
            Unused,
            new ConstantSparseProvider(),
            new PostgresSparseOptions { Vocabulary = new Dictionary<string, int> { ["query"] = 0 } },
            autoCreateSchema: false);

        Assert.Throws<NotSupportedException>(() => engine.Search("query", Insertion));
    }

    [Fact]
    public void VectorEngine_RefusesInsertionOrder()
    {
        using var engine = new PostgresVectorSearchEngine(
            Unused,
            new ConstantEmbeddingProvider(),
            new PostgresVectorOptions { Dimension = 2 },
            autoCreateSchema: false);

        Assert.Throws<NotSupportedException>(() => engine.Search("query", Insertion));
    }

    [Fact]
    public void Bm25Engine_RefusesInsertionOrder()
    {
        using var engine = new PostgresBm25SearchEngine(
            Unused, new PostgresBm25Options(), autoCreateSchema: false);

        Assert.Throws<NotSupportedException>(() => engine.Search("query", Insertion));
    }

    [Fact]
    public void ParadeDBEngine_RefusesInsertionOrder()
    {
        using var engine = new ParadeDBTextSearchEngine(
            Unused, new ParadeDBOptions(), autoCreateSchema: false);

        Assert.Throws<NotSupportedException>(() => engine.Search("query", Insertion));
    }

    private sealed class ConstantSparseProvider : ISparseEmbeddingProvider
    {
        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, float>>(
                new Dictionary<string, float> { ["query"] = 1f });
    }

    private sealed class ConstantEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1f, 0f });
    }
}
