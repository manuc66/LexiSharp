using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>
/// A bounded, thread-safe memory of acceptances and refusals per context, decaying with a
/// <see cref="RecencyPolicy"/> and shrunk towards a global acceptance prior.
/// </summary>
/// <remarks>
/// <para>
/// The memory stores aggregates, never text: a context is an opaque key produced by an
/// <see cref="IContextCodec{TContext}"/> (hashed by default), and the masses are two numbers per
/// context and category. It is a feedback prior, not a second classifier.
/// </para>
/// <para>
/// <b>Time.</b> Every method that reads a mass takes the instant it is evaluated at. The stored
/// masses sit at their last update; a read projects them forward by the policy's half-life. Reads
/// are therefore pure and concurrent; only <see cref="Observe"/>, <see cref="Purge"/>,
/// <see cref="Prune"/> and <see cref="Rebase"/> take the writer lock.
/// </para>
/// <para>
/// <b>Outcomes.</b> <see cref="FeedbackOutcome.Accepted"/> credits the accepted category and
/// removes one unit of refusal for it; <see cref="FeedbackOutcome.Corrected"/> credits the accepted
/// category and adds one refusal to the offered one; <see cref="FeedbackOutcome.Rejected"/> adds a
/// refusal; <see cref="FeedbackOutcome.Abstained"/> credits the accepted category when one was
/// chosen; <see cref="FeedbackOutcome.Ignored"/> only counts as an exposure and is never a refusal.
/// </para>
/// <para>
/// <b>Bounds.</b> Contexts are capped and evicted least-recently-used; categories per context and
/// category entries overall are capped too. <see cref="Purge"/> drops contexts older than the
/// policy's maximum age.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The caller's context key: a sender, a repository, a folder, a request.</typeparam>
public sealed class FeedbackMemory<TContext> : IDisposable
    where TContext : notnull
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly IContextCodec<TContext> _codec;
    private readonly FeedbackMemoryOptions _options;
    private readonly Dictionary<string, ContextState> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly GlobalState _global = new();

    private RecencyPolicy _policy;
    private DateTimeOffset? _lastObservedAt;
    private long _pairCount;
    private bool _disposed;

    /// <summary>Creates an empty memory.</summary>
    /// <param name="policy">The decay and expiry policy.</param>
    /// <param name="options">The bounds and smoothing; null uses <see cref="FeedbackMemoryOptions.Default"/>.</param>
    /// <param name="codec">The context codec; null uses the hashed codec for <typeparamref name="TContext"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public FeedbackMemory(
        RecencyPolicy policy,
        FeedbackMemoryOptions? options = null,
        IContextCodec<TContext>? codec = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        options ??= FeedbackMemoryOptions.Default;
        ValidateOptions(options);

        _policy = policy;
        _options = options;
        _codec = codec ?? ContextCodecs.Hashed<TContext>();
    }

    /// <summary>The decay policy currently applied.</summary>
    public RecencyPolicy Policy => Volatile.Read(ref _policy);

    /// <summary>How many contexts the memory holds.</summary>
    public int ContextCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return _contexts.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Records one interaction. This is the only writer of the memory's masses.</summary>
    /// <param name="feedback">The feedback to record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="feedback"/> is null.</exception>
    /// <exception cref="ArgumentException">The outcome and categories are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The instant runs backwards, or a propensity is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public void Observe(FeedbackEvent<TContext> feedback)
    {
        ValidateFeedback(feedback);

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (_lastObservedAt is { } last && feedback.At < last)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(feedback),
                    feedback.At,
                    "Feedback instants must be non-decreasing within one memory.");
            }

            _lastObservedAt = feedback.At;

            string key = _codec.Encode(feedback.Context);

            if (!_contexts.TryGetValue(key, out ContextState? state))
            {
                if (_contexts.Count >= _options.MaxContexts)
                {
                    EvictLeastRecentlyUsed();
                }

                state = new ContextState(feedback.At);
                _contexts.Add(key, state);
                _lruNodes.Add(key, _lru.AddLast(key));
            }
            else
            {
                Touch(key);
            }

            Materialize(state, feedback.At);
            MaterializeGlobal(feedback.At);

            state.Observations++;

            if (feedback.OfferedCategory is not null)
            {
                state.Exposures++;
            }

            if (feedback.Outcome is FeedbackOutcome.Accepted
                or FeedbackOutcome.Corrected
                or FeedbackOutcome.Rejected)
            {
                state.Responses++;
            }

            switch (feedback.Outcome)
            {
                case FeedbackOutcome.Accepted:
                    AddAcceptance(state, feedback.AcceptedCategory!, 1);
                    AddGlobalAcceptance(feedback.AcceptedCategory!, 1);
                    SubtractRefusal(state, feedback.OfferedCategory!, 1);
                    break;

                case FeedbackOutcome.Corrected:
                    AddAcceptance(state, feedback.AcceptedCategory!, 1);
                    AddGlobalAcceptance(feedback.AcceptedCategory!, 1);
                    AddRefusal(state, feedback.OfferedCategory!, 1);
                    break;

                case FeedbackOutcome.Rejected:
                    AddRefusal(state, feedback.OfferedCategory!, 1);
                    break;

                case FeedbackOutcome.Abstained:
                    if (feedback.AcceptedCategory is not null)
                    {
                        AddAcceptance(state, feedback.AcceptedCategory, 1);
                        AddGlobalAcceptance(feedback.AcceptedCategory, 1);
                    }

                    break;

                case FeedbackOutcome.Ignored:
                    break;
            }

            TrimContext(state);
            TrimPairs();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Projects the context's evidence to <paramref name="now"/>.</summary>
    /// <param name="context">The context to read.</param>
    /// <param name="now">The instant to project to; a past instant sees the state at the last update.</param>
    /// <returns>The evidence; an unknown context has no categories and zero evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public ContextPrior Prior(TContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);

        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            string key = _codec.Encode(context);

            return _contexts.TryGetValue(key, out ContextState? state)
                ? Project(state, now)
                : new ContextPrior(
                    Array.Empty<CategoryPrior>(),
                    0,
                    0,
                    0,
                    DateTimeOffset.MinValue);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Returns the categories whose refusal mass reaches
    /// <see cref="FeedbackMemoryOptions.VetoAfter"/> at <paramref name="now"/>.</summary>
    /// <param name="context">The context to read.</param>
    /// <param name="now">The instant to project to.</param>
    /// <returns>The vetoed categories; empty for an unknown context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public IReadOnlySet<string> Vetoed(TContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);

        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            string key = _codec.Encode(context);

            var vetoed = new HashSet<string>(StringComparer.Ordinal);

            if (!_contexts.TryGetValue(key, out ContextState? state))
            {
                return vetoed;
            }

            double factor = DecayFactor(now, state.LastUpdated);

            foreach ((string category, double mass) in state.Refusal)
            {
                if (mass * factor >= _options.VetoAfter)
                {
                    vetoed.Add(category);
                }
            }

            return vetoed;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Drops contexts older than the policy's maximum age.</summary>
    /// <param name="now">The instant the purge is evaluated at.</param>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public void Purge(DateTimeOffset now)
    {
        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            var expired = new List<string>();

            foreach ((string key, ContextState state) in _contexts)
            {
                if (_policy.MaxAge != Timeout.InfiniteTimeSpan
                    && now - state.LastUpdated > _policy.MaxAge)
                {
                    expired.Add(key);
                    continue;
                }

                Materialize(state, now);

                double total = state.AcceptanceTotal;

                foreach (double mass in state.Refusal.Values)
                {
                    total += mass;
                }

                if (total < _options.MinMass)
                {
                    expired.Add(key);
                }
            }

            foreach (string key in expired)
            {
                RemoveContext(key);
            }

            MaterializeGlobal(now);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Removes every category that is not in <paramref name="categories"/>.</summary>
    /// <param name="categories">The label set to keep.</param>
    /// <exception cref="ArgumentNullException"><paramref name="categories"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public void Prune(IReadOnlySet<string> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            foreach (ContextState state in _contexts.Values)
            {
                foreach (string category in state.Acceptance.Keys
                             .Where(category => !categories.Contains(category))
                             .ToList())
                {
                    state.AcceptanceTotal -= state.Acceptance[category];
                    state.Acceptance.Remove(category);
                    _pairCount--;
                }

                foreach (string category in state.Refusal.Keys
                             .Where(category => !categories.Contains(category))
                             .ToList())
                {
                    state.Refusal.Remove(category);
                    _pairCount--;
                }

                state.AcceptanceTotal = Math.Max(0, state.AcceptanceTotal);
            }

            foreach (string category in _global.Acceptance.Keys
                         .Where(category => !categories.Contains(category))
                         .ToList())
            {
                _global.AcceptanceTotal -= _global.Acceptance[category];
                _global.Acceptance.Remove(category);
                _pairCount--;
            }

            _global.AcceptanceTotal = Math.Max(0, _global.AcceptanceTotal);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Materializes the current masses at <paramref name="now"/> and then replaces the policy.
    /// The past is not reweighted: only the decay applied after this call uses the new policy.
    /// </summary>
    /// <param name="now">The instant to materialize at.</param>
    /// <param name="policy">The new policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public void Rebase(DateTimeOffset now, RecencyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            foreach (ContextState state in _contexts.Values)
            {
                Materialize(state, now);
            }

            MaterializeGlobal(now);
            Volatile.Write(ref _policy, policy);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Projects the whole memory to <paramref name="now"/> into a data transfer object.</summary>
    /// <param name="now">The instant to project to.</param>
    /// <returns>A versioned snapshot whose masses are already decayed to <paramref name="now"/>.</returns>
    /// <exception cref="ObjectDisposedException">The memory has been disposed.</exception>
    public FeedbackSnapshot Save(DateTimeOffset now)
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            var contexts = new List<ContextFeedbackSnapshot>(_contexts.Count);

            foreach (string key in _contexts.Keys.Order(StringComparer.Ordinal))
            {
                ContextState state = _contexts[key];
                double factor = DecayFactor(now, state.LastUpdated);

                contexts.Add(new ContextFeedbackSnapshot(
                    key,
                    state.LastUpdated,
                    state.Exposures,
                    state.Responses,
                    state.Observations,
                    ProjectCategories(state.Acceptance, state.Refusal, factor)));
            }

            double globalFactor = _global.AcceptanceTotal == 0 && _global.Acceptance.Count == 0
                ? 0
                : DecayFactor(now, _global.LastUpdated);

            var globalCategories = new List<CategoryFeedbackSnapshot>(_global.Acceptance.Count);

            foreach ((string category, double mass) in _global.Acceptance.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                double projected = mass * globalFactor;

                if (projected >= _options.MinMass)
                {
                    globalCategories.Add(new CategoryFeedbackSnapshot(category, projected, 0));
                }
            }

            DateTimeOffset globalUpdated = _global.LastUpdated == DateTimeOffset.MinValue
                ? now
                : _global.LastUpdated;

            return new FeedbackSnapshot(
                FeedbackSnapshot.CurrentVersion,
                _codec.Id,
                _policy,
                _options,
                _codec.IsOpaque,
                now,
                new GlobalFeedbackSnapshot(globalUpdated, globalCategories),
                contexts);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Restores a memory from a snapshot.</summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="codec">The codec to use; null uses the hashed codec.</param>
    /// <returns>A memory with the snapshot's masses, policy and bounds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidDataException">The version, codec or a mass is invalid.</exception>
    public static FeedbackMemory<TContext> Load(
        FeedbackSnapshot snapshot,
        IContextCodec<TContext>? codec = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion != FeedbackSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported feedback snapshot version {snapshot.SchemaVersion}; " +
                $"this build reads {FeedbackSnapshot.CurrentVersion}.");
        }

        codec ??= ContextCodecs.Hashed<TContext>();

        if (!string.Equals(snapshot.CodecId, codec.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The snapshot was written with codec '{snapshot.CodecId}', not '{codec.Id}'.");
        }

        var memory = new FeedbackMemory<TContext>(snapshot.Policy, snapshot.Options, codec);

        foreach (CategoryFeedbackSnapshot category in snapshot.Global.Categories)
        {
            RequireMass(category.AcceptanceMass, nameof(snapshot));
            RequireMass(category.RefusalMass, nameof(snapshot));

            if (category.AcceptanceMass > 0)
            {
                memory._global.Acceptance[category.Category] = category.AcceptanceMass;
                memory._global.AcceptanceTotal += category.AcceptanceMass;
                memory._pairCount++;
            }
        }

        memory._global.LastUpdated = snapshot.Global.LastUpdated;

        foreach (ContextFeedbackSnapshot context in snapshot.Contexts)
        {
            var state = new ContextState(context.LastUpdated)
            {
                Exposures = context.Exposures,
                Responses = context.Responses,
                Observations = context.Observations,
            };

            foreach (CategoryFeedbackSnapshot category in context.Categories)
            {
                RequireMass(category.AcceptanceMass, nameof(snapshot));
                RequireMass(category.RefusalMass, nameof(snapshot));

                if (category.AcceptanceMass > 0)
                {
                    state.Acceptance[category.Category] = category.AcceptanceMass;
                    state.AcceptanceTotal += category.AcceptanceMass;
                    memory._pairCount++;
                }

                if (category.RefusalMass > 0)
                {
                    state.Refusal[category.Category] = category.RefusalMass;
                    memory._pairCount++;
                }
            }

            memory._contexts.Add(context.Key, state);
            memory._lruNodes.Add(context.Key, memory._lru.AddLast(context.Key));

            if (memory._lastObservedAt is null || context.LastUpdated > memory._lastObservedAt.Value)
            {
                memory._lastObservedAt = context.LastUpdated;
            }
        }

        return memory;
    }

    /// <summary>Disposes the internal reader/writer lock.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }

    private ContextPrior Project(ContextState state, DateTimeOffset now)
    {
        double factor = DecayFactor(now, state.LastUpdated);
        double total = state.AcceptanceTotal * factor;

        double globalFactor = _global.AcceptanceTotal == 0 && _global.Acceptance.Count == 0
            ? 0
            : DecayFactor(now, _global.LastUpdated);

        double globalTotal = _global.AcceptanceTotal * globalFactor;
        int globalCount = 0;

        foreach (double mass in _global.Acceptance.Values)
        {
            if (mass * globalFactor > 0)
            {
                globalCount++;
            }
        }

        var keys = new HashSet<string>(state.Acceptance.Keys, StringComparer.Ordinal);
        keys.UnionWith(state.Refusal.Keys);

        var categories = new List<CategoryPrior>(keys.Count);

        foreach (string category in keys)
        {
            double acceptance = Get(state.Acceptance, category) * factor;
            double refusal = Get(state.Refusal, category) * factor;

            if (acceptance < _options.MinMass && refusal < _options.MinMass)
            {
                continue;
            }

            double globalShare = 0;

            if (globalCount > 0)
            {
                double globalMass = Get(_global.Acceptance, category) * globalFactor;

                globalShare = (globalMass + _options.GlobalSmoothing)
                    / (globalTotal + (globalCount * _options.GlobalSmoothing));
            }

            double share = (acceptance + (_options.Shrinkage * globalShare))
                / (total + _options.Shrinkage);

            categories.Add(new CategoryPrior(
                category,
                Math.Clamp(share, 0, 1),
                globalShare,
                acceptance,
                refusal));
        }

        categories.Sort(static (left, right) =>
        {
            int byShare = right.AcceptanceShare.CompareTo(left.AcceptanceShare);

            if (byShare != 0)
            {
                return byShare;
            }

            int byRefusal = right.RefusalMass.CompareTo(left.RefusalMass);

            return byRefusal != 0
                ? byRefusal
                : string.CompareOrdinal(left.Category, right.Category);
        });

        double evidence = total / (total + _options.Shrinkage);
        double responseRate = state.Exposures == 0
            ? 0
            : state.Responses / (double)state.Exposures;

        return new ContextPrior(
            categories,
            Math.Clamp(evidence, 0, 1),
            Math.Clamp(responseRate, 0, 1),
            state.Observations,
            state.LastUpdated);
    }

    private IReadOnlyList<CategoryFeedbackSnapshot> ProjectCategories(
        Dictionary<string, double> acceptance,
        Dictionary<string, double> refusal,
        double factor)
    {
        var keys = new HashSet<string>(acceptance.Keys, StringComparer.Ordinal);
        keys.UnionWith(refusal.Keys);

        var categories = new List<CategoryFeedbackSnapshot>(keys.Count);

        foreach (string category in keys.Order(StringComparer.Ordinal))
        {
            double accepted = Get(acceptance, category) * factor;
            double rejected = Get(refusal, category) * factor;

            if (accepted < _options.MinMass && rejected < _options.MinMass)
            {
                continue;
            }

            categories.Add(new CategoryFeedbackSnapshot(category, accepted, rejected));
        }

        return categories;
    }

    private void Materialize(ContextState state, DateTimeOffset now)
    {
        if (now <= state.LastUpdated)
        {
            return;
        }

        double factor = DecayFactor(now, state.LastUpdated);
        state.LastUpdated = now;

        if (factor <= 0)
        {
            _pairCount -= state.Acceptance.Count + state.Refusal.Count;
            state.Acceptance.Clear();
            state.Refusal.Clear();
            state.AcceptanceTotal = 0;
            return;
        }

        Multiply(state.Acceptance, factor);
        Multiply(state.Refusal, factor);
        state.AcceptanceTotal *= factor;

        RemoveTiny(state.Acceptance, ref state.AcceptanceTotal);
        RemoveTiny(state.Refusal);
    }

    private void MaterializeGlobal(DateTimeOffset now)
    {
        if (_global.AcceptanceTotal == 0 && _global.Acceptance.Count == 0)
        {
            _global.LastUpdated = now;
            return;
        }

        if (now <= _global.LastUpdated)
        {
            return;
        }

        double factor = DecayFactor(now, _global.LastUpdated);
        _global.LastUpdated = now;

        if (factor <= 0)
        {
            _pairCount -= _global.Acceptance.Count;
            _global.Acceptance.Clear();
            _global.AcceptanceTotal = 0;
            return;
        }

        Multiply(_global.Acceptance, factor);
        _global.AcceptanceTotal *= factor;
        RemoveTiny(_global.Acceptance, ref _global.AcceptanceTotal);
    }

    private void Touch(string key)
    {
        if (!_lruNodes.TryGetValue(key, out LinkedListNode<string>? node))
        {
            return;
        }

        if (!ReferenceEquals(node, _lru.Last))
        {
            _lru.Remove(node);
            _lru.AddLast(node);
        }
    }

    private void EvictLeastRecentlyUsed()
    {
        LinkedListNode<string>? first = _lru.First;

        if (first is not null)
        {
            RemoveContext(first.Value);
        }
    }

    private void RemoveContext(string key)
    {
        if (!_contexts.Remove(key, out ContextState? state))
        {
            return;
        }

        _pairCount -= state.Acceptance.Count + state.Refusal.Count;

        if (_lruNodes.Remove(key, out LinkedListNode<string>? node))
        {
            _lru.Remove(node);
        }
    }

    private void AddAcceptance(ContextState state, string category, double amount)
    {
        if (!state.Acceptance.TryGetValue(category, out double current))
        {
            _pairCount++;
        }

        double next = current + amount;

        if (next < _options.MinMass)
        {
            if (state.Acceptance.Remove(category))
            {
                _pairCount--;
            }

            return;
        }

        state.Acceptance[category] = next;
        state.AcceptanceTotal += amount;
    }

    private void AddGlobalAcceptance(string category, double amount)
    {
        if (!_global.Acceptance.TryGetValue(category, out double current))
        {
            _pairCount++;
        }

        double next = current + amount;

        if (next < _options.MinMass)
        {
            if (_global.Acceptance.Remove(category))
            {
                _pairCount--;
            }

            return;
        }

        _global.Acceptance[category] = next;
        _global.AcceptanceTotal += amount;
    }

    private void AddRefusal(ContextState state, string category, double amount)
    {
        if (!state.Refusal.TryGetValue(category, out double current))
        {
            _pairCount++;
        }

        double next = current + amount;

        if (next < _options.MinMass)
        {
            if (state.Refusal.Remove(category))
            {
                _pairCount--;
            }

            return;
        }

        state.Refusal[category] = next;
    }

    private void SubtractRefusal(ContextState state, string category, double amount)
    {
        if (!state.Refusal.TryGetValue(category, out double current))
        {
            return;
        }

        double next = Math.Max(0, current - amount);

        if (next < _options.MinMass)
        {
            state.Refusal.Remove(category);
            _pairCount--;
        }
        else
        {
            state.Refusal[category] = next;
        }
    }

    private void TrimContext(ContextState state)
    {
        while (state.Acceptance.Count + state.Refusal.Count > _options.MaxCategoriesPerContext)
        {
            string? victim = null;
            double smallest = double.MaxValue;

            foreach ((string category, double mass) in state.Acceptance)
            {
                if (mass < smallest)
                {
                    smallest = mass;
                    victim = category;
                }
            }

            foreach ((string category, double mass) in state.Refusal)
            {
                if (mass < smallest)
                {
                    smallest = mass;
                    victim = category;
                }
            }

            if (victim is null)
            {
                return;
            }

            if (state.Acceptance.TryGetValue(victim, out double accepted))
            {
                state.Acceptance.Remove(victim);
                state.AcceptanceTotal -= accepted;
                _pairCount--;
            }

            if (state.Refusal.Remove(victim))
            {
                _pairCount--;
            }
        }

        state.AcceptanceTotal = Math.Max(0, state.AcceptanceTotal);
    }

    private void TrimPairs()
    {
        while (_pairCount > _options.MaxPairs)
        {
            ContextState? victimState = null;
            string? victim = null;
            double smallest = double.MaxValue;
            bool fromAcceptance = false;

            foreach (ContextState state in _contexts.Values)
            {
                foreach ((string category, double mass) in state.Acceptance)
                {
                    if (mass < smallest)
                    {
                        smallest = mass;
                        victim = category;
                        victimState = state;
                        fromAcceptance = true;
                    }
                }

                foreach ((string category, double mass) in state.Refusal)
                {
                    if (mass < smallest)
                    {
                        smallest = mass;
                        victim = category;
                        victimState = state;
                        fromAcceptance = false;
                    }
                }
            }

            if (victimState is null || victim is null)
            {
                return;
            }

            if (fromAcceptance)
            {
                if (victimState.Acceptance.Remove(victim))
                {
                    victimState.AcceptanceTotal -= smallest;
                    _pairCount--;
                }
            }
            else if (victimState.Refusal.Remove(victim))
            {
                _pairCount--;
            }

            victimState.AcceptanceTotal = Math.Max(0, victimState.AcceptanceTotal);
        }
    }

    private double DecayFactor(DateTimeOffset now, DateTimeOffset last)
    {
        if (now <= last)
        {
            return 1;
        }

        TimeSpan age = now - last;

        if (_policy.MaxAge != Timeout.InfiniteTimeSpan && age > _policy.MaxAge)
        {
            return 0;
        }

        return _policy.HalfLife == Timeout.InfiniteTimeSpan
            ? 1
            : Math.Pow(0.5, age.TotalSeconds / _policy.HalfLife.TotalSeconds);
    }

    private static double Get(Dictionary<string, double> masses, string category) =>
        masses.TryGetValue(category, out double mass) ? mass : 0;

    private static void Multiply(Dictionary<string, double> masses, double factor)
    {
        foreach (string category in masses.Keys.ToList())
        {
            masses[category] *= factor;
        }
    }

    private void RemoveTiny(Dictionary<string, double> masses, ref double total)
    {
        RemoveTiny(masses);
        total = Math.Max(0, total);
    }

    private void RemoveTiny(Dictionary<string, double> masses)
    {
        foreach (string category in masses.Keys.ToList())
        {
            if (masses[category] < _options.MinMass)
            {
                masses.Remove(category);
                _pairCount--;
            }
        }
    }

    private static void RequireMass(double mass, string parameterName)
    {
        if (!double.IsFinite(mass) || mass < 0)
        {
            throw new InvalidDataException($"The snapshot carries an invalid mass in '{parameterName}'.");
        }
    }

    private static void ValidateOptions(FeedbackMemoryOptions options)
    {
        if (options.VetoAfter < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.VetoAfter,
                "VetoAfter must be at least 1.");
        }

        if (options.MaxContexts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxContexts,
                "MaxContexts must be at least 1.");
        }

        if (options.MaxCategoriesPerContext < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxCategoriesPerContext,
                "MaxCategoriesPerContext must be at least 1.");
        }

        if (options.MaxPairs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxPairs,
                "MaxPairs must be at least 1.");
        }

        if (!double.IsFinite(options.MinMass) || options.MinMass <= 0 || options.MinMass >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinMass,
                "MinMass must be finite and in (0, 1).");
        }

        if (!double.IsFinite(options.Shrinkage) || options.Shrinkage <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Shrinkage,
                "Shrinkage must be finite and positive.");
        }

        if (!double.IsFinite(options.GlobalSmoothing) || options.GlobalSmoothing <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.GlobalSmoothing,
                "GlobalSmoothing must be finite and positive.");
        }
    }

    private static void ValidateFeedback(FeedbackEvent<TContext> feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        if (feedback.Text is null)
        {
            throw new ArgumentException("Feedback text cannot be null.", nameof(feedback));
        }

        if (feedback.Propensity is { } propensity
            && (!double.IsFinite(propensity) || propensity <= 0 || propensity > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(feedback),
                propensity,
                "Propensity must be in (0, 1].");
        }

        switch (feedback.Outcome)
        {
            case FeedbackOutcome.Accepted:
                RequireCategories(feedback, offered: true, accepted: true);

                if (!string.Equals(feedback.OfferedCategory, feedback.AcceptedCategory, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Accepted requires the offered and accepted categories to be equal.",
                        nameof(feedback));
                }

                break;

            case FeedbackOutcome.Corrected:
                RequireCategories(feedback, offered: true, accepted: true);

                if (string.Equals(feedback.OfferedCategory, feedback.AcceptedCategory, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Corrected requires the offered and accepted categories to differ.",
                        nameof(feedback));
                }

                break;

            case FeedbackOutcome.Rejected:
            case FeedbackOutcome.Ignored:
                RequireCategories(feedback, offered: true, accepted: false);
                break;

            case FeedbackOutcome.Abstained:
                RequireCategories(feedback, offered: false, accepted: false);

                if (feedback.Propensity is not null)
                {
                    throw new ArgumentException(
                        "An abstention has no logged action and therefore no propensity.",
                        nameof(feedback));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(feedback),
                    feedback.Outcome,
                    "Unknown feedback outcome.");
        }

        if (feedback.Offered is not { } offered)
        {
            return;
        }

        if (feedback.OfferedCategory is null)
        {
            throw new ArgumentException(
                "An offered distribution requires an offered category.",
                nameof(feedback));
        }

        bool containsOffer = false;

        foreach (ClassificationResult result in offered)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (!double.IsFinite(result.Probability) || result.Probability is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(feedback),
                    result.Probability,
                    "Offered probabilities must be in [0, 1].");
            }

            if (string.Equals(result.Category, feedback.OfferedCategory, StringComparison.Ordinal))
            {
                containsOffer = true;
            }
        }

        if (!containsOffer)
        {
            throw new ArgumentException(
                "The offered distribution does not contain the offered category.",
                nameof(feedback));
        }
    }

    private static void RequireCategories(
        FeedbackEvent<TContext> feedback,
        bool offered,
        bool accepted)
    {
        if (offered && feedback.OfferedCategory is null)
        {
            throw new ArgumentException("This outcome requires an offered category.", nameof(feedback));
        }

        if (!offered && feedback.OfferedCategory is not null)
        {
            throw new ArgumentException("This outcome cannot carry an offered category.", nameof(feedback));
        }

        if (accepted && feedback.AcceptedCategory is null)
        {
            throw new ArgumentException("This outcome requires an accepted category.", nameof(feedback));
        }

        if (!accepted && feedback.AcceptedCategory is not null)
        {
            throw new ArgumentException("This outcome cannot carry an accepted category.", nameof(feedback));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

    private sealed class ContextState
    {
        public ContextState(DateTimeOffset lastUpdated)
        {
            LastUpdated = lastUpdated;
        }

        public DateTimeOffset LastUpdated;

        public double AcceptanceTotal;

        public long Exposures;

        public long Responses;

        public long Observations;

        public Dictionary<string, double> Acceptance { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, double> Refusal { get; } = new(StringComparer.Ordinal);
    }

    private sealed class GlobalState
    {
        public DateTimeOffset LastUpdated = DateTimeOffset.MinValue;

        public double AcceptanceTotal;

        public Dictionary<string, double> Acceptance { get; } = new(StringComparer.Ordinal);
    }
}
