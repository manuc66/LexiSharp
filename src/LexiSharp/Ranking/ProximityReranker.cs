using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>How <see cref="ProximityReranker"/> turns a term window into a score change.</summary>
public enum ProximityMode
{
    /// <summary>
    /// Multiply the first-stage score by a factor in <c>[floor, 1]</c> derived from the window, so a
    /// widely spread match is pulled <b>down</b> — but never below the floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The floor is load-bearing, and its absence was a real bug.</b> The natural decay
    /// <c>1 - strength × (1 - n/W)</c> is unbounded: two terms 81 apart in a 100-token document give
    /// <c>n/W ≈ 0.025</c>, a 97.5 % penalty, and in a 10 000-token document at opposite ends
    /// <c>n/W ≈ 0.0002</c> — a factor of one five-thousandth. That punishes a long document for
    /// being long, when the question being asked (« are these terms near each other? ») is relative
    /// by nature. It cost 0.15 nDCG@5 on the reference corpus before the floor was added, and unit
    /// tests did not catch it: they asserted that an adjacent match outranks a spread one, which
    /// holds just as happily under a 2 % penalty as under a 97 % one.
    /// </para>
    /// <para>
    /// The floor bounds how much any single document can lose, which is what makes the shape a
    /// preference rather than a veto. Whether it then helps is still a property of your corpus —
    /// measure it.
    /// </para>
    /// </remarks>
    Damp = 0,

    /// <summary>
    /// Add a proximity term, scaled by the query terms' idf, to the first-stage score, so a tightly
    /// co-located match is promoted <b>up</b> without demoting anything else.
    /// </summary>
    /// <remarks>
    /// This is the shape a phrase-boost clause has: the lexical score stays the primary signal and
    /// proximity can only add. Whether it helps is a property of the corpus — measure it.
    /// </remarks>
    Boost = 1,
}

/// <summary>
/// Re-ranks a candidate list by how <b>close together</b> the query terms actually occur in each
/// document, refining the order a term-frequency scorer produced without discarding its signal.
/// </summary>
/// <remarks>
/// <para>
/// BM25 and TF-IDF score a document by how often its query terms occur. They are blind to
/// <i>where</i>: a document saying « the quick brown fox jumps over the lazy dog » scores the same
/// for <c>quick fox</c> as one where « quick » and « fox » sit in the same sentence. A proximity
/// reranker is the second stage that notices the difference, which is why this is a reranker and
/// not a scorer — it refines a shortlist the first stage already deemed relevant, and never
/// promotes a document the first stage rejected.
/// </para>
/// <para>
/// <b>The measure is the minimum window.</b> Let the query have <c>n</c> distinct terms, and let
/// <c>W</c> be the smallest number of consecutive token positions containing at least one occurrence
/// of every one of them. <c>W ≥ n</c> always, and the reranker turns that into a
/// <b>tightness</b> in <c>(0, 1]</c> — <c>1.0</c> when the terms are contiguous, smaller as they
/// spread:
/// </para>
/// <code>
/// tightness = n / W
/// </code>
/// <para>
/// <see cref="ProximityMode"/> then decides what tightness does, and the two shapes are not
/// interchangeable — measured, they behave differently:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="ProximityMode.Damp"/> — <c>score × (1 - strength × (1 - tightness))</c>. The factor
/// is in <c>(0, 1]</c>, so this can only ever lower a score. <b>This measurably hurts</b> at full
/// strength on every corpus tried here, and is kept because it is the right tool for a corpus where
/// a spread-out match really is a weaker match.
/// </item>
/// <item>
/// <see cref="ProximityMode.Boost"/> — <c>score + strength × Σ idf(t) × tightness</c>. The proximity
/// term lives on its own scale, so this can only raise a score and never demotes on distance alone.
/// </item>
/// </list>
/// <para>
/// Either way the score is guarded against reaching <c>0</c>, which would read as "not a match" and
/// drop the document, and against NaN or infinity.
/// </para>
/// <para>
/// <b>Two cases deliberately get no adjustment.</b> A single-term query has no proximity to speak of
/// (<c>n = W = 1</c>). And a candidate missing one of the query terms entirely has no window covering
/// them all, so it is left untouched — whether a document should contain every query term is the
/// first-stage scorer's judgement, not this one's, and adjusting it here would quietly re-rank on
/// term coverage.
/// </para>
/// <para>
/// Field boundaries count as distance. Positions of separate <see cref="SearchDocument.TextFields"/>
/// runs are separated by a gap, so a query matching a title and a body gets a correspondingly larger
/// window — the intended reading, not an accident.
/// </para>
/// <para>
/// <b>No claim that this improves retrieval.</b> The arithmetic is specified and tested; whether
/// proximity helps is a property of your corpus. Measure it with
/// <c>--configs bm25,bm25-proximity</c> rather than assuming, and see
/// <see cref="ProximityMode"/> for what came out here.
/// </para>
/// </remarks>
public sealed class ProximityReranker : IReranker
{
    private readonly ITextIndex _index;
    private readonly ITokenizer _tokenizer;
    private readonly double _strength;
    private readonly double _floor;
    private readonly ProximityMode _mode;

    /// <param name="index">
    /// The index the candidates were retrieved from. Needed for term positions
    /// (<see cref="IReadOnlyTextIndex.GetTermPositions"/>) and, in <see cref="ProximityMode.Boost"/>, for
    /// document frequencies. It is read only.
    /// </param>
    /// <param name="tokenizer">
    /// Tokenizer for the query. <b>Pass the one the index was built with</b> — if it differs, the
    /// query terms will not line up with the indexed positions and the window is measured over the
    /// wrong tokens. Left null this falls back to <see cref="Tokenizer.Default"/>, which is only
    /// correct when the index used that one too.
    /// </param>
    /// <param name="strength">
    /// How hard to apply <paramref name="mode"/>, in <c>[0, 1]</c>. <c>0</c> is a no-op. In
    /// <see cref="ProximityMode.Damp"/> it scales the pull-down; in
    /// <see cref="ProximityMode.Boost"/> it scales the added proximity term.
    /// </param>
    /// <param name="floor">
    /// In <see cref="ProximityMode.Damp"/>, the lowest factor any document can be pulled down to, in
    /// <c>(0, 1]</c>. This is what keeps the penalty bounded, and therefore proportional: without it
    /// the decay multiplies a long document by a factor approaching zero. Ignored by
    /// <see cref="ProximityMode.Boost"/>. Defaults to <c>0.5</c> — no document ever loses more than
    /// half its score.
    /// </param>
    /// <param name="mode">Which shape to apply. Defaults to <see cref="ProximityMode.Damp"/>.</param>
    public ProximityReranker(
        ITextIndex index,
        ITokenizer? tokenizer = null,
        double strength = 1.0,
        ProximityMode mode = ProximityMode.Damp,
        double floor = 0.5)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (double.IsNaN(strength) || double.IsInfinity(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strength), strength, "strength must be within [0, 1] and finite.");
        }

        if (double.IsNaN(floor) || double.IsInfinity(floor) || floor is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(floor), floor, "The damp floor must be within (0, 1] and finite.");
        }

        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown proximity mode.");

        _index = index;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _strength = strength;
        _floor = floor;
        _mode = mode;
    }

    /// <inheritdoc />
    public string Name => _mode == ProximityMode.Boost ? "Proximity (boost)" : "Proximity (damp)";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return candidates;

        if (_strength == 0)
            return [.. candidates];

        var terms = Distinct(_tokenizer.Tokenize(query));

        // Nothing to be close or far about.
        if (terms.Count < 2)
            return [.. candidates];

        var occurrences = new List<PositionedTerm>(terms.Count * 4);
        bool everyTermPresent = true;

        foreach (string term in terms)
        {
            var positions = CollectPositions(term, candidates);
            var any = false;

            foreach (var (position, candidate) in positions)
            {
                occurrences.Add(new PositionedTerm(candidate, position, term));
                any = true;
            }

            everyTermPresent &= any;
        }

        if (!everyTermPresent)
            return [.. candidates];

        occurrences.Sort(static (left, right) =>
        {
            int byDocument = left.CandidateIndex.CompareTo(right.CandidateIndex);
            return byDocument != 0 ? byDocument : left.Position.CompareTo(right.Position);
        });

        var tightest = TightestWindowPerCandidate(occurrences, terms.Count);

        // Boost needs the proximity term on its own scale, so it sums the query terms' idf once.
        double idfSum = 0;

        if (_mode == ProximityMode.Boost && _index.StatisticDocumentCount > 0)
        {
            foreach (string term in terms)
            {
                int df = _index.DocumentFrequency(term);
                idfSum += Math.Log(1.0 + (_index.StatisticDocumentCount - df + 0.5) / (df + 0.5));
            }
        }

        var reranked = new List<SearchResult>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            if (!tightest.TryGetValue(i, out int window) || window <= 0)
            {
                reranked.Add(candidate);
                continue;
            }

            double tightness = (double)terms.Count / window;
            double score;

            if (_mode == ProximityMode.Boost)
            {
                score = candidate.Score + _strength * idfSum * tightness;
            }
            else
            {
                // Bounded: the raw decay 1 - strength*(1 - tightness) approaches 0 as the window
                // grows, which annihilates long documents for the crime of being long.
                double factor = 1.0 - _strength * (1.0 - tightness);
                score = candidate.Score * Math.Max(factor, _floor);
            }

            // A non-positive score would read as "not a match" and drop the document, and NaN or
            // infinity is rejected by consuming engines. The arithmetic cannot produce either for a
            // finite window, but the convention is worth enforcing rather than assuming.
            if (score <= 0 || double.IsNaN(score) || double.IsInfinity(score))
            {
                reranked.Add(candidate);
                continue;
            }

            reranked.Add(candidate with { Score = score });
        }

        // Same order on equal scores as the input, so the reranker's own sorting never reshuffles
        // ties the first stage had already settled.
        return [.. reranked
            .Select((result, index) => (result, index))
            .OrderByDescending(entry => entry.result.Score)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.result)];
    }

    /// <summary>
    /// Collects the positions of one query term across every candidate, paired with the candidate's
    /// index. One pass over the candidates, so an absent document is a cheap miss rather than a
    /// dictionary lookup per document.
    /// </summary>
    private List<(int Position, int CandidateIndex)> CollectPositions(
        string term,
        IReadOnlyList<SearchResult> candidates)
    {
        var found = new List<(int Position, int CandidateIndex)>();

        for (int i = 0; i < candidates.Count; i++)
        {
            foreach (int position in _index.GetTermPositions(candidates[i].DocumentId, term))
                found.Add((position, i));
        }

        return found;
    }

    private readonly record struct PositionedTerm(int CandidateIndex, int Position, string Term);

    /// <summary>
    /// The smallest window per candidate, by a two-pointer sweep over that candidate's occurrences
    /// in position order. The occurrences arrive sorted by (candidate, position), so each candidate's
    /// run is contiguous and the sweep never walks the list twice.
    /// </summary>
    private static Dictionary<int, int> TightestWindowPerCandidate(
        List<PositionedTerm> occurrences,
        int termCount)
    {
        var tightest = new Dictionary<int, int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        int runStart = 0;
        int runCandidate = occurrences[0].CandidateIndex;

        void CloseRun(int endExclusive)
        {
            if (endExclusive > runStart)
            {
                int best = int.MaxValue;
                int left = runStart;

                for (int right = runStart; right < endExclusive; right++)
                {
                    counts[occurrences[right].Term] = counts.GetValueOrDefault(occurrences[right].Term) + 1;

                    while (left <= right && counts.Count == termCount)
                    {
                        int window = occurrences[right].Position - occurrences[left].Position + 1;
                        if (window < best)
                            best = window;

                        string leftTerm = occurrences[left].Term;
                        counts[leftTerm] = counts[leftTerm] - 1;
                        if (counts[leftTerm] == 0)
                            counts.Remove(leftTerm);
                        left++;
                    }
                }

                tightest[runCandidate] = best == int.MaxValue ? 0 : best;
            }

            counts.Clear();
        }

        for (int i = 1; i < occurrences.Count; i++)
        {
            if (occurrences[i].CandidateIndex != runCandidate)
            {
                CloseRun(i);
                runStart = i;
                runCandidate = occurrences[i].CandidateIndex;
            }
        }

        CloseRun(occurrences.Count);
        return tightest;
    }

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> terms)
    {
        if (terms is DistinctTermList)
            return terms;

        return TermDeduplicator.Distinct(terms);
    }
}
