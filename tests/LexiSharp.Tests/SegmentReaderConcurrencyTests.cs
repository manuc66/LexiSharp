using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A segment read through a source that cannot hand out spans copies each block into a buffer before
/// parsing it, and a memory-mapped segment is the only shipped source that cannot. The reader is
/// shared by every search over the segment, so a buffer owned by the reader is written by two
/// searches at once and one of them decodes the other's bytes — a frequency from a different
/// document, or a block that does not parse.
/// </summary>
/// <remarks>
/// The test makes the interleave a fact rather than a hope: the copy step of both reads meets at a
/// barrier, so both threads are provably inside it together. Without the barrier this is a race that
/// a stress loop would only sometimes catch, and a test that passes on the broken build is no test.
/// </remarks>
public class SegmentReaderConcurrencyTests
{
    [Fact]
    public void TwoThreadsReadingOneSegmentDoNotDecodeEachOthersBlock()
    {
        InMemoryTextIndex built = Build(out int alphaFrequency, out int betaFrequency);
        byte[] bytes = SegmentWriter.Write(built);

        var source = new SideBySideSource(bytes);
        var reader = new SegmentReader(source);

        Assert.True(reader.TryResolveTerm("alpha".AsSpan(), out var alpha));
        Assert.True(reader.TryResolveTerm("beta".AsSpan(), out var beta));

        // Document 0 holds both terms, at frequencies that differ, so a thread that decodes the
        // other term's block cannot accidentally return the value it asked for.
        Assert.NotEqual(alphaFrequency, betaFrequency);

        source.Arm();

        int[] read = [-1, -1];
        Exception?[] failed = [null, null];

        var threads = new[]
        {
            new Thread(() => Read(0, alpha)),
            new Thread(() => Read(1, beta)),
        };

        foreach (Thread thread in threads)
            thread.Start();

        foreach (Thread thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a read did not finish");

        Assert.Null(failed[0]);
        Assert.Null(failed[1]);
        Assert.Equal(alphaFrequency, read[0]);
        Assert.Equal(betaFrequency, read[1]);

        void Read(int slot, SegmentReader.TermRegion region)
        {
            try
            {
                read[slot] = reader.FrequencyAt(region, 0);
            }
            catch (Exception exception)
            {
                failed[slot] = exception;
            }
        }
    }

    /// <summary>
    /// A segment of 2 048 documents whose first posting block is reached by both reads, so neither
    /// walk has to refetch a window mid-test.
    /// </summary>
    private static InMemoryTextIndex Build(out int alphaFrequency, out int betaFrequency)
    {
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 2_048; i++)
        {
            int alphas = i == 0 ? 1 : 3;
            int betas = i == 0 ? 4 : 2;

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                string.Join(' ', Enumerable.Repeat("alpha", alphas))
                + " "
                + string.Join(' ', Enumerable.Repeat("beta", betas))));
        }

        alphaFrequency = 1;
        betaFrequency = 4;

        return index;
    }

    /// <summary>
    /// A source the reader cannot view — which is what a memory-mapped segment is — whose copies meet
    /// at a barrier so both threads are inside the copy step at the same time.
    /// </summary>
    private sealed class SideBySideSource(byte[] bytes) : SegmentSource
    {
        private readonly ArraySegmentSource _inner = new(bytes);
        private readonly Barrier _barrier = new(2);
        private int _armed;
        private int _reads;

        public override int Length => _inner.Length;

        public override int ReadInt32(int offset) => _inner.ReadInt32(offset);

        public override long ReadInt64(int offset) => _inner.ReadInt64(offset);

        public override bool TryView(int offset, int length, out ReadOnlySpan<byte> view)
        {
            view = default;

            return false;
        }

        /// <summary>Starts meeting at the barrier; the reads that resolve terms happen before this.</summary>
        public void Arm()
        {
            Volatile.Write(ref _reads, 0);
            Volatile.Write(ref _armed, 1);
        }

        public override void Read(int offset, int length, byte[] destination)
        {
            bool together = Volatile.Read(ref _armed) != 0
                && Interlocked.Increment(ref _reads) <= 2;

            _inner.Read(offset, length, destination);

            // After the copy, so the second thread's bytes land before either thread decodes: the
            // interleave is the one the buffer makes possible, not a scheduling accident.
            if (together)
                _barrier.SignalAndWait(TimeSpan.FromSeconds(15));
        }
    }
}
