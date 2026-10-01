namespace LexiSharp.Core;

/// <summary>
/// The atomic publish point for the « snapshot swap » concurrency pattern: one thread builds a
/// fresh engine off-lock, then <see cref="Swap"/> makes it visible to every concurrent reader in
/// one atomic step, with none of the reader-side cost of a lock.
/// </summary>
/// <remarks>
/// <para>
/// This is the frequent-writes counterpart of <see cref="SynchronizedTextSearchEngine"/>: the
/// wrapper serializes a writer against readers with a <see cref="System.Threading.ReaderWriterLockSlim"/>,
/// which costs the readers measurable throughput under a writer that never yields; this holder
/// makes the swap itself atomic and leaves every engine untouched during the build. The engines it
/// holds follow the ordinary <see cref="ITextSearchEngine"/> contract — train a new instance
/// off-lock, populate it, publish it, and never write through an instance after it has been
/// swapped out — which is exactly the external synchronization that contract's remarks describe.
/// </para>
/// <para>
/// A reader must capture the engine <b>once per search</b> into a local
/// (<c>var engine = reference.Current;</c>, then search through that local): reading the holder
/// again mid-search could pick up a newer engine, and a search that shared a torn view across two
/// engines would be the failure this type exists to prevent. The volatile read is what makes that
/// single capture safe.
/// </para>
/// <para>
/// No engine is disposed here — neither the initial one nor the one <see cref="Swap"/> displaces,
/// which is returned to the caller: the caller owns every instance it passes in, and nothing in
/// this holder changes that.
/// </para>
/// </remarks>
public sealed class AtomicEngineReference
{
    private ITextSearchEngine _engine;

    /// <param name="engine">The engine initially visible to readers. Not owned by this holder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    public AtomicEngineReference(ITextSearchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <summary>
    /// The currently published engine. Each read is volatile, so a reader that needs one stable
    /// engine per search should read this once into a local and search through that.
    /// </summary>
    public ITextSearchEngine Current => Volatile.Read(ref _engine);

    /// <summary>
    /// Atomically replaces the published engine and returns the previous one, so a writer can
    /// retire the instance readers have stopped seeing. The new engine must be fully constructed
    /// and populated before the call.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    public ITextSearchEngine Swap(ITextSearchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return Interlocked.Exchange(ref _engine, engine);
    }
}