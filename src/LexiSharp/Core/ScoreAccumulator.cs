using System.Buffers;

namespace LexiSharp.Core;

/// <summary>
/// Ordinal-indexed score buffer for one term-at-a-time scoring pass: the accumulator every
/// posting walk folds into, plus the list of ordinals it touched.
/// </summary>
/// <remarks>
/// <para>
/// Three parallel arrays, all rented from <see cref="ArrayPool{T}"/> so a steady stream of
/// searches allocates nothing after the first few. They are sized to the index's ordinal space
/// rather than to the candidate count, which is what makes the pass a single walk of the inverted
/// lists with no union step in front of it: the cost is proportional to the posting entries the
/// query terms actually have, in both the rare-term and the head-term regime.
/// </para>
/// <para>
/// <see cref="_recorded"/> exists so «first touch» is a flag rather than an inference from
/// <c>score == 0</c>. Every scorer eligible for this path returns a strictly positive
/// contribution, so the inference would hold today — but a future weight that legitimately
/// contributed <c>0</c> would then record its ordinal twice, hand the top-ranked window the same
/// document twice, and quietly corrupt the page. A byte per ordinal in an array that stays in L1
/// is not worth trading that for.
/// </para>
/// <para>
/// <b>Not thread-safe</b>: one accumulator belongs to one in-flight pass. The engine rents one per
/// search and returns it before the search returns, so concurrent searches never share one.
/// </para>
/// </remarks>
internal sealed class ScoreAccumulator : IDisposable
{
    private double[] _scores;
    private byte[] _recorded;
    private int[] _ordinals;
    private int _count;

    private ScoreAccumulator(double[] scores, byte[] recorded, int[] ordinals)
    {
        _scores = scores;
        _recorded = recorded;
        _ordinals = ordinals;
        _count = 0;
    }

    /// <summary>
    /// Rents an accumulator covering ordinals <c>[0, capacity)</c>.
    /// </summary>
    /// <remarks>
    /// The recorded-flags array is cleared here, and this is not optional bookkeeping.
    /// <c>ArrayPool</c> is a <i>process-wide</i> pool: the array that comes back is whatever some
    /// unrelated code — a previous search, a previous test, a third-party library — last wrote
    /// into. A flag array arriving with stale <c>1</c>s makes <see cref="Record"/> take its
    /// «already seen this ordinal» branch, skip the recording, and drop the document out of the
    /// results. That is a search that silently returns 46 documents where it owes 64, on some runs
    /// and not others, which is a far worse failure than a slower one.
    /// <para>
    /// The scores are deliberately <b>not</b> cleared: they are overwritten on first touch, so
    /// clearing them would be work the next pass throws away.
    /// </para>
    /// </remarks>
    public static ScoreAccumulator Rent(int capacity)
    {
        if (capacity <= 0)
            capacity = 1;

        byte[] recorded = ArrayPool<byte>.Shared.Rent(capacity);
        Array.Clear(recorded);

        return new ScoreAccumulator(
            ArrayPool<double>.Shared.Rent(capacity),
            recorded,
            ArrayPool<int>.Shared.Rent(capacity));
    }

    /// <summary>Number of distinct ordinals recorded so far.</summary>
    public int Count => _count;

    /// <summary>The score accumulated for <paramref name="ordinal"/>.</summary>
    public double this[int ordinal] => _scores[ordinal];

    /// <summary>The <paramref name="position"/>-th recorded ordinal, in first-touch order.</summary>
    public int OrdinalAt(int position) => _ordinals[position];

    /// <summary>
    /// Folds one term's contribution into the document at <paramref name="ordinal"/>, recording
    /// the ordinal the first time any term reaches it.
    /// </summary>
    /// <remarks>
    /// The first touch <b>writes</b> rather than adds, and the flags it tests are known-clear
    /// because <see cref="Rent"/> cleared them: nothing here reads state the buffer happened to
    /// arrive with.
    /// </remarks>
    public void Record(int ordinal, double contribution)
    {
        if (_recorded[ordinal] == 0)
        {
            _recorded[ordinal] = 1;
            _ordinals[_count++] = ordinal;
            _scores[ordinal] = contribution;
            return;
        }

        _scores[ordinal] += contribution;
    }

    /// <summary>
    /// Returns the buffer to a state a later pass can reuse: only the recorded flags are state,
    /// and clearing exactly those is cheaper than clearing the whole buffer.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _count; i++)
            _recorded[_ordinals[i]] = 0;

        _count = 0;
    }

    public void Dispose()
    {
        ArrayPool<double>.Shared.Return(_scores);
        ArrayPool<byte>.Shared.Return(_recorded);
        ArrayPool<int>.Shared.Return(_ordinals);

        _scores = [];
        _recorded = [];
        _ordinals = [];
        _count = 0;
    }
}
