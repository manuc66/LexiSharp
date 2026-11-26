using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class PreferenceRerankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, SearchDocument> Documents = new(StringComparer.Ordinal)
    {
        ["d1"] = new SearchDocument("d1", "alpha"),
        ["d2"] = new SearchDocument("d2", "beta"),
    };

    private static SearchDocument? Resolve(string id) =>
        Documents.TryGetValue(id, out SearchDocument? document) ? document : null;

    private static PreferenceEvent Event() =>
        new(
            "alpha",
            "ctx",
            [
                new ClassificationResult("d1", 0.5),
                new ClassificationResult("d2", 0.5),
            ],
            "d1",
            Now);

    private static SearchResult[] Candidates() =>
    [
        new SearchResult("d2", 1.0, new SearchDocument("d2", "beta")),
        new SearchResult("d3", 0.9, new SearchDocument("d3", "alpha")),
    ];

    [Fact]
    public void WithoutTraining_RerankIsTheIdentity()
    {
        var reranker = new PreferenceReranker();
        SearchResult[] candidates = Candidates();

        IReadOnlyList<SearchResult> result = reranker.Rerank("alpha", candidates);

        Assert.Equal(candidates.Select(candidate => candidate.DocumentId), result.Select(candidate => candidate.DocumentId));
        Assert.Equal(candidates.Select(candidate => candidate.Score), result.Select(candidate => candidate.Score));
    }

    [Fact]
    public void Training_GeneralizesToUnseenDocuments()
    {
        var reranker = new PreferenceReranker(Resolve, new DocumentOnlySource());

        reranker.Train(Enumerable.Range(0, 30).Select(_ => Event()));

        IReadOnlyList<SearchResult> result = reranker.Rerank("alpha", Candidates());

        Assert.Equal("d3", result[0].DocumentId);
    }

    [Fact]
    public void Rerank_DoesNotMutateTheInputOrIntroduceDocuments()
    {
        var reranker = new PreferenceReranker(Resolve, new DocumentOnlySource());
        reranker.Train(Enumerable.Range(0, 10).Select(_ => Event()));

        SearchResult[] candidates = Candidates();
        double[] scores = candidates.Select(candidate => candidate.Score).ToArray();

        IReadOnlyList<SearchResult> result = reranker.Rerank("alpha", candidates);

        Assert.All(result, candidate => Assert.Contains(candidate.DocumentId, candidates.Select(item => item.DocumentId)));
        Assert.Equal(scores, candidates.Select(candidate => candidate.Score));
        Assert.Equal("Preference", reranker.Name);
    }

    [Fact]
    public void Snapshot_RoundTripsThroughLXPR()
    {
        var reranker = new PreferenceReranker(Resolve, new DocumentOnlySource());
        reranker.Train(Enumerable.Range(0, 20).Select(_ => Event()));

        PreferenceSnapshot snapshot = reranker.Save(Now);

        using var stream = new MemoryStream();
        PreferenceSnapshotSerializer.Save(snapshot, stream);
        stream.Position = 0;

        PreferenceSnapshot loadedSnapshot = PreferenceSnapshotSerializer.Load(stream);
        var loaded = PreferenceReranker.Load(loadedSnapshot, Resolve, new DocumentOnlySource());

        Assert.Equal(
            reranker.Rerank("alpha", Candidates()).Select(candidate => candidate.DocumentId),
            loaded.Rerank("alpha", Candidates()).Select(candidate => candidate.DocumentId));
    }

    [Fact]
    public void Context_DisambiguatesTheSameCandidateSet()
    {
        var reranker = new PreferenceReranker(Resolve);
        var events = new List<PreferenceEvent>();

        for (int i = 0; i < 30; i++)
        {
            events.Add(new PreferenceEvent(
                "query",
                "A",
                [new ClassificationResult("d1", 0.5), new ClassificationResult("d2", 0.5)],
                "d1",
                Now));

            events.Add(new PreferenceEvent(
                "query",
                "B",
                [new ClassificationResult("d1", 0.5), new ClassificationResult("d2", 0.5)],
                "d2",
                Now));
        }

        reranker.Train(events);

        SearchResult[] candidates =
        [
            new SearchResult("d1", 1.0, new SearchDocument("d1", "alpha")),
            new SearchResult("d2", 0.9, new SearchDocument("d2", "beta")),
        ];

        Assert.Equal("d1", reranker.Rerank("query", "A", candidates)[0].DocumentId);
        Assert.Equal("d2", reranker.Rerank("query", "B", candidates)[0].DocumentId);
    }

    [Fact]
    public void Observe_UpdatesTheOnlineModel()
    {
        var reranker = new PreferenceReranker(Resolve, new DocumentOnlySource());

        for (int i = 0; i < 10; i++)
        {
            reranker.Observe(Event());
        }

        Assert.Equal("d3", reranker.Rerank("alpha", Candidates())[0].DocumentId);
    }

    [Fact]
    public void HashedSnapshot_RoundTrips()
    {
        var options = new PreferenceOptions { HashFeatures = true };
        var reranker = new PreferenceReranker(Resolve, new DocumentOnlySource(), options);
        reranker.Train(Enumerable.Range(0, 20).Select(_ => Event()));

        PreferenceSnapshot snapshot = reranker.Save(Now);

        using var stream = new MemoryStream();
        PreferenceSnapshotSerializer.Save(snapshot, stream);
        stream.Position = 0;

        PreferenceSnapshot loadedSnapshot = PreferenceSnapshotSerializer.Load(stream);
        var loaded = PreferenceReranker.Load(loadedSnapshot, Resolve, new DocumentOnlySource());

        Assert.True(loadedSnapshot.Hashed);
        Assert.Empty(loadedSnapshot.Features);
        Assert.Equal(
            reranker.Rerank("alpha", Candidates()).Select(candidate => candidate.DocumentId),
            loaded.Rerank("alpha", Candidates()).Select(candidate => candidate.DocumentId));
    }

    [Fact]
    public void MalformedEvents_AreRejected()
    {
        var reranker = new PreferenceReranker(Resolve, new DocumentOnlySource());

        Assert.Throws<ArgumentException>(() => reranker.Train(
        [
            new PreferenceEvent(
                "alpha",
                "ctx",
                [new ClassificationResult("d1", 0.5), new ClassificationResult("d1", 0.5)],
                "d1",
                Now),
        ]));

        Assert.Throws<ArgumentException>(() => reranker.Train(
        [
            new PreferenceEvent(
                "alpha",
                "ctx",
                [new ClassificationResult("d1", 0.5), new ClassificationResult("d2", 0.5)],
                "d3",
                Now),
        ]));

        Assert.Throws<ArgumentOutOfRangeException>(() => reranker.Train(
        [
            new PreferenceEvent(
                "alpha",
                "ctx",
                [new ClassificationResult("d1", 0.5), new ClassificationResult("d2", 0.5)],
                "d1",
                Now,
                Propensity: 2),
        ]));
    }

    [Fact]
    public void Load_MissingFeature_IsInvalidData()
    {
        PreferenceOptions options = PreferenceOptions.Default;
        var categories = new List<PreferenceCategorySnapshot>
        {
            new("*", [new PreferenceWeightSnapshot("missing", -1, 1.0)]),
        };
        string signature = PreferenceLearner.SignatureOf(options, false, ["known"], categories);
        var snapshot = new PreferenceSnapshot(1, options, false, ["known"], categories, signature);

        Assert.Throws<InvalidDataException>(() => PreferenceReranker.Load(snapshot));
    }

    private sealed class DocumentOnlySource : IFeatureSource<RerankItem>
    {
        public IFeatureSet FeaturesOf(RerankItem item)
        {
            var features = new HashSet<string>(StringComparer.Ordinal);

            foreach (string token in item.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                features.Add("q:" + token);
            }

            foreach (string token in item.DocumentText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                features.Add("d:" + token);
            }

            return new FeatureSet(features);
        }
    }
}
