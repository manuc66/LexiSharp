using System.Runtime.InteropServices;
using LexiSharp.Core;

namespace LexiSharp.Ranking;

public sealed partial class RankedTextSearchEngine
{
    /// <summary>
    /// Per-field value counts over the documents that pass every match gate — independent of
    /// the Offset/Limit window. Built once per faceted search.
    /// </summary>
    /// <remarks>
    /// String values are counted as they arrive. Numeric values are parsed on the way in and their
    /// numbers buffered, because a histogram's boundaries are the match set's own minimum and
    /// maximum, which are only known once the pass is over. The buffer is one <see cref="double"/>
    /// per counted document per numeric facet, which is the price of deriving boundaries from the
    /// data in a single pass.
    /// </remarks>
    private sealed class FacetCollector
    {
        private readonly string[] _fields;
        private readonly Dictionary<string, int>[] _counts;
        private readonly NumericFacetRequest[] _numeric;
        private readonly List<double>[] _numericValues;
        private readonly int[] _unparseable;

        /// <summary>
        /// A collector for the requested facets, or <c>null</c> when neither kind was asked for —
        /// which is what keeps the document-reading paths free of a facet check.
        /// </summary>
        public static FacetCollector? Create(
            IReadOnlyList<string>? fields,
            IReadOnlyList<NumericFacetRequest>? numeric) =>
            fields is { Count: > 0 } || numeric is { Count: > 0 }
                ? new FacetCollector(fields, numeric)
                : null;

        private FacetCollector(
            IReadOnlyList<string>? fields,
            IReadOnlyList<NumericFacetRequest>? numeric)
        {
            _fields = DistinctFields(fields);
            _counts = new Dictionary<string, int>[_fields.Length];

            for (int i = 0; i < _fields.Length; i++)
                _counts[i] = new Dictionary<string, int>(StringComparer.Ordinal);

            var requests = new List<NumericFacetRequest>(numeric?.Count ?? 0);

            if (numeric is not null)
            {
                for (int i = 0; i < numeric.Count; i++)
                {
                    if (numeric[i] is not null)
                        requests.Add(numeric[i]);
                }
            }

            _numeric = requests.ToArray();
            _numericValues = new List<double>[_numeric.Length];
            _unparseable = new int[_numeric.Length];

            for (int i = 0; i < _numeric.Length; i++)
                _numericValues[i] = new List<double>();
        }

        private static string[] DistinctFields(IReadOnlyList<string>? fields)
        {
            if (fields is not { Count: > 0 })
                return Array.Empty<string>();

            var distinct = new List<string>(fields.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (!string.IsNullOrEmpty(field) && seen.Add(field))
                    distinct.Add(field);
            }

            return distinct.ToArray();
        }

        public void Count(SearchDocument document)
        {
            var documentFields = document.Fields;

            if (documentFields is null)
                return;

            for (int i = 0; i < _fields.Length; i++)
            {
                // A document missing the field simply does not count for it.
                if (!documentFields.TryGetValue(_fields[i], out var value) || value is null)
                    continue;

                var counts = _counts[i];
                counts.TryGetValue(value, out int current);
                counts[value] = current + 1;
            }

            for (int i = 0; i < _numeric.Length; i++)
            {
                // The same absence rule as the string facets: no field, no count — and no entry in
                // UnparseableCount either, because an absent value is not a malformed one.
                if (!documentFields.TryGetValue(_numeric[i].Field, out var value) || value is null)
                    continue;

                if (TryParseNumber(value, out double number))
                    _numericValues[i].Add(number);
                else
                    _unparseable[i]++;
            }
        }

        /// <summary>
        /// The numeric parse, deliberately the same call <see cref="MetadataFilter"/> compares with:
        /// <c>NumberStyles.Any</c> in the invariant culture. <see cref="double.NaN"/> and the
        /// infinities parse successfully and are still refused — a range has no place to put them.
        /// </summary>
        private static bool TryParseNumber(string value, out double number) =>
            double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);

        public IReadOnlyList<FacetBucket> Build()
        {
            var buckets = new List<FacetBucket>(_fields.Length);

            for (int i = 0; i < _fields.Length; i++)
            {
                // No counted document carries this field: no bucket.
                if (_counts[i].Count == 0)
                    continue;

                var values = new List<FacetValue>(_counts[i].Count);

                foreach (var pair in _counts[i])
                    values.Add(new FacetValue(pair.Key, pair.Value));

                values.Sort(static (a, b) =>
                {
                    int byCount = b.Count.CompareTo(a.Count);
                    return byCount != 0 ? byCount : string.CompareOrdinal(a.Value, b.Value);
                });

                buckets.Add(new FacetBucket(_fields[i], values));
            }

            return buckets;
        }

        public IReadOnlyList<NumericFacetBucket> BuildNumeric()
        {
            var buckets = new List<NumericFacetBucket>(_numeric.Length);

            for (int i = 0; i < _numeric.Length; i++)
            {
                // The string facets' rule, applied to the raw values: no counted document carries
                // the field, so there is nothing to report. A field that is carried but holds no
                // parseable value is reported, with no ranges and the count of what failed to
                // parse — the difference between "nobody has this" and "nobody has a number".
                if (_numericValues[i].Count == 0 && _unparseable[i] == 0)
                    continue;

                _numericValues[i].Sort();

                // An explicit request already knows its edges, so it reports them even when nothing
                // parsed; a histogram's edges come from the values, so with none there is nothing to
                // report.
                IReadOnlyList<NumericFacetRange> ranges = _numeric[i].IsHistogram
                    ? _numericValues[i].Count == 0
                        ? Array.Empty<NumericFacetRange>()
                        : Histogram(_numericValues[i], _numeric[i].BucketCount)
                    : ExplicitRanges(_numericValues[i], _numeric[i].Boundaries!);

                buckets.Add(new NumericFacetBucket(_numeric[i].Field, ranges, _unparseable[i]));
            }

            return buckets;
        }

        private static IReadOnlyList<NumericFacetRange> ExplicitRanges(
            List<double> sorted,
            IReadOnlyList<double> boundaries)
        {
            var ranges = new NumericFacetRange[boundaries.Count - 1];
            var values = CollectionsMarshal.AsSpan(sorted);

            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                // [from, to): lower_bound(from)..lower_bound(to) is exactly the values in the range.
                int count = LowerBound(values, boundaries[i + 1]) - LowerBound(values, boundaries[i]);
                ranges[i] = new NumericFacetRange(boundaries[i], boundaries[i + 1], count);
            }

            return ranges;
        }

        private static IReadOnlyList<NumericFacetRange> Histogram(List<double> sorted, int bucketCount)
        {
            var values = CollectionsMarshal.AsSpan(sorted);
            double min = values[0];
            double max = values[^1];

            // Every value identical: one point bucket, inclusive on both sides. Dividing (max - min)
            // by the bucket count here would be a division by zero, and [x, x) would count nothing.
            // Exact equality on purpose: the sorted extremes are identical only when every value is,
            // and a tolerance would collapse a narrow but real spread into that single bucket.
            if (min == max) // NOSONAR:S1244
                return new[] { new NumericFacetRange(min, max, values.Length, UpperInclusive: true) };

            var edges = new double[bucketCount + 1];
            double width = (max - min) / bucketCount;

            edges[0] = min;

            for (int i = 1; i < bucketCount; i++)
            {
                // Clamped rather than computed straight: (max - min) / bucketCount can round such
                // that the last interior edge lands on or past max, which would report a range whose
                // From is greater than its To. Non-decreasing is all this array has to be.
                edges[i] = Math.Clamp(min + i * width, edges[i - 1], max);
            }

            edges[bucketCount] = max;

            var counts = new int[bucketCount];

            for (int i = 0; i < values.Length; i++)
            {
                // The bucket whose From is the last edge at or below the value: upper_bound - 1 is
                // exactly the [from, to) membership rule, and the clamp puts a value equal to max in
                // the last bucket, which is the one closed at the top.
                int index = Math.Clamp(UpperBound(edges, values[i]) - 1, 0, bucketCount - 1);
                counts[index]++;
            }

            var ranges = new NumericFacetRange[bucketCount];

            for (int i = 0; i < bucketCount; i++)
                ranges[i] = new NumericFacetRange(edges[i], edges[i + 1], counts[i], i == bucketCount - 1);

            return ranges;
        }

        /// <summary>Index of the first element that is not less than <paramref name="key"/>.</summary>
        private static int LowerBound(ReadOnlySpan<double> values, double key)
        {
            int low = 0;
            int high = values.Length;

            while (low < high)
            {
                int middle = low + ((high - low) >> 1);

                if (values[middle] < key)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        /// <summary>Index of the first element that is greater than <paramref name="key"/>.</summary>
        private static int UpperBound(ReadOnlySpan<double> values, double key)
        {
            int low = 0;
            int high = values.Length;

            while (low < high)
            {
                int middle = low + ((high - low) >> 1);

                if (values[middle] <= key)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }
}
