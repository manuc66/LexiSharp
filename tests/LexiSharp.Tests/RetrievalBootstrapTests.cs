using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public class RetrievalBootstrapTests
{
    private static ConfigResult Result(string name, params (string Id, double Ndcg)[] values)
    {
        QueryOutcome[] outcomes = values
            .Select(value => new QueryOutcome(value.Id, Array.Empty<string>(), 0, 1, 0, value.Ndcg))
            .ToArray();

        return new ConfigResult(
            name,
            outcomes.Length,
            outcomes.Average(outcome => outcome.NdcgAt10),
            0,
            0,
            0,
            TimeSpan.Zero,
            outcomes);
    }

    [Fact]
    public void IdenticalConfigurations_HaveZeroDeltaAndPOne()
    {
        ConfigResult first = Result("A", ("q1", 0.2), ("q2", 0.8), ("q3", 0.5));
        ConfigResult second = Result("B", ("q1", 0.2), ("q2", 0.8), ("q3", 0.5));

        IReadOnlyList<BootstrapRow> rows = RetrievalBootstrap.Compare(
            new[] { first, second }, "A", resamples: 500);

        BootstrapRow row = rows.Single(candidate => candidate.Name == "B");
        Assert.Equal(0, row.Delta, 12);
        Assert.Equal(0, row.DeltaLower, 12);
        Assert.Equal(0, row.DeltaUpper, 12);
        Assert.Equal(1.0, row.PValue, 12);
    }

    [Fact]
    public void Bootstrap_BracketsTheMean_AndIsDeterministic()
    {
        ConfigResult first = Result("A", ("q1", 0.1), ("q2", 0.4), ("q3", 0.9), ("q4", 0.3));
        ConfigResult second = Result("B", ("q1", 0.2), ("q2", 0.3), ("q3", 1.0), ("q4", 0.2));

        IReadOnlyList<BootstrapRow> firstRun = RetrievalBootstrap.Compare(
            new[] { first, second }, "A", resamples: 2_000);
        IReadOnlyList<BootstrapRow> secondRun = RetrievalBootstrap.Compare(
            new[] { first, second }, "A", resamples: 2_000);

        Assert.Equal(firstRun.Count, secondRun.Count);

        for (int i = 0; i < firstRun.Count; i++)
        {
            Assert.Equal(firstRun[i].Mean, secondRun[i].Mean);
            Assert.Equal(firstRun[i].Lower, secondRun[i].Lower);
            Assert.Equal(firstRun[i].Upper, secondRun[i].Upper);
            Assert.Equal(firstRun[i].Delta, secondRun[i].Delta);
            Assert.Equal(firstRun[i].DeltaLower, secondRun[i].DeltaLower);
            Assert.Equal(firstRun[i].DeltaUpper, secondRun[i].DeltaUpper);
            Assert.Equal(firstRun[i].PValue, secondRun[i].PValue);
            Assert.True(firstRun[i].Lower <= firstRun[i].Mean);
            Assert.True(firstRun[i].Mean <= firstRun[i].Upper);
        }
    }

    [Fact]
    public void DifferentQuerySets_Throw()
    {
        ConfigResult first = Result("A", ("q1", 0.2), ("q2", 0.8));
        ConfigResult second = Result("B", ("q1", 0.2), ("q3", 0.8));

        Assert.Throws<ArgumentException>(() =>
            RetrievalBootstrap.Compare(new[] { first, second }, "A", resamples: 100));
    }
}
