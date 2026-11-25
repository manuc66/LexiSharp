using LexiSharp.Adaptive;
using Xunit;

namespace LexiSharp.Tests;

public class FeedbackMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static FeedbackMemory<string> Memory(FeedbackMemoryOptions? options = null) =>
        new(
            new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30)),
            options ?? new FeedbackMemoryOptions(),
            ContextCodecs.Raw<string>());

    private static FeedbackEvent<string> Event(
        FeedbackOutcome outcome,
        string? offered,
        string? accepted,
        string context = "ctx",
        DateTimeOffset? at = null) =>
        new(
            "text",
            offered,
            accepted,
            context,
            at ?? Now,
            outcome);

    [Fact]
    public void Accepted_RaisesTheAcceptanceShare()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));
        memory.Observe(Event(FeedbackOutcome.Accepted, "B", "B"));

        ContextPrior prior = memory.Prior("ctx", Now);
        CategoryPrior a = prior.Categories.Single(category => category.Category == "A");

        Assert.Equal(1.0, a.AcceptanceMass, precision: 10);
        Assert.Equal(0.5, a.AcceptanceShare, precision: 10);
        Assert.Equal(2.0 / 12.0, prior.Evidence, precision: 10);
        Assert.Equal(1.0, prior.ResponseRate, precision: 10);
    }

    [Fact]
    public void Corrected_CreditsTheAcceptedAndPenalizesTheOffered()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Corrected, "A", "B"));

        ContextPrior prior = memory.Prior("ctx", Now);

        Assert.Equal(1.0, prior.Categories.Single(category => category.Category == "B").AcceptanceMass, precision: 10);
        Assert.Equal(1.0, prior.Categories.Single(category => category.Category == "A").RefusalMass, precision: 10);
    }

    [Fact]
    public void Rejected_AddsRefusalOnly()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Rejected, "A", null));

        ContextPrior prior = memory.Prior("ctx", Now);

        Assert.Equal(1.0, prior.Categories.Single().RefusalMass, precision: 10);
        Assert.Equal(0.0, prior.Categories.Single().AcceptanceMass, precision: 10);
    }

    [Fact]
    public void Ignored_AddsNoMassButCountsAsExposure()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Ignored, "A", null));

        ContextPrior prior = memory.Prior("ctx", Now);

        Assert.Empty(prior.Categories);
        Assert.Equal(0.0, prior.ResponseRate, precision: 10);
        Assert.Equal(0, prior.Evidence, precision: 10);
    }

    [Fact]
    public void Accepted_ClearsOneRefusal()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Rejected, "A", null));
        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));

        CategoryPrior a = memory.Prior("ctx", Now).Categories.Single(category => category.Category == "A");

        Assert.Equal(0.0, a.RefusalMass, precision: 10);
        Assert.Equal(1.0, a.AcceptanceMass, precision: 10);
    }

    [Fact]
    public void Veto_AppearsAfterTheThresholdAndDecays()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Rejected, "A", null));
        memory.Observe(Event(FeedbackOutcome.Rejected, "A", null));
        memory.Observe(Event(FeedbackOutcome.Rejected, "A", null));

        Assert.Contains("A", memory.Vetoed("ctx", Now));

        Assert.DoesNotContain("A", memory.Vetoed("ctx", Now.AddDays(10)));
    }

    [Fact]
    public void Ignored_NeverVetoes()
    {
        var memory = Memory();

        for (int i = 0; i < 10; i++)
            memory.Observe(Event(FeedbackOutcome.Ignored, "A", null));

        Assert.Empty(memory.Vetoed("ctx", Now));
    }

    [Fact]
    public void Mass_HalvesAtTheHalfLife()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));
        memory.Observe(Event(FeedbackOutcome.Accepted, "B", "B"));

        CategoryPrior a = memory.Prior("ctx", Now.AddDays(10)).Categories.Single(category => category.Category == "A");
        CategoryPrior b = memory.Prior("ctx", Now.AddDays(10)).Categories.Single(category => category.Category == "B");

        Assert.Equal(0.5, a.AcceptanceMass, precision: 10);
        Assert.Equal(0.5, b.AcceptanceMass, precision: 10);
    }

    [Fact]
    public void OutOfOrderObservation_IsRejected()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A", at: Now.AddSeconds(-1))));
    }

    [Fact]
    public void InconsistentOutcomes_AreRejected()
    {
        var memory = Memory();

        Assert.Throws<ArgumentException>(() => memory.Observe(Event(FeedbackOutcome.Accepted, "A", "B")));
        Assert.Throws<ArgumentException>(() => memory.Observe(Event(FeedbackOutcome.Corrected, "A", "A")));
        Assert.Throws<ArgumentException>(() => memory.Observe(Event(FeedbackOutcome.Rejected, "A", "A")));
        Assert.Throws<ArgumentException>(() => memory.Observe(Event(FeedbackOutcome.Ignored, "A", "A")));
        Assert.Throws<ArgumentException>(() => memory.Observe(Event(FeedbackOutcome.Abstained, "A", "A")));
    }

    [Fact]
    public void ContextCap_EvictsTheLeastRecentlyUsed()
    {
        var memory = Memory(new FeedbackMemoryOptions { MaxContexts = 2 });

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A", context: "first"));
        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A", context: "second"));
        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A", context: "third"));

        Assert.Equal(2, memory.ContextCount);
        Assert.Empty(memory.Prior("first", Now).Categories);
        Assert.NotEmpty(memory.Prior("second", Now).Categories);
        Assert.NotEmpty(memory.Prior("third", Now).Categories);
    }

    [Fact]
    public void Purge_RemovesContextsOlderThanMaxAge()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));
        memory.Purge(Now.AddDays(31));

        Assert.Equal(0, memory.ContextCount);
    }

    [Fact]
    public void Prune_RemovesCategoriesOutsideTheLabelSet()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));
        memory.Observe(Event(FeedbackOutcome.Accepted, "B", "B"));
        memory.Prune(new HashSet<string>(["A"], StringComparer.Ordinal));

        ContextPrior prior = memory.Prior("ctx", Now);

        Assert.Equal(["A"], prior.Categories.Select(category => category.Category));
    }

    [Fact]
    public void HashedCodec_IsStableAndOpaque()
    {
        IContextCodec<string> codec = ContextCodecs.Hashed<string>();

        string first = codec.Encode("user@example.com");
        string second = codec.Encode("user@example.com");

        Assert.Equal(first, second);
        Assert.NotEqual("user@example.com", first);
        Assert.True(codec.IsOpaque);
        Assert.Contains("System.String", codec.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentReaders_SeeTheSameProjection()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));
        memory.Observe(Event(FeedbackOutcome.Accepted, "B", "B"));

        double[] shares = new double[64];

        Parallel.For(0, shares.Length, i =>
            shares[i] = memory.Prior("ctx", Now).Categories.Single(category => category.Category == "A").AcceptanceShare);

        Assert.All(shares, share => Assert.Equal(shares[0], share, precision: 12));
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        var memory = Memory();

        memory.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));
        memory.Observe(Event(FeedbackOutcome.Corrected, "A", "B"));

        FeedbackSnapshot snapshot = memory.Save(Now);
        FeedbackMemory<string> loaded = FeedbackMemory<string>.Load(snapshot, ContextCodecs.Raw<string>());

        Assert.Equal(memory.Prior("ctx", Now).Categories.Count, loaded.Prior("ctx", Now).Categories.Count);
        Assert.Equal(
            memory.Prior("ctx", Now).Categories.Select(category => category.Category),
            loaded.Prior("ctx", Now).Categories.Select(category => category.Category));
        Assert.Equal(
            memory.Prior("ctx", Now).Categories.Select(category => category.AcceptanceMass),
            loaded.Prior("ctx", Now).Categories.Select(category => category.AcceptanceMass));
    }
}
