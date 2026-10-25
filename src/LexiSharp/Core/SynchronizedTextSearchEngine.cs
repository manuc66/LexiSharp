namespace LexiSharp.Core;

/// <summary>
/// Wraps an <see cref="ITextSearchEngine"/> so that concurrent searches and concurrent mutation
/// stop being the caller's problem: any number of <see cref="Search(string, SearchOptions?, CancellationToken)"/> (and
/// the capability calls below) may run at the same time, while <see cref="Index"/>,
/// <see cref="Add"/>, <see cref="Remove"/> and <see cref="Clear"/> each take exclusive access and
/// wait for the searches already in flight to return.
/// </summary>
/// <remarks>
/// <para>
/// This is opt-in rather than baked into the engines, for the same reason
/// <see cref="Classification.SynchronizedTextClassifier"/> is: the common shape — one writer, with
/// readers either single-threaded or pointed at a whole instance that is being swapped — keeps
/// paying nothing and keeps being free to do what a lock would forbid, such as training a fresh
/// engine off-lock and publishing it (<see cref="AtomicEngineReference"/> is the reader-side-free
/// swap for exactly that shape). A caller that wants overlapping mutation and search wraps its
/// engine in this rather than hand-rolling the same lock.
/// </para>
/// <para>
/// Searches take the <b>read</b> lock, so concurrent searches still overlap — the wrapper
/// serializes writers against readers, not readers against each other. The mutating members take
/// the write lock. This is a <see cref="ReaderWriterLockSlim"/> with
/// <see cref="LockRecursionPolicy.NoRecursion"/>, so a mistake that re-enters fails loudly instead
/// of deadlocking; nothing here re-enters, because the locked calls go straight to the wrapped
/// engine and never back through a member of this one.
/// </para>
/// <para>
/// The capability surfaces forward too, and the forwarding is exact: the wrapper only serializes
/// access, so <see cref="IFacetedSearchEngine"/>, <see cref="IDetailedSearchEngine"/> and
/// <see cref="IExplainableSearchEngine"/> return the wrapped engine's own results, unchanged, just
/// ordered against the same lock. A wrapped engine without a capability throws
/// <see cref="NotSupportedException"/> naming the wrapped type, per call.
/// </para>
/// <para>
/// The read lock is not free in principle, and the measured cost on the stock engine is reported
/// in the remarks of <see cref="Search(ReadOnlySpan{char}, SearchOptions?, CancellationToken)"/> — measured against
/// the unwrapped engine in the same session, not claimed, and reproducible with the concurrency lane
/// (<c>dotnet run --project bench/LexiSharp.Benchmarks -c Release -- concurrency --runs 5</c>, documented
/// in <c>docs/benchmarks.md</c>). A caller who needs the last few percent
/// of single-threaded latency should keep the engine unwrapped rather than expect a number.
/// </para>
/// <para>
/// Dispose releases the lock. It does not dispose the wrapped engine, which the caller still owns.
/// </para>
/// </remarks>
public sealed class SynchronizedTextSearchEngine
    : IFacetedSearchEngine, IDetailedSearchEngine, IExplainableSearchEngine, IDisposable
{
    private readonly ITextSearchEngine _inner;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <param name="inner">The engine to synchronize access to. Not owned by this wrapper.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public SynchronizedTextSearchEngine(ITextSearchEngine inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        _lock.EnterWriteLock();

        try
        {
            _inner.Index(documents);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        _lock.EnterWriteLock();

        try
        {
            _inner.Add(document);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        _lock.EnterWriteLock();

        try
        {
            return _inner.Remove(documentId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _lock.EnterWriteLock();

        try
        {
            _inner.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Search(query.AsSpan(), options, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The read side of the lock, shared by every search and capability call, so concurrent
    /// searches still overlap. <b>Measured</b> against the unwrapped engine in the same session by
    /// the concurrency lane: one 100,000-document Zipf corpus, a 1:1:1 mix of a one-term tail query, a
    /// three-term tail query and a head query, five interleaved runs per caller count. Throughput was
    /// at parity — 1.006x at one caller, 1.017x at eight — and every per-run throughput range over 1,
    /// 2, 4 and 8 callers included 1.0. The mix p50 ratio was 0.990x at one caller, 1.035x at two,
    /// 1.014x at four and 1.004x at eight; the two-caller range (1.013x–1.061x) is the only one that
    /// excludes 1.0, and the one-caller reading does not reproduce it, so no fixed cost is
    /// established. On the median, the read pair costs at most a few percent of a 4–10 µs tail call;
    /// one four-caller p99 run reached 1.126x, so that is not a bound on the tail. Do not extrapolate
    /// to a contended lock or to another engine: it is one host, one corpus, a read-only workload,
    /// and the lock's share of a call is bounded by the call's own work — the head query in that mix
    /// costs about a millisecond.
    /// <para>
    /// Under write contention the cost stops being nothing, and the concurrency lane does not cover
    /// it: one writer toggling <c>Add</c>/<c>Remove</c> as fast as it can while the same eight readers
    /// searched, the readers' combined throughput fell to <b>0.15x</b> the uncontended rate over a
    /// 3-second window, with no reader starved to zero (the least-loaded one still completed 455
    /// searches). That figure comes from the session that added this type; no command in this tree
    /// reproduces it, so read it as a direction rather than a reproducible number.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        _lock.EnterReadLock();

        try
        {
            return _inner.Search(query, options, cancellationToken);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IFacetedSearchEngine"/>.</exception>
    public FacetedSearchResult SearchWithFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchWithFacets(query.AsSpan(), options, facetFields);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IFacetedSearchEngine"/>.</exception>
    public FacetedSearchResult SearchWithFacets(
        ReadOnlySpan<char> query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        var faceted = Faceted();

        _lock.EnterReadLock();

        try
        {
            return faceted.SearchWithFacets(query, options, facetFields);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IDetailedSearchEngine"/>.</exception>
    public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        var detailed = Detailed();

        _lock.EnterReadLock();

        try
        {
            return detailed.SearchWithDetails(query, options, cancellationToken);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IExplainableSearchEngine"/>.</exception>
    public ScoreExplanation? Explain(string documentId, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Explain(documentId, query.AsSpan());
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IExplainableSearchEngine"/>.</exception>
    public ScoreExplanation? Explain(string documentId, ReadOnlySpan<char> query)
    {
        var explainable = Explainable();

        _lock.EnterReadLock();

        try
        {
            return explainable.Explain(documentId, query);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private IFacetedSearchEngine Faceted() =>
        _inner as IFacetedSearchEngine
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IFacetedSearchEngine)}.");

    private IDetailedSearchEngine Detailed() =>
        _inner as IDetailedSearchEngine
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IDetailedSearchEngine)}.");

    private IExplainableSearchEngine Explainable() =>
        _inner as IExplainableSearchEngine
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IExplainableSearchEngine)}.");

    /// <summary>Releases the internal lock. The wrapped engine is not disposed.</summary>
    public void Dispose() => _lock.Dispose();
}