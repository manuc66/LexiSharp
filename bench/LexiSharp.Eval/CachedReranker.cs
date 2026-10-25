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
    private readonly Dictionary<(string Query, string Document), float> _scores = [];
    private readonly StreamWriter _log;
    private readonly bool _readOnly;

    private int _sinceFlush;

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

    /// <summary>Pairs read from the cache file at start-up.</summary>
    public int Loaded { get; private set; }

    /// <summary>Lines in the cache file that could not be read, typically one truncated tail.</summary>
    public int Unreadable { get; private set; }

    /// <summary>Pairs served from the cache instead of the model, this run.</summary>
    public int Reused { get; private set; }

    /// <summary>Pairs the model scored, this run.</summary>
    public int Computed { get; private set; }

    public IReadOnlyList<(string DocumentId, float Score)> Rerank(
        string query, IReadOnlyList<SearchDocument> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        string queryKey = Hash(query);
        var scores = new float[candidates.Count];
        var missing = new List<int>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (_scores.TryGetValue((queryKey, candidates[i].Id), out float score))
            {
                scores[i] = score;
                Reused++;
            }
            else
            {
                missing.Add(i);
            }
        }

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

                scores[index] = score;
                Compute(queryKey, candidates[index].Id, score);
            }

            Computed += missing.Count;
            FlushIfDue();
        }

        // The same ordering the uncached reranker produces: score descending, candidate order kept on
        // a tie. Rebuilding it here rather than trusting the inner order is what makes a resumed run
        // reproduce an uninterrupted one exactly, including at the rank-10 boundary.
        return candidates
            .Select((candidate, index) => (candidate.Id, Score: scores[index]))
            .OrderByDescending(pair => pair.Score)
            .ToArray();
    }

    /// <summary>Writes whatever is buffered. Safe to call more than once.</summary>
    public void Dispose()
    {
        _log.Flush();
        _log.Dispose();
    }

    private void Compute(string queryKey, string documentId, float score)
    {
        _scores[(queryKey, documentId)] = score;
        _sinceFlush++;

        if (_readOnly)
            return;

        _log.Write(queryKey);
        _log.Write('\t');
        _log.Write(documentId);
        _log.Write('\t');
        _log.Write(score.ToString("R", CultureInfo.InvariantCulture));
        _log.Write('\n');
    }

    private void FlushIfDue()
    {
        if (_readOnly || _sinceFlush < FlushEvery)
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

            _scores[(parts[0], parts[1])] = score;
            Loaded++;
        }
    }

    /// <summary>A query's text as a stable, short key. Collisions across a corpus are not plausible.</summary>
    private static string Hash(string query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)).AsSpan(0, 8));
}
