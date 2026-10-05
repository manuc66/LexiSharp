using System.Globalization;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Scores a document by its <b>best window</b> rather than by its whole text: BM25's saturation
/// computed over term frequencies counted inside a sliding window, and the maximum taken over every
/// window of every requested width.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unit is still the document.</b> A window is a way of counting term frequencies, not a
/// separate indexed unit: the document is returned whole, keeps its id, and is ranked against other
/// documents. Nothing here chunks anything or requires the caller to have chunked anything.
/// </para>
/// <para>
/// <b>The formula, and what is missing from it.</b> For a window <c>w</c> of width <c>s</c>,
/// <c>score(w) = Σ_t idf(t) · tf_w(t) · (k1 + 1) / (tf_w(t) + k1)</c>, and the document's score is the
/// largest such value over all windows and all widths. There is deliberately <b>no length
/// normalization</b> in it: a window's length is its own width by construction, so there is nothing
/// to normalize it against, and a term's damping inside a window is decided by how many times it
/// occurs in those <c>s</c> positions and by nothing else. The length information that BM25's
/// <c>b</c> uses is therefore absent here rather than neutralized — and that is the point of the
/// type. Score a document by its best passage and length stops being what ranks it; if you want
/// length to still count, put it back explicitly as a term of its own (see
/// <see cref="DocumentLengthRatioScorer"/>) with a weight you chose, instead of inheriting a
/// coefficient fitted to whole documents.
/// </para>
/// <para>
/// <b>The identity this type is built to satisfy.</b> With <c>includeWholeDocument</c> and no
/// widths, the single window is the whole document, every <c>tf_w</c> is the document's term
/// frequency, and the normalization is 1 — which is precisely BM25 with <c>b = 0</c>. The whole
/// document is not an approximation of the windowed score; it is the same arithmetic, and
/// <c>WindowBm25ScorerTests</c> asserts it equals <c>new Bm25Scorer(k1, b: 0)</c> to the bit.
/// Comparing the two arms of a windowed-versus-whole experiment through this type therefore compares
/// one scorer against itself rather than two of them, which is what makes the difference the windowing
/// and nothing else.
/// </para>
/// <para>
/// <b>An <see cref="ITermOverlapScorer"/>, unlike the composite.</b> A document sharing no query term
/// has no position inside any window, so this scorer returns <c>0</c> for it exactly as
/// <see cref="Bm25Scorer"/> does, and the engine may skip such documents on its behalf. Composed
/// inside a <see cref="WeightedCompositeScorer"/> alongside a component that scores non-matching
/// documents, that promise belongs to the composite and not to this type — see that type's remarks
/// for what a caller does about it.
/// </para>
/// <para>
/// <b>Never the term-at-a-time accumulation pass.</b> That pass scores by ordinal, from a weight the
/// scorer hands it per query term; counting term frequencies per window needs the positions of the
/// document in hand, which the buffer does not carry. So an engine serving this scorer declines the
/// fast path and walks the candidate set document by document, which is a price and not a defect —
/// the same trade <see cref="SearchOptions.AccumulateFilteredQueries"/> makes explicit for a filter.
/// The cost per candidate is one merged pass over the positions that actually match, plus one window
/// sweep per width; a corpus of long documents with a wide set of widths is not cheap, which is the
/// second half of why a cascade — retrieval over a shortlist, then this on the shortlist — is the
/// topology it belongs to.
/// </para>
/// <para>
/// <b>The width is the whole decision, it belongs to the corpus, and on one of the two corpora measured
/// here the gain is nothing at all.</b> The evidence is an external measurement — harness outside this
/// repository, not reproducible from it — run on this type rather than on a re-implementation of it,
/// against its own whole-document counterpart, which <c>includeWholeDocument</c> makes BM25 at
/// <c>b = 0</c>. Width chosen on a development split and read on a held-out one. On a corpus of meeting
/// transcripts it is a large gain; on one of book-length narrative no width beat zero, and the
/// development split's deltas were positive at every width while the test split's were negative or
/// zero at every width. Same code, same defaults, same protocol. <c>docs/ranking.md</c> carries the
/// figures and the protocol; the numbers are not repeated here because a figure in a comment on a
/// public type is an assertion with nothing behind it.
/// </para>
/// <para>
/// <b>Read any such figure against the right alternative.</b> The counterpart above is this type at
/// <c>b = 0</c>, which is the honest within-family comparison and not a strong scorer: a
/// whole-document <see cref="Bm25Scorer"/> tuned on the same development split beats it by a wide
/// margin on both corpora. Measured against <i>that</i> baseline, the same formula's advantage shrinks
/// to a small gain on the first corpus and becomes a loss on the second. A caller who has already
/// tuned <see cref="Bm25Scorer"/> should plan against the second comparison and not the first, and
/// <c>docs/ranking.md</c> gives both.
/// </para>
/// <para>
/// Two things that measurement settled, both of which a plausible reading gets wrong. The candidate
/// generator matters as much as the width does — the default stride makes windows abut and a stride of
/// a quarter width overlaps them four deep — and it decides whether a narrow window is unusable or
/// merely useless. And a window too narrow to hold the passage costs less than it looks: an earlier
/// re-implementation reported a collapse at 16 terms that was its fixed-count candidate generator
/// rather than the width.
/// </para>
/// <para>
/// Measure the sweep on the corpus you ship, choose the width on a split you then do not read, and
/// read it per length band rather than as one average: a single average over mixed document lengths
/// will look like a plateau where your corpus has a slope.
/// </para>
/// <para>
/// <b>Thread-safe.</b> Everything the score needs is computed inside the call; the instance holds only
/// its configuration, and two concurrent searches may share one.
/// </para>
/// </remarks>
public sealed class WindowBm25Scorer : ITextScorer, ITermOverlapScorer
{
    private readonly int[] _widths;
    private readonly int _stride;
    private readonly bool _includeWholeDocument;
    private readonly double _k1;
    private readonly double _saturation;
    private readonly string _name;

    /// <summary>Builds a windowed scorer.</summary>
    /// <param name="widths">
    /// Window widths in tokens; several of them sweep the document at more than one scale and the
    /// score is the best over all of them. At least one width, or
    /// <paramref name="includeWholeDocument"/>, is required. A width longer than a document scores
    /// that whole document. <c>null</c> is the same as an empty list.
    /// </param>
    /// <param name="stride">
    /// How far the window advances between positions, in tokens; <c>0</c> — the default — means
    /// non-overlapping, each width advancing by its own length. An advance wider than the window
    /// would leave text unscored and is capped at the width instead.
    /// </param>
    /// <param name="includeWholeDocument">
    /// Also score the whole document as one window. Combined with widths this is a multi-scale sweep
    /// that includes the document itself, which is the shape a sliding-window sweep usually wants;
    /// alone, it is the scorer this type reduces to when <paramref name="widths"/> is empty.
    /// </param>
    /// <param name="k1">Saturation parameter, as in BM25. Defaults to <c>1.5</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A width is not positive, <paramref name="stride"/> is negative, or <paramref name="k1"/> is
    /// negative or not finite.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Neither a width nor <paramref name="includeWholeDocument"/> was given, so the scorer would
    /// have no window to score.
    /// </exception>
    public WindowBm25Scorer(
        IReadOnlyList<int>? widths = null,
        int stride = 0,
        bool includeWholeDocument = false,
        double k1 = 1.5)
    {
        if (widths is not null)
        {
            _widths = new int[widths.Count];

            for (int i = 0; i < widths.Count; i++)
            {
                int width = widths[i];

                if (width <= 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(widths), width, "A window width must be positive.");

                _widths[i] = width;
            }
        }
        else
        {
            _widths = [];
        }

        if (_widths.Length == 0 && !includeWholeDocument)
            throw new ArgumentException(
                "At least one window width, or includeWholeDocument, is required: a scorer with no window has nothing to score.",
                nameof(widths));

        if (stride < 0)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "Stride must not be negative.");

        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
            throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative and finite.");

        _stride = stride;
        _includeWholeDocument = includeWholeDocument;
        _k1 = k1;
        // The same multiplier BM25 applies, so the whole-document window and BM25 with b = 0 agree
        // on the numerator as well as the denominator.
        _saturation = k1 + 1.0;
        _name = BuildName();
    }

    /// <inheritdoc />
    public string Name => _name;

    /// <summary>The window widths this scorer sweeps, in the order it sweeps them.</summary>
    public IReadOnlyList<int> Widths => _widths;

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);

        // The same three refusals Bm25Scorer makes, and for the same reason: no corpus statistics, no
        // text, or no average to divide by. All three are 0 rather than an exception, so a document
        // that cannot be scored joins the ones that do not match.
        if (documentCount == 0 || documentLength == 0 || index.AverageDocumentLength <= 0)
            return 0;

        var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

        var idf = new double[terms.Count];
        var positions = new IReadOnlyList<int>[terms.Count];
        var events = new List<Occurrence>(documentLength);
        int present = 0;

        for (int i = 0; i < terms.Count; i++)
        {
            positions[i] = index.GetTermPositions(documentId, terms[i]);

            if (positions[i].Count == 0)
                continue;

            int df = index.DocumentFrequency(terms[i]);

            // Lucene's idf, the same expression Bm25Scorer evaluates: the two scorers have to agree
            // on the rare-term weight, or the whole-document window would differ from BM25 for a
            // reason that has nothing to do with windows.
            idf[i] = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));

            for (int p = 0; p < positions[i].Count; p++)
                events.Add(new Occurrence(positions[i][p], i));

            present++;
        }

        if (present == 0)
            return 0;

        // One sort for every width rather than one per width. The position lists are not required to
        // arrive sorted — IReadOnlyTextIndex does not promise it — and a document holds at most one
        // token per position, so the merged list is no longer than the document.
        events.Sort(static (x, y) => x.Position.CompareTo(y.Position));

        double best = _includeWholeDocument ? WholeDocumentScore(idf, positions) : 0;
        var counts = new int[terms.Count];

        for (int w = 0; w < _widths.Length; w++)
        {
            int width = _widths[w];
            double sweep = BestWindowScore(idf, events, counts, documentLength, width);

            if (sweep > best)
                best = sweep;
        }

        return best;
    }

    /// <summary>
    /// The whole document as one window: every term's full occurrence count, no sliding.
    /// </summary>
    /// <remarks>
    /// This is the branch that makes the type's central identity hold, so it computes the
    /// contribution exactly as <see cref="Bm25Scorer"/> computes it at <c>b = 0</c> — same idf, same
    /// numerator grouping, same normalization of 1 — rather than calling the other scorer, which
    /// would make the identity a tautology instead of a property under test.
    /// </remarks>
    private double WholeDocumentScore(double[] idf, IReadOnlyList<int>[] positions)
    {
        double total = 0;

        for (int i = 0; i < positions.Length; i++)
        {
            int termFrequency = positions[i].Count;

            if (termFrequency == 0)
                continue;

            total += idf[i] * termFrequency * _saturation / (termFrequency + (_k1 * 1.0));
        }

        return total;
    }

    /// <summary>
    /// The best window of one width, by a two-pointer sweep over the merged occurrences.
    /// </summary>
    /// <remarks>
    /// The window is <c>[start, min(start + width, documentLength))</c> and the starts advance by the
    /// stride, so the tail of a document whose length is not a multiple of the stride is still
    /// covered by the last, clamped window. Both pointers only move forward, which makes the sweep
    /// linear in the number of occurrences plus the windows times the number of query terms — and
    /// makes the counts exact rather than accumulated-and-approximated.
    /// </remarks>
    private double BestWindowScore(
        double[] idf, List<Occurrence> events, int[] counts, int documentLength, int width)
    {
        // Zeroed once per width: the sweep leaves every count back at zero by construction, since a
        // window that has passed an occurrence has also released it.
        Array.Clear(counts);

        int advance = _stride == 0 ? width : Math.Min(_stride, width);
        int lowest = 0;
        int highest = 0;
        double best = 0;

        for (int start = 0; start < documentLength; start += advance)
        {
            int end = Math.Min(start + width, documentLength);

            while (highest < events.Count && events[highest].Position < end)
            {
                counts[events[highest].Term]++;
                highest++;
            }

            while (lowest < highest && events[lowest].Position < start)
            {
                counts[events[lowest].Term]--;
                lowest++;
            }

            double window = 0;

            for (int t = 0; t < counts.Length; t++)
            {
                int termFrequency = counts[t];

                if (termFrequency == 0)
                    continue;

                window += idf[t] * termFrequency * _saturation / (termFrequency + (_k1 * 1.0));
            }

            if (window > best)
                best = window;
        }

        return best;
    }

    /// <summary>A query term's occurrence inside a document, for the merged sweep.</summary>
    private readonly record struct Occurrence(int Position, int Term);

    /// <summary>
    /// A name carrying the configuration, because two instances differing only in their widths are
    /// two different scorers and a cost sheet or a log line that cannot tell them apart has lost the
    /// thing it was for.
    /// </summary>
    private string BuildName()
    {
        var builder = new StringBuilder("WindowBM25");

        foreach (int width in _widths)
            builder.Append("/w").Append(width);

        if (_stride > 0)
            builder.Append("/stride=").Append(_stride);

        if (_includeWholeDocument)
            builder.Append("/whole");

        return builder.Append("/k1=").Append(_k1.ToString("0.###", CultureInfo.InvariantCulture)).ToString();
    }
}