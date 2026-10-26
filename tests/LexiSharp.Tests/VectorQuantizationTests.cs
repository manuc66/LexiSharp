using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The int8 representation of <see cref="InMemoryVectorSearchEngine"/>: that it is opt-in, that its
/// norm is the stored vector's norm, and how much of the float page it keeps on a deterministic
/// fixture.
/// </summary>
/// <remarks>
/// The recall test is a lower bound on a fixture, not a claim about any embedding model: int8
/// quantisation error depends on the component distribution, so the number measured on the cached
/// <c>multilingual-e5-small</c> corpus in <c>docs/embeddings.md</c> is the one to read for a real
/// model. These tests pin the mechanism — the option, the norm, the zero vector, the round trip.
/// </remarks>
public class VectorQuantizationTests
{
    private static readonly InMemoryVectorSearchOptions Quantized = new()
    {
        Quantization = VectorQuantization.Int8,
    };

    [Fact]
    public void TheDefaultOptionsKeepTheFloatRepresentation()
    {
        var engine = new InMemoryVectorSearchEngine(new FixedProvider());

        Assert.Equal(VectorQuantization.None, engine.Options.Quantization);
    }

    [Fact]
    public void TheDefaultEngineExportsTheProviderVectorsExactly()
    {
        var provider = new FixedProvider();
        provider.SetPassage("a", new[] { 0.1f, -0.7f, 0.3f, 0.05f });
        var engine = new InMemoryVectorSearchEngine(provider);

        engine.Add(new SearchDocument("a", "a"));

        // A default-on quantiser would hand back the dequantised vector here, and none of these
        // components is a multiple of the per-vector step.
        Assert.Equal(provider.VectorFor("a"), engine.Export().Single().Vector.ToArray());
    }

    [Fact]
    public void TheQuantizedEngineExportsTheVectorItScans()
    {
        var provider = new FixedProvider();
        provider.SetPassage("a", new[] { 0.1f, -0.7f, 0.3f, 0.05f });
        var engine = new InMemoryVectorSearchEngine(provider, Quantized);

        engine.Add(new SearchDocument("a", "a"));

        float[] exported = engine.Export().Single().Vector.ToArray();
        float[] original = provider.VectorFor("a");

        Assert.Equal(original.Length, exported.Length);
        Assert.NotEqual(original, exported);

        // Every component is within half a quantisation step of the original, so the export is a
        // rounding of the input rather than a different vector.
        float step = original.Max(MathF.Abs) / 127f;

        for (int i = 0; i < original.Length; i++)
            Assert.True(MathF.Abs(original[i] - exported[i]) <= (step / 2) + 1e-6f);
    }

    [Fact]
    public void ExactlyRepresentableVectorsReturnTheSamePageAsTheFloatEngine()
    {
        // Each document's largest component is ±127 and every component is an integer, so the
        // per-vector scale is exactly 1 and the stored int8 components are the input components:
        // the only difference between the two engines is the order of the float accumulation.
        var documents = new List<SearchDocument>();
        var provider = new FixedProvider();

        for (int d = 0; d < 12; d++)
        {
            var vector = new float[32];

            for (int i = 0; i < vector.Length; i++)
                vector[i] = ((d * 37) + (i * 11)) % 255 - 127;

            vector[(d * 7) % vector.Length] = d % 2 == 0 ? 127 : -127;

            string id = $"doc-{d}";
            documents.Add(new SearchDocument(id, id));
            provider.SetPassage(id, vector);
        }

        var queries = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var random = new Random(7);

        for (int q = 0; q < 6; q++)
        {
            var vector = new float[32];

            for (int i = 0; i < vector.Length; i++)
                vector[i] = (float)((random.NextDouble() * 2) - 1);

            string text = $"query-{q}";
            queries[text] = vector;
            provider.SetQuery(text, vector);
        }

        var exact = new InMemoryVectorSearchEngine(provider);
        var quantized = new InMemoryVectorSearchEngine(provider, Quantized);

        foreach (SearchDocument document in documents)
        {
            exact.Add(document);
            quantized.Add(document);
        }

        foreach (var (text, _) in queries)
        {
            var exactPage = exact.Search(text, new SearchOptions(Limit: 5));
            var quantizedPage = quantized.Search(text, new SearchOptions(Limit: 5));

            Assert.Equal(
                exactPage.Select(result => result.DocumentId),
                quantizedPage.Select(result => result.DocumentId));

            for (int i = 0; i < exactPage.Count; i++)
                Assert.Equal(exactPage[i].Score, quantizedPage[i].Score, 5);
        }
    }

    [Fact]
    public void TheQuantizedCosineAgainstItsOwnStoredVectorIsOne()
    {
        // The norm decision, seen from outside: query the engine with the vector it stores. If the
        // scan divided by the *float* vector's norm, this cosine would be below 1 by the
        // quantisation error — the score would be that of a vector the engine does not hold.
        var provider = new FixedProvider();
        var engine = new InMemoryVectorSearchEngine(provider, Quantized);

        provider.SetPassage("a", new[] { 3f, -2f, 0.5f, 0.25f, -3f, 1f });
        engine.Add(new SearchDocument("a", "a"));

        float[] stored = engine.Export().Single().Vector.ToArray();
        provider.SetQuery("a-like", stored);

        var results = engine.Search("a-like", new SearchOptions(Limit: 1));

        Assert.Single(results);
        Assert.Equal(1.0, results[0].Score, 6);
        Assert.True(results[0].Score <= 1.0 + 1e-6);
    }

    [Fact]
    public void AZeroVectorStillNeverMatches()
    {
        var provider = new FixedProvider();
        var engine = new InMemoryVectorSearchEngine(provider, Quantized);

        provider.SetPassage("empty", new float[6]);
        provider.SetPassage("real", new[] { 1f, 0f, 0f, 0f, 0f, 0f });

        engine.Add(new SearchDocument("empty", "empty"));
        engine.Add(new SearchDocument("real", "real"));

        provider.SetQuery("q", new[] { 1f, 0f, 0f, 0f, 0f, 0f });

        var results = engine.Search("q", new SearchOptions(Limit: 10));

        Assert.Equal(new[] { "real" }, results.Select(result => result.DocumentId));
    }

    [Fact]
    public void ImportingAnExportReRanksIdentically()
    {
        var provider = new FixedProvider();
        var engine = new InMemoryVectorSearchEngine(provider, Quantized);

        for (int d = 0; d < 20; d++)
        {
            string id = $"doc-{d}";
            var vector = new float[24];

            for (int i = 0; i < vector.Length; i++)
                vector[i] = MathF.Sin((d * 31) + (i * 7)) * 4f;

            provider.SetPassage(id, vector);
            engine.Add(new SearchDocument(id, id));
        }

        provider.SetQuery("q", Enumerable.Range(0, 24).Select(i => MathF.Cos(i * 3f)).ToArray());

        var before = engine.Search("q", new SearchOptions(Limit: 10));

        // Export hands out the dequantised rows, so re-importing them and quantising again must
        // land on the same components: a second rounding of an already-rounded row is a no-op.
        var reloaded = new InMemoryVectorSearchEngine(provider, Quantized);
        reloaded.Import(engine.Export());

        var after = reloaded.Search("q", new SearchOptions(Limit: 10));

        Assert.Equal(
            before.Select(result => result.DocumentId),
            after.Select(result => result.DocumentId));

        for (int i = 0; i < before.Count; i++)
            Assert.Equal(before[i].Score, after[i].Score, 5);
    }

    [Fact]
    public void AReusedSlotDoesNotKeepTheRemovedVectorsScale()
    {
        var provider = new FixedProvider();
        var engine = new InMemoryVectorSearchEngine(provider, Quantized);

        provider.SetPassage("huge", Enumerable.Repeat(1000f, 8).ToArray());
        provider.SetPassage("small", new[] { 0.01f, 0.02f, -0.03f, 0.04f, 0f, 0.01f, -0.02f, 0.03f });

        engine.Add(new SearchDocument("huge", "huge"));
        engine.Add(new SearchDocument("small", "small"));

        engine.Remove("huge");

        // The vacated slot is reused on the next add, so the new document's scale and components
        // have to be written over the old ones rather than left beside them.
        provider.SetPassage("replacement", new[] { 0.2f, -0.1f, 0.3f, 0f, 0.1f, -0.2f, 0f, 0.05f });
        engine.Add(new SearchDocument("replacement", "replacement"));

        provider.SetQuery("q", new[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f });

        var results = engine.Search("q", new SearchOptions(Limit: 10));

        Assert.Equal(new[] { "replacement", "small" }, results.Select(result => result.DocumentId));

        float[] stored = engine.Export().Single(entry => entry.Document.Id == "replacement").Vector.ToArray();
        Assert.Equal(provider.VectorFor("replacement").Length, stored.Length);
        Assert.True(MathF.Abs(stored[0] - 0.2f) < 0.01f);
    }

    [Fact]
    public void AFilteredQuantizedSearchStillAppliesTheFilter()
    {
        var provider = new FixedProvider();
        var engine = new InMemoryVectorSearchEngine(provider, Quantized);

        foreach (string kind in new[] { "a", "b" })
        {
            foreach (int d in new[] { 0, 1 })
            {
                string id = $"{kind}-{d}";

                provider.SetPassage(id, new[] { 1f, (float)d, 0.5f, 0f });
                engine.Add(new SearchDocument(
                    id,
                    id,
                    new Dictionary<string, string> { ["kind"] = kind }));
            }
        }

        provider.SetQuery("q", new[] { 1f, 0f, 0f, 0f });

        var results = engine.Search("q", new SearchOptions(
            Limit: 10,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "b") }));

        Assert.Equal(new[] { "b-0", "b-1" }, results.Select(result => result.DocumentId));
    }

    [Fact]
    public void AVectorWidthThatIsNotAMultipleOfTheSimdWidthKeepsTheFloatPage()
    {
        // 37 components: one full SIMD chunk and a five-element tail, which is where a widening
        // kernel that assumed the width would drop or double-count components.
        var provider = new FixedProvider();
        var exact = new InMemoryVectorSearchEngine(provider);
        var quantized = new InMemoryVectorSearchEngine(provider, Quantized);
        var random = new Random(37);

        for (int d = 0; d < 16; d++)
        {
            var vector = new float[37];

            for (int i = 0; i < vector.Length; i++)
                vector[i] = random.Next(-127, 128);

            vector[d] = 127;

            string id = $"doc-{d}";
            provider.SetPassage(id, vector);
            exact.Add(new SearchDocument(id, id));
            quantized.Add(new SearchDocument(id, id));
        }

        for (int q = 0; q < 5; q++)
        {
            string text = $"query-{q}";
            var vector = new float[37];

            for (int i = 0; i < vector.Length; i++)
                vector[i] = (float)((random.NextDouble() * 2) - 1);

            provider.SetQuery(text, vector);

            Assert.Equal(
                exact.Search(text, new SearchOptions(Limit: 8)).Select(result => result.DocumentId),
                quantized.Search(text, new SearchOptions(Limit: 8)).Select(result => result.DocumentId));
        }
    }

    [Fact]
    public void RandomVectorsKeepAtLeastNinetyFivePercentOfTheFloatTopTen()
    {
        // A 64-dimension Gaussian fixture, 400 documents and 25 queries, all deterministic. The
        // bound is below the value this fixture measures (reported in the test's failure message
        // by construction: the assertion names both) so it pins the mechanism rather than a score.
        const int dimension = 64;
        const int documentCount = 400;
        const int queryCount = 25;
        const int k = 10;

        var random = new Random(20261026);
        var provider = new FixedProvider();

        for (int d = 0; d < documentCount; d++)
        {
            string id = $"doc-{d}";
            provider.SetPassage(id, Gaussian(random, dimension));
        }

        for (int q = 0; q < queryCount; q++)
            provider.SetQuery($"query-{q}", Gaussian(random, dimension));

        var exact = new InMemoryVectorSearchEngine(provider);
        var quantized = new InMemoryVectorSearchEngine(provider, Quantized);

        // AddVector is reached through Import, which takes the vectors the provider already
        // produced for the passages: no re-embedding, and the two engines see the same rows.
        exact.Import(Enumerable.Range(0, documentCount)
            .Select(d => new VectorIndexEntry(new SearchDocument($"doc-{d}", $"doc-{d}"), provider.VectorFor($"doc-{d}"))));

        quantized.Import(Enumerable.Range(0, documentCount)
            .Select(d => new VectorIndexEntry(new SearchDocument($"doc-{d}", $"doc-{d}"), provider.VectorFor($"doc-{d}"))));

        int overlap = 0;

        for (int q = 0; q < queryCount; q++)
        {
            HashSet<string> exactPage = exact
                .Search($"query-{q}", new SearchOptions(Limit: k))
                .Select(result => result.DocumentId)
                .ToHashSet(StringComparer.Ordinal);

            overlap += quantized
                .Search($"query-{q}", new SearchOptions(Limit: k))
                .Count(result => exactPage.Contains(result.DocumentId));
        }

        double recall = overlap / (double)(queryCount * k);

        Assert.True(recall >= 0.95, $"recall@{k} was {recall:0.0000} on {documentCount} documents of {dimension} dimensions");
    }

    private static float[] Gaussian(Random random, int dimension)
    {
        var vector = new float[dimension];

        for (int i = 0; i < dimension; i++)
        {
            // Box-Muller, one of the pair per element: the fixture is about the distribution, not
            // about saving a square root.
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            vector[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return vector;
    }

    /// <summary>An <see cref="IEmbeddingProvider"/> serving vectors set by the test.</summary>
    private sealed class FixedProvider : IEmbeddingProvider
    {
        private readonly Dictionary<string, float[]> _passages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float[]> _queries = new(StringComparer.Ordinal);

        public int Dimension { get; private set; } = 4;

        public void SetPassage(string text, float[] vector)
        {
            _passages[text] = vector;
            Dimension = vector.Length;
        }

        public void SetQuery(string text, float[] vector) => _queries[text] = vector;

        public float[] VectorFor(string text) => _passages[text];

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, float[]> source = use == EmbeddingUse.Query ? _queries : _passages;

            if (!source.TryGetValue(text, out float[]? vector))
                throw new InvalidOperationException($"No {use} vector for '{text}'.");

            return Task.FromResult<ReadOnlyMemory<float>>(vector);
        }
    }
}
