using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Guards the one piece of shared mutable state the term-at-a-time scoring path introduces.
/// </summary>
/// <remarks>
/// The scoring pass rents its buffers from <see cref="ArrayPool{T}"/>, which is a
/// <b>process-wide</b> pool. An array that comes back holds whatever the previous renter wrote
/// into it — a previous search, a previous test, a third-party library in the same process. The
/// recorded-flags array in particular is state, not scratch: a buffer arriving with stale flags
/// makes the accumulation skip recording those ordinals, and the search then quietly returns fewer
/// documents than it owes.
/// <para>
/// The original symptom was not a failing test but an <i>intermittently</i> failing suite: the
/// same three tests failed on some runs of the parallel suite and passed on others, and each
/// passed in isolation. This one is a regression guard rather than a proof — it cannot force the
/// pool to hand back a particular array — but it reproduces the original failure mode closely
/// enough that removing the clear on rent makes it fail, which is the property that matters.
/// </para>
/// </remarks>
public class ScoreAccumulatorTests
{
    /// <summary>
    /// A rented accumulator must not inherit the recorded flags of whatever used the array before.
    /// </summary>
    [Fact]
    public void Rent_IgnoresTheFlagsTheArrayCameBackWith()
    {
        const int Capacity = 512;
        var expected = new double[Capacity];

        for (int attempt = 0; attempt < 200; attempt++)
        {
            // Return a deliberately poisoned buffer to the pool first: a flag array where every
            // byte is 1 is the worst case, and it is exactly what a previous pass leaves behind
            // when it is disposed without being reset.
            var poison = ArrayPool<byte>.Shared.Rent(Capacity);
            Array.Fill(poison, (byte)1);
            ArrayPool<byte>.Shared.Return(poison);

            using var accumulator = ScoreAccumulator.Rent(Capacity);

            for (int ordinal = 0; ordinal < Capacity; ordinal++)
                expected[ordinal] = 0;

            // Record every ordinal exactly once, and once more for a third of them, which is the
            // shape a two-term query over a shared candidate block produces.
            for (int ordinal = 0; ordinal < Capacity; ordinal++)
            {
                accumulator.Record(ordinal, ordinal + 0.5);
                expected[ordinal] = ordinal + 0.5;
            }

            for (int ordinal = 0; ordinal < Capacity; ordinal += 3)
            {
                accumulator.Record(ordinal, 1.25);
                expected[ordinal] += 1.25;
            }

            Assert.Equal(Capacity, accumulator.Count);

            var seen = new bool[Capacity];

            for (int i = 0; i < accumulator.Count; i++)
            {
                int ordinal = accumulator.OrdinalAt(i);
                Assert.False(seen[ordinal]);
                seen[ordinal] = true;
                Assert.Equal(expected[ordinal], accumulator[ordinal]);
            }
        }
    }

    /// <summary>
    /// Concurrent accumulators must stay independent: they share the pool, and one pass resetting
    /// or returning its buffers may not disturb another in flight.
    /// </summary>
    [Fact]
    public void ConcurrentAccumulators_DoNotShareState()
    {
        const int Workers = 8;
        const int Rounds = 300;
        const int Ordinals = 256;

        var failures = new ConcurrentBag<string>();

        Parallel.For(0, Workers, worker =>
        {
            for (int round = 0; round < Rounds; round++)
            {
                // Each worker claims a disjoint block of ordinals and gives every one of them a
                // distinct, recognizable score.
                int offset = worker * Ordinals;

                using var accumulator = ScoreAccumulator.Rent(Workers * Ordinals);

                for (int i = 0; i < Ordinals; i++)
                {
                    accumulator.Record(offset + i, offset + i + 1);
                    accumulator.Record(offset + i, 0.5);
                }

                if (accumulator.Count != Ordinals)
                {
                    failures.Add($"worker {worker} round {round}: count {accumulator.Count}");
                    continue;
                }

                for (int i = 0; i < accumulator.Count; i++)
                {
                    int ordinal = accumulator.OrdinalAt(i);
                    double expected = offset + i + 1.5;

                    if (accumulator[ordinal] != expected)
                        failures.Add($"worker {worker} round {round}: ordinal {ordinal} = {accumulator[ordinal]}, expected {expected}");
                }
            }
        });

        Assert.Empty(failures);
    }

    /// <summary>
    /// A buffer reused after <see cref="ScoreAccumulator.Reset"/> must behave exactly like a fresh
    /// one, which is what makes returning it to the pool safe.
    /// </summary>
    [Fact]
    public void Reset_MakesABufferIndistinguishableFromAFreshOne()
    {
        using var first = ScoreAccumulator.Rent(64);
        for (int ordinal = 0; ordinal < 64; ordinal += 2)
            first.Record(ordinal, 3.5);

        Assert.Equal(32, first.Count);

        first.Reset();
        Assert.Equal(0, first.Count);

        for (int ordinal = 0; ordinal < 64; ordinal += 2)
            first.Record(ordinal, 3.5);

        Assert.Equal(32, first.Count);

        for (int i = 0; i < first.Count; i++)
            Assert.Equal(3.5, first[first.OrdinalAt(i)]);
    }
}
