using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>
/// Makes cross-encoder reranking resumable, by remembering the score of every
/// <c>(query, document)</c> pair it has already computed.
/// </summary>
/// <remarks>
/// <para>
/// The reason is arithmetic. A reranked configuration costs one forward pass per candidate per
/// query, so a corpus of a thousand long queries at a hundred candidates is tens of thousands of
/// passes and hours of CPU. A run interrupted at hour nine must not begin again at hour zero, and
/// the three reranked configurations of one corpus share most of their candidates, so the second
/// and third get most of their work for free.
/// </para>
/// <para>
/// The cache is an append-only text log — <c>hash(TAB)documentId(TAB)score</c> — flushed to disk
/// every <see cref="FlushEvery"/> new pairs. A process killed mid-write leaves one partial line; the
/// loader counts it as unreadable and keeps the rest rather than refusing a file that is 99.99 %
/// valid. Nothing is ever rewritten, so an interrupted flush cannot lose entries already on disk.
/// </para>
/// <para>
/// The harness reranks from a <see cref="System.Threading.Tasks.Parallel"/> loop when <c>--jobs</c>
/// is above one, so <see cref="Rerank"/> is called from several threads at once. The score store is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>: a lookup takes no lock, and a reranked
/// configuration makes one lookup per candidate, so the read path is the one worth keeping
/// uncontended. The log's integrity is a lock around one whole line, and the model call is outside
/// every lock — that is the work the workers are there to overlap.
/// </para>
/// <para>
/// The query is keyed by a hash of its text rather than by its id, because that is what the reranker
/// is given: two queries with the same text score the same by construction, and the interface has no
/// id to key on.
/// </para>
/// </remarks>
internal sealed class CachedReranker : IReranker, IDisposable
{
    /// <summary>New pairs between two flushes. One query at the 100-candidate depth is ~6 % of it.</summary>
    private const int FlushEvery = 1_000;

    private readonly IReranker _inner;
    private readonly ConcurrentDictionary<(string Query, string Document), float> _scores = new();
    private readonly StreamWriter _log;
    private readonly bool _readOnly;

    // The log and the flush counter share one lock, and that is the whole boundary. A line is
    // written inside one critical section, which is what stops several workers' Writes from
    // splicing into a line with four fields; the flush is in that same section, so it cannot run
    // beside a half-written line; and _sinceFlush is read, reset and incremented only there, so no
    // increment can land between the threshold check and the reset and be lost. Holding the lock
    // for the flush blocks the other appenders for its duration — acceptable against a model pass,
    // and the alternative is a second lock whose interaction with the counter is the defect again.
    private readonly object _logLock = new();

    private int _sinceFlush;
    private int _reused;
    private int _computed;
    private int _appended;
    private bool _disposed;

    /// <param name="inner">The reranker that actually runs the model.</param>
    /// <param name="path">Cache file; created when absent, appended to when present.</param>
    /// <param name="readOnly">
    /// Load the cache and serve it without writing. For a run that should not grow the cache.
    /// </param>
    public CachedReranker(IReranker inner, string path, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _inner = inner;
        _readOnly = readOnly;
        CachePath = path;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        Load();

        _log = new StreamWriter(new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    /// <summary>The file this cache reads and appends to.</summary>
    public string CachePath { get; }

    /// <summary>Pairs read from the cache file at start-up: one per distinct pair, not one per line.</summary>
    public int Loaded { get; private set; }

    /// <summary>Lines in the cache file that could not be read, typically one truncated tail.</summary>
    public int Unreadable { get; private set; }

    /// <summary>
    /// Repeated lines in the cache file, kept once. This writer does not produce them; the loader
    /// counts them so that a file which carries two scores for one pair is visible rather than
    /// silent.
    /// </summary>
    public int Duplicates { get; private set; }

    /// <summary>Pairs served from the cache instead of the model, this run.</summary>
    public int Reused => Volatile.Read(ref _reused);

    /// <summary>Pairs the model scored, this run.</summary>
    public int Computed => Volatile.Read(ref _computed);

    /// <summary>
    /// Distinct pairs this run added to the file. Lower than <see cref="Computed"/> only when two
    /// workers missed the same pair at the same moment: the model ran twice, the file gained one line.
    /// </summary>
    public int Appended => Volatile.Read(ref _appended);

    public IReadOnlyList<(string DocumentId, float Score)> Rerank(
        string query, IReadOnlyList<SearchDocument> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        string queryKey = Hash(query);
        var scores = new float[candidates.Count];
        var missing = new List<int>();
        int reused = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            if (_scores.TryGetValue((queryKey, candidates[i].Id), out float score))
            {
                scores[i] = score;
                reused++;
            }
            else
            {
                missing.Add(i);
            }
        }

        // One Interlocked.Add per query rather than one per candidate: the count is local until the
        // loop ends, and the store is what the workers contend on, not this line.
        if (reused > 0)
            Interlocked.Add(ref _reused, reused);

        if (missing.Count > 0)
        {
            IReadOnlyList<SearchDocument> toScore = missing.Select(index => candidates[index]).ToArray();
            Dictionary<string, float> fresh = _inner
                .Rerank(query, toScore)
                .ToDictionary(pair => pair.DocumentId, pair => pair.Score, StringComparer.Ordinal);

            foreach (int index in missing)
            {
                if (!fresh.TryGetValue(candidates[index].Id, out float score))
                {
                    // The inner reranker dropped a candidate, which it does not do; treating it as a
                    // miss would silently rank it last on a re-run that had it cached.
                    throw new InvalidOperationException(
                        $"The reranker returned no score for document '{candidates[index].Id}', so its "
                        + "result cannot be cached without changing the ranking.");
                }

                scores[index] = Remember(queryKey, candidates[index].Id, score);
            }

            Interlocked.Add(ref _computed, missing.Count);
        }

        // The same ordering the uncached reranker produces: score descending, candidate order kept on
        // a tie. Rebuilding it here rather than trusting the inner order is what makes a resumed run
        // reproduce an uninterrupted one exactly, including at the rank-10 boundary.
        return candidates
            .Select((candidate, index) => (candidate.Id, Score: scores[index]))
            .OrderByDescending(pair => pair.Score)
            .ToArray();
    }

    /// <summary>
    /// Flushes whatever is buffered and closes the file. Safe to call more than once, and safe to
    /// call while another thread is appending a line: it waits for that line under the log lock
    /// rather than flushing underneath it.
    /// </summary>
    /// <remarks>
    /// It is not safe to call while another thread is between its model call and its append — that
    /// append throws <see cref="ObjectDisposedException"/> rather than writing to a closed file. The
    /// contract the harness relies on is the stronger, simpler one: the cache is disposed after the
    /// query loop returns, which joins every worker, and never from inside it.
    /// </remarks>
    public void Dispose()
    {
        lock (_logLock)
        {
            if (_disposed)
                return;

            _log.Flush();
            _log.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Records a score and returns the one the cache now holds: the caller's, unless another worker
    /// stored the same pair first, in which case that worker's value is what every later run reads
    /// and what this query ranks with. Only the worker that stores the pair appends a line, so the
    /// file carries one line per pair even when two workers scored it at the same moment.
    /// </summary>
    private float Remember(string queryKey, string documentId, float score)
    {
        if (_scores.TryAdd((queryKey, documentId), score))
        {
            Append(queryKey, documentId, score);
            return score;
        }

        // The pair lost a race with another worker's identical computation. The model is the same and
        // the text is the same, so the two scores agree; reading the stored one is what makes them
        // agree even if a future model is not bit-reproducible.
        return _scores.TryGetValue((queryKey, documentId), out float stored) ? stored : score;
    }

    private void Append(string queryKey, string documentId, float score)
    {
        if (_readOnly)
            return;

        string line = string.Concat(
            queryKey,
            "\t",
            documentId,
            "\t",
            score.ToString("R", CultureInfo.InvariantCulture),
            "\n");

        lock (_logLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _log.Write(line);
            _appended++;
            _sinceFlush++;

            FlushIfDueLocked();
        }
    }

    /// <summary>Flushes when the buffer has grown by <see cref="FlushEvery"/> pairs. Requires the lock.</summary>
    private void FlushIfDueLocked()
    {
        if (_sinceFlush < FlushEvery)
            return;

        // Flush(true) pushes through to the device, so a crash loses at most the pairs after the
        // last flush rather than the whole buffer — the point of the file in the first place.
        _log.Flush();
        (_log.BaseStream as FileStream)?.Flush(flushToDisk: true);
        _sinceFlush = 0;
    }

    private void Load()
    {
        if (!File.Exists(CachePath))
            return;

        using var reader = new StreamReader(CachePath, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;

            string[] parts = line.Split('\t');

            if (parts.Length != 3
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float score))
            {
                // A kill during a write leaves exactly one of these, at the end. A count of more than
                // one is corruption and still is not a reason to throw away the readable majority.
                Unreadable++;
                continue;
            }

            // First line wins, and a repeat is counted: two lines for one pair are two scores for it,
            // and the first is the one the run that wrote it ranked with.
            if (_scores.TryAdd((parts[0], parts[1]), score))
                Loaded++;
            else
                Duplicates++;
        }
    }

    /// <summary>A query's text as a stable, short key. Collisions across a corpus are not plausible.</summary>
    private static string Hash(string query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)).AsSpan(0, 8));
}
