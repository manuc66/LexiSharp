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
/// <see cref="_stamps"/> exists so «first touch» is a fact rather than an inference from
/// <c>score == 0</c>. Every scorer eligible for this path returns a strictly positive
/// contribution, so the inference would hold today — but a future weight that legitimately
/// contributed <c>0</c> would then record its ordinal twice, hand the top-ranked window the same
/// document twice, and quietly corrupt the page.
/// </para>
/// <para>
/// It carries a <b>generation</b> rather than a flag, and that is what makes renting free: an
/// ordinal is touched when its stamp equals the accumulator's generation, so a pass starts by
/// naming a new generation instead of clearing a corpus-sized array. A sampler put the clearing at
/// 13 % of a query at 50 000 documents — 7 % in <see cref="Rent"/>'s <c>Array.Clear</c> and 6 % in
/// <see cref="Reset"/> — and the end-to-end A/B on a head query that folds 49 943 entries measures
/// <b>561.5 to 502.1 us, 10.6 %</b>, on three launches a side with no overlap. Four bytes an ordinal
/// buys it, against the eight the score array already spends; the generation is unique per rent, so
/// a pooled array arriving with stale stamps cannot be mistaken for a pass in progress.
/// </para>
/// <para>
/// <b>Not thread-safe</b>: one accumulator belongs to one in-flight pass. The engine rents one per
/// search and returns it before the search returns, so concurrent searches never share one.
/// </para>
/// </remarks>
internal sealed class ScoreAccumulator : IDisposable
{
    /// <summary>
    /// The next generation no accumulator has used. Process-wide because a rented array can come
    /// from anywhere in the pool, so a stamp is only meaningful against a value nothing else will
    /// pick while that array is in flight.
    /// </summary>
    private static int _nextGeneration;

    /// <summary>
    /// The pool the stamp arrays come from, <b>private to this class</b> and deliberately not
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <remarks>
    /// A stamp is only meaningful if every value in a stamp array was written by this class as a
    /// generation. The shared pool cannot promise that: <see cref="_ordinals"/> is also an
    /// <c>int[]</c>, so a recycled ordinal list arrives holding document ordinals — 0, 1, 2, … —
    /// and one of them lands on the generation of the pass that just rented it. The ordinal is
    /// then treated as already touched, never recorded, and the search silently returns one document
    /// fewer. That is not a hypothesis: it failed
    /// <c>ScoreAccumulatorTests.Rent_IgnoresTheFlagsTheArrayCameBackWith</c> on the 252nd
    /// sequential rent, with the missing ordinal equal to the round number.
    /// </remarks>
    private static readonly ArrayPool<int> StampPool = ArrayPool<int>.Create();

    private double[] _scores;
    private int[] _stamps;
    private int[] _ordinals;
    private int _generation;
    private int _count;

    private ScoreAccumulator(double[] scores, int[] stamps, int[] ordinals, int generation)
    {
        _scores = scores;
        _stamps = stamps;
        _ordinals = ordinals;
        _generation = generation;
        _ordinals = ordinals;
        _count = 0;
    }

    /// <summary>
    /// Rents an accumulator covering ordinals <c>[0, capacity)</c>.
    /// </summary>
    /// <remarks>
    /// Nothing is cleared here. <c>ArrayPool</c> is a <i>process-wide</i> pool: the array that
    /// comes back is whatever some unrelated code — a previous search, a previous test, a
    /// third-party library — last wrote into, so a flag would have to be cleared before it could
    /// be trusted, which is what made renting cost 7 % of a query at 50 000 documents. A stamp
    /// compared against a generation nothing else has used is trusted without clearing it, which
    /// is why <see cref="Record"/> can no longer drop a document because of state it inherited.
    /// <para>
    /// The scores are deliberately <b>not</b> cleared either: they are overwritten on first touch,
    /// so clearing them would be work the next pass throws away.
    /// </para>
    /// </remarks>
    public static ScoreAccumulator Rent(int capacity)
    {
        if (capacity <= 0)
            capacity = 1;

        int generation = Interlocked.Increment(ref _nextGeneration);
        int[] stamps = StampPool.Rent(capacity);

        // Wrapping takes 2^31 rents and is the one moment a stale stamp could match the current
        // generation, so the array is cleared once — on the pass after the wrap, and never again
        // until the next one.
        if (generation <= 0)
        {
            Array.Clear(stamps);
            generation = Interlocked.Increment(ref _nextGeneration);
        }

        return new ScoreAccumulator(
            ArrayPool<double>.Shared.Rent(capacity),
            stamps,
            ArrayPool<int>.Shared.Rent(capacity),
            generation);
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
    /// The first touch <b>writes</b> rather than adds, and the stamp it tests cannot have been
    /// written by anything else, because the generation it compares against is one no other
    /// accumulator holds.
    /// </remarks>
    public void Record(int ordinal, double contribution)
    {
        if (_stamps[ordinal] != _generation)
        {
            _stamps[ordinal] = _generation;
            _ordinals[_count++] = ordinal;
            _scores[ordinal] = contribution;
            return;
        }

        _scores[ordinal] += contribution;
    }

    /// <summary>
    /// Starts a new pass over the same buffer: the generation moves forward, which makes every
    /// stamp written by the previous pass stale, and the recorded ordinals are forgotten. O(1),
    /// where clearing the touched flags was O(touched).
    /// </summary>
    public void Reset()
    {
        int generation = Interlocked.Increment(ref _nextGeneration);

        if (generation <= 0)
        {
            Array.Clear(_stamps);
            generation = Interlocked.Increment(ref _nextGeneration);
        }

        _generation = generation;
        _count = 0;
    }

    public void Dispose()
    {
        ArrayPool<double>.Shared.Return(_scores);
        StampPool.Return(_stamps);
        ArrayPool<int>.Shared.Return(_ordinals);

        _scores = [];
        _stamps = [];
        _ordinals = [];
        _count = 0;
    }
}
