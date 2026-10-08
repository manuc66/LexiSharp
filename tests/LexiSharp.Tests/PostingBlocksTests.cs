using System.IO;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The block layout has to be readable back exactly, and the two things a pruned pass needs from it —
/// a bound per block and a way to reach a block without decoding the ones before it — have to hold.
/// </summary>
/// <remarks>
/// The layout has no consumer yet: this is the shape a pruned scoring pass and a memory-mapped
/// segment will both read, so what is tested here is the format itself — every entry, every bound,
/// and every jump.
/// </remarks>
public class PostingBlocksTests
{
    [Fact]
    public void TheRegionRoundTripsEveryEntry()
    {
        foreach (int count in new[] { 0, 1, 2, 127, 128, 129, 1_000, 10_000 })
        {
            var postings = Postings(count, 1_000_000, seed: count + 1);
            int size = PostingBlocks.Measure(postings.Ordinals, postings.Frequencies, postings.Lengths);

            // One byte more than measured, so an overrun lands in the buffer rather than past it.
            var region = new byte[size + 1];
            int written = PostingBlocks.Write(postings.Ordinals, postings.Frequencies, postings.Lengths, region);

            Assert.Equal(size, written);

            var reader = new PostingBlockReader(region.AsSpan(0, written));
            int index = 0;

            while (reader.MoveNext())
            {
                while (reader.TryReadEntry(out int ordinal, out int frequency))
                {
                    Assert.Equal(postings.Ordinals[index], ordinal);
                    Assert.Equal(postings.Frequencies[index], frequency);
                    index++;
                }
            }

            Assert.Equal(count, index);
        }
    }

    [Fact]
    public void EachBlockIsBoundByItsOwnEntries()
    {
        var postings = Postings(1_000, 100_000, seed: 7);
        var region = Region(postings);
        var reader = new PostingBlockReader(region);

        int entries = 0;

        while (reader.MoveNext())
        {
            Assert.Equal(0, reader.FirstOrdinal % PostingBlocks.BlockSpan);
            Assert.Equal(reader.FirstOrdinal / PostingBlocks.BlockSpan, reader.WindowIndex);

            int maxFrequency = 0;
            int minLength = int.MaxValue;
            int last = -1;
            int inBlock = 0;

            while (reader.TryReadEntry(out int ordinal, out int frequency))
            {
                // Every entry of a block is inside the block's window.
                Assert.Equal(reader.WindowIndex, ordinal / PostingBlocks.BlockSpan);
                Assert.True(ordinal > last);
                Assert.True(frequency >= 1);
                Assert.True(postings.Lengths[ordinal] >= 1);

                last = ordinal;
                inBlock++;
                maxFrequency = Math.Max(maxFrequency, frequency);
                minLength = Math.Min(minLength, postings.Lengths[ordinal]);
                entries++;
            }

            Assert.True(inBlock is >= 1 and <= PostingBlocks.BlockSpan);
            Assert.Equal(inBlock, reader.Count);
            Assert.Equal(last, reader.LastOrdinal);
            Assert.Equal(maxFrequency, reader.MaxFrequency);
            Assert.Equal(minLength, reader.MinDocumentLength);
        }

        Assert.Equal(postings.Ordinals.Length, entries);
    }

    [Fact]
    public void AdvanceToLandsWhereALinearScanWould()
    {
        var postings = Postings(5_000, 500_000, seed: 11);
        var region = Region(postings);
        var random = new Random(23);

        for (int probe = 0; probe < 300; probe++)
        {
            int target = random.Next(500_000);
            var reader = new PostingBlockReader(region);

            // The reader lands on a block that can hold the target; walking its entries then reaches
            // the first entry at or after it. A linear scan is what that has to agree with.
            int found = -1;

            if (reader.AdvanceTo(target))
            {
                while (reader.TryReadEntry(out int ordinal, out _))
                {
                    if (ordinal >= target)
                    {
                        found = ordinal;
                        break;
                    }
                }
            }

            int expected = -1;

            for (int i = 0; i < postings.Ordinals.Length; i++)
            {
                if (postings.Ordinals[i] >= target)
                {
                    expected = postings.Ordinals[i];
                    break;
                }
            }

            Assert.Equal(expected, found);
        }
    }

    [Fact]
    public void AdvanceToWalksForwardFromWhereItIs()
    {
        var postings = Postings(2_000, 200_000, seed: 3);
        var region = Region(postings);
        var reader = new PostingBlockReader(region);

        // Reaching a far block, then a nearer one, is a forward-only cursor: the second call must
        // still answer correctly for a target that comes after the first.
        Assert.True(reader.AdvanceTo(150_000));
        Assert.True(reader.AdvanceTo(180_000));

        int ordinal = -1;

        while (reader.TryReadEntry(out ordinal, out _))
        {
            if (ordinal >= 180_000)
                break;
        }

        Assert.True(ordinal >= 180_000);
    }

    [Fact]
    public void ATruncatedRegionIsRejected()
    {
        var postings = Postings(1_000, 100_000, seed: 5);
        var region = Region(postings);

        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new PostingBlockReader(region.AsSpan(0, region.Length - 3));

            while (reader.MoveNext())
            {
            }
        });
    }

    [Fact]
    public void TheLayoutCostsLessThanTheFlatArrays()
    {
        var postings = Postings(10_000, 1_000_000, seed: 17);

        int flat = postings.Ordinals.Length * 2 * sizeof(int);
        int blocks = PostingBlocks.Measure(postings.Ordinals, postings.Frequencies, postings.Lengths);

        Assert.True(blocks < flat, $"the block layout took {blocks} bytes against {flat} for the flat arrays");
    }

    /// <summary>A synthetic posting list: ascending ordinals, small frequencies, per-document lengths.</summary>
    private static (int[] Ordinals, int[] Frequencies, int[] Lengths) Postings(int count, int ordinalSpace, int seed)
    {
        var random = new Random(seed);
        var ordinals = new int[count];
        var frequencies = new int[count];
        var lengths = new int[ordinalSpace];

        for (int i = 0; i < ordinalSpace; i++)
            lengths[i] = 1 + random.Next(400);

        int step = Math.Max(1, ordinalSpace / (count + 1));

        for (int i = 0; i < count; i++)
        {
            ordinals[i] = (i * step) + random.Next(step);
            frequencies[i] = 1 + random.Next(6);
        }

        return (ordinals, frequencies, lengths);
    }

    private static byte[] Region((int[] Ordinals, int[] Frequencies, int[] Lengths) postings)
    {
        var region = new byte[PostingBlocks.Measure(postings.Ordinals, postings.Frequencies, postings.Lengths)];
        int written = PostingBlocks.Write(postings.Ordinals, postings.Frequencies, postings.Lengths, region);

        Assert.Equal(region.Length, written);

        return region;
    }
}
