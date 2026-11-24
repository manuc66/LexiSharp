using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class NewComponentsPropertiesTests
{
    [PropertyAttribute]
    public Property TemporalPrior_ProbabilitiesSumToOne()
    {
        var gen = from day in Gen.Elements(Enum.GetValues<DayOfWeek>())
                  from hour in Gen.Choose(0, 23)
                  select (day, hour);

        return Prop.ForAll(Arb.From(gen), pair =>
        {
            var history = new[]
            {
                new ObservedAssignment(pair.day, pair.hour, "A"),
                new ObservedAssignment(pair.day, pair.hour, "A"),
                new ObservedAssignment(pair.day, pair.hour, "A"),
                new ObservedAssignment(pair.day, pair.hour, "B"),
            };
            var prior = new TemporalPrior(history, blockHours: 3, minSupport: 1);
            var probs = prior.Probabilities(pair.day, pair.hour);
            if (probs.Count == 0)
            {
                return true;
            }

            double sum = probs.Values.Sum();
            return Math.Abs(sum - 1.0) < 1e-9;
        });
    }

    [PropertyAttribute]
    public Property EntityExtractor_NoOverlappingSpans()
    {
        var gen = Gen.Elements("http://a.com", "http://b.com", "plain text", "a/b/c.txt");

        return Prop.ForAll(Arb.From(gen), text =>
        {
            var extractor = new EntityExtractor(new[] { GenericEntityPatterns.Url, GenericEntityPatterns.FilePath, GenericEntityPatterns.Domain });
            var result = extractor.Extract(text);
            for (int i = 0; i < result.Entities.Count; i++)
            {
                for (int j = i + 1; j < result.Entities.Count; j++)
                {
                    var a = result.Entities[i];
                    var b = result.Entities[j];
                    bool overlap = a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;
                    if (overlap)
                    {
                        return false;
                    }
                }
            }

            return true;
        });
    }

    [PropertyAttribute]
    public Property SignificanceTests_PValueInRange()
    {
        var gen = from n in Gen.Choose(1, 20)
                  from seed in Gen.Choose(0, 1000)
                  select (n, seed);

        return Prop.ForAll(Arb.From(gen), pair =>
        {
            var rng = new Random(pair.seed);
            var a = Enumerable.Range(0, pair.n).Select(_ => rng.NextDouble()).ToArray();
            var b = Enumerable.Range(0, pair.n).Select(_ => rng.NextDouble()).ToArray();
            double p = LexiSharp.Core.SignificanceTests.WilcoxonSignedRank(a, b);
            return p >= 0.0 && p <= 1.0;
        });
    }
}
