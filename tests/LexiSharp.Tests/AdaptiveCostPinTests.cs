using System.Text.Json;

using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveCostPinTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Pin_IsCurrentAndTheAdaptiveVariantBeatsStatic()
    {
        string path = Path.Combine(
            RepositoryRoot(),
            "bench/LexiSharp.Eval/reference/adaptive-cost.json");

        AdaptiveCostPin expected = JsonSerializer.Deserialize<AdaptiveCostPin>(
            File.ReadAllText(path),
            JsonOptions)!;

        AdaptiveCostPin actual = AdaptiveCostScenario.Run(
            expected.Events,
            expected.DriftAt,
            expected.AskCost,
            expected.AbstainCost,
            expected.Seed);

        Assert.Equal(expected.OrderHash, actual.OrderHash);
        Assert.Equal(expected.Variants.Count, actual.Variants.Count);

        for (int i = 0; i < expected.Variants.Count; i++)
        {
            AdaptiveCostVariant left = expected.Variants[i];
            AdaptiveCostVariant right = actual.Variants[i];

            Assert.Equal(left.Name, right.Name);
            Assert.Equal(left.Events, right.Events);
            Assert.Equal(left.Accepted, right.Accepted);
            Assert.Equal(left.Asked, right.Asked);
            Assert.Equal(left.Abstained, right.Abstained);
            Assert.Equal(left.Recovered, right.Recovered);
            Assert.Equal(left.Precision, right.Precision, precision: 9);
            Assert.Equal(left.CostPer100, right.CostPer100, precision: 9);
            Assert.Equal(left.RecoveryMean, right.RecoveryMean, precision: 9);
        }

        AdaptiveCostVariant staticVariant = actual.Variants.Single(variant => variant.Name == "static");
        AdaptiveCostVariant adaptiveVariant = actual.Variants.Single(variant => variant.Name == "adaptive");

        Assert.True(adaptiveVariant.Precision > staticVariant.Precision);
        Assert.True(adaptiveVariant.CostPer100 < staticVariant.CostPer100);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
