namespace LexiSharp.Core;

/// <summary>
/// One numeric facet over <see cref="SearchDocument.Fields"/>: either explicit boundaries, or a
/// bucket count whose boundaries are derived from the match set — a histogram.
/// </summary>
/// <remarks>
/// <para>
/// A field value is read as a number with <c>double.TryParse</c>, <c>NumberStyles.Any</c> and the
/// invariant culture — the same parse <see cref="MetadataFilter"/> uses for its numeric
/// comparisons — and the result must be finite. A document that does not carry the field does not
/// count, exactly as for the string facets. A value that does not parse is <b>not</b> placed in a
/// range: it increments <see cref="NumericFacetBucket.UnparseableCount"/> instead, so the number is
/// reported rather than dropped.
/// </para>
/// <para>
/// <see cref="MetadataFilter"/> falls back to an ordinal string comparison when a value is not a
/// number, because a filter still has to answer <c>year &gt; 2023</c> on a field that holds the odd
/// <c>n/a</c>. A range facet does not: its edges are numbers, and a value that is not one has no
/// position among them to put it in. Folding it into the first or the last bucket would invent a
/// position, which is why it is counted apart.
/// </para>
/// </remarks>
public sealed record NumericFacetRequest
{
    /// <summary>Name of the document field the facet counts.</summary>
    public string Field { get; }

    /// <summary>
    /// The ascending boundaries of an explicit request, or <c>null</c> for a histogram. Each
    /// consecutive pair is one range, so <c>n</c> boundaries give <c>n - 1</c> ranges.
    /// </summary>
    public IReadOnlyList<double>? Boundaries { get; }

    /// <summary>Number of derived buckets for a histogram; <c>0</c> for an explicit request.</summary>
    public int BucketCount { get; }

    /// <summary>Whether this request derives its boundaries from the match set.</summary>
    public bool IsHistogram => BucketCount > 0;

    private NumericFacetRequest(string field, IReadOnlyList<double>? boundaries, int bucketCount)
    {
        Field = field;
        Boundaries = boundaries;
        BucketCount = bucketCount;
    }

    /// <summary>
    /// Counts the parseable values of one field into the ranges consecutive
    /// <paramref name="boundaries"/> delimit: <c>[b0, b1)</c>, <c>[b1, b2)</c>, and so on.
    /// </summary>
    /// <remarks>
    /// Each range includes its lower boundary and excludes its upper one. A value below the first
    /// boundary or at or above the last one falls in none of them and is not counted in this bucket
    /// at all — choosing the boundaries is how a caller says what the facet covers, and the reported
    /// ranges make the uncovered part visible. Empty ranges between boundaries are reported with a
    /// count of zero; leaving a gap out of the boundaries is what merges two ranges.
    /// </remarks>
    /// <param name="field">Name of the document field to count. Not null, empty or whitespace.</param>
    /// <param name="boundaries">
    /// At least two finite, strictly ascending boundaries. They are copied into the request, so
    /// later mutation of the argument does not change it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="field"/> is blank, or <paramref name="boundaries"/> has fewer than two
    /// entries, holds a non-finite value, or is not strictly ascending.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="boundaries"/> is null.</exception>
    public static NumericFacetRequest Ranges(string field, IReadOnlyList<double> boundaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(boundaries);

        if (boundaries.Count < 2)
        {
            throw new ArgumentException(
                "A range facet needs at least two boundaries: one range is one pair, and fewer than two leaves nothing to count.",
                nameof(boundaries));
        }

        var copy = new double[boundaries.Count];

        for (int i = 0; i < copy.Length; i++)
        {
            double boundary = boundaries[i];

            if (!double.IsFinite(boundary))
            {
                throw new ArgumentException(
                    $"Boundary {i} is {boundary}; a range edge must be a finite number.",
                    nameof(boundaries));
            }

            if (i > 0 && boundary <= copy[i - 1])
            {
                throw new ArgumentException(
                    $"Boundary {i} ({boundary}) is not greater than boundary {i - 1} ({copy[i - 1]}); boundaries must be strictly ascending.",
                    nameof(boundaries));
            }

            copy[i] = boundary;
        }

        return new NumericFacetRequest(field, copy, 0);
    }

    /// <summary>
    /// Derives <paramref name="bucketCount"/> equal-width buckets from the smallest and largest
    /// parseable values of one field over the match set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Boundaries are <c>min + i * (max - min) / bucketCount</c>, and the last bucket is closed at
    /// the top (<c>[from, to]</c>) so the match set's own maximum is counted in it; every other
    /// bucket, and every explicit range, is <c>[from, to)</c>. When every value is identical the
    /// width is zero and the facet reports one bucket whose <c>From</c> equals its <c>To</c>, with
    /// <see cref="NumericFacetRange.UpperInclusive"/> set, rather than dividing by zero or dropping
    /// the match set.
    /// </para>
    /// <para>
    /// The buckets are derived from the values, not from a rounded grid: two calls over the same
    /// match set produce identical edges. Empty buckets are reported with a count of zero, so a
    /// histogram of <c>n</c> buckets is <c>n</c> ranges whenever the match set holds at least one
    /// parseable value and the width is positive — including when <paramref name="bucketCount"/>
    /// exceeds the number of distinct values.
    /// </para>
    /// </remarks>
    /// <param name="field">Name of the document field to count. Not null, empty or whitespace.</param>
    /// <param name="bucketCount">Number of buckets; at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="field"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bucketCount"/> is less than one.</exception>
    public static NumericFacetRequest Histogram(string field, int bucketCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);

        return new NumericFacetRequest(field, null, bucketCount);
    }
}

/// <summary>
/// The numeric facet counts for one requested field: its ranges, in requested order, plus the
/// values that could not be read as numbers.
/// </summary>
/// <param name="Field">The requested field name.</param>
/// <param name="Ranges">
/// The buckets, in boundary order. An explicit request reports every range it asked for, empty ones
/// included; a histogram whose field holds no parseable value has no edges to derive and reports an
/// empty list rather than an invented range.
/// </param>
/// <param name="UnparseableCount">
/// Counted documents whose value for <paramref name="Field"/> is present and is not a finite
/// number. They are in no range; this is where they went.
/// </param>
public sealed record NumericFacetBucket(
    string Field,
    IReadOnlyList<NumericFacetRange> Ranges,
    int UnparseableCount);

/// <summary>
/// One numeric bucket: how many counted documents fall between <c>From</c> and <c>To</c> — in
/// <c>[From, To)</c>, or in <c>[From, To]</c> when <see cref="UpperInclusive"/> is set.
/// </summary>
/// <param name="From">Lower boundary, always included.</param>
/// <param name="To">
/// Upper boundary, excluded unless <paramref name="UpperInclusive"/> is set. Equal to
/// <paramref name="From"/> in the zero-width case — every value identical — where the single
/// bucket is the point itself.
/// </param>
/// <param name="Count">Number of counted documents in the bucket.</param>
/// <param name="UpperInclusive">
/// Whether <paramref name="To"/> itself belongs to this bucket. <c>true</c> only for the last
/// bucket of a derived histogram, which is closed at the top so the match set's maximum is counted;
/// explicit ranges are always <c>[From, To)</c>.
/// </param>
public sealed record NumericFacetRange(
    double From,
    double To,
    int Count,
    bool UpperInclusive = false);
