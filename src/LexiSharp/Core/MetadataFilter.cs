namespace LexiSharp.Core;

/// <summary>Comparison applied by a <see cref="MetadataFilter"/>.</summary>
public enum MetadataFilterOperator
{
    /// <summary>Field value strictly equals the filter value.</summary>
    Equal,

    /// <summary>
    /// Negation of <see cref="Equal"/>: the field is absent <b>or</b> its value differs.
    /// </summary>
    NotEqual,

    /// <summary>Field value contains the filter value as a substring.</summary>
    Contains,

    /// <summary>
    /// Field value is greater than the filter value — numerically when both sides parse as
    /// numbers (invariant culture), otherwise by ordinal string comparison.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Field value is less than the filter value — numerically when both sides parse as numbers
    /// (invariant culture), otherwise by ordinal string comparison.
    /// </summary>
    LessThan,
}

/// <summary>
/// A declarative, single-field predicate over <see cref="SearchDocument.Fields"/> — the raw
/// material of structured filtering (e.g. <c>category = article</c>, <c>year &gt; 2023</c>,
/// <c>tags</c> containing <c>nlp</c>).
/// </summary>
/// <remarks>
/// A document that does not carry the field fails every operator except <see cref="MetadataFilterOperator.NotEqual"/>
/// (an absent field is not equal to anything, so it passes an exclusion). Several filters
/// combined in <see cref="SearchOptions.Filters"/> are AND-ed together. All comparisons are
/// culture-invariant and case-sensitive.
/// </remarks>
public sealed record MetadataFilter
{
    /// <summary>Name of the document field the filter applies to.</summary>
    public string Field { get; }

    /// <summary>Comparison applied between the field value and <see cref="Value"/>.</summary>
    public MetadataFilterOperator Operator { get; }

    /// <summary>Value to compare against (never null; empty strings are allowed).</summary>
    public string Value { get; }

    /// <summary>Creates a filter over one document field.</summary>
    /// <param name="field">Name of the document field the filter applies to.</param>
    /// <param name="operator">Comparison applied between the field value and <paramref name="value"/>.</param>
    /// <param name="value">Value to compare against (empty strings are allowed).</param>
    public MetadataFilter(string field, MetadataFilterOperator @operator, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(value);

        Field = field;
        Operator = @operator;
        Value = value;
    }

    /// <summary>
    /// Evaluates the filter against a document.
    /// </summary>
    /// <param name="document">The document to test.</param>
    /// <returns><c>true</c> when the document satisfies the filter.</returns>
    public bool Matches(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var fields = document.Fields;

        if (fields is null || !fields.TryGetValue(Field, out var fieldValue))
            return Operator == MetadataFilterOperator.NotEqual;

        return Operator switch
        {
            MetadataFilterOperator.Equal => string.Equals(fieldValue, Value, StringComparison.Ordinal),
            MetadataFilterOperator.NotEqual => !string.Equals(fieldValue, Value, StringComparison.Ordinal),
            MetadataFilterOperator.Contains => fieldValue.Contains(Value, StringComparison.Ordinal),
            MetadataFilterOperator.GreaterThan => Compare(fieldValue, Value) > 0,
            MetadataFilterOperator.LessThan => Compare(fieldValue, Value) < 0,
            _ => throw new InvalidOperationException($"Unknown filter operator {Operator}."),
        };
    }

    private static int Compare(string left, string right)
    {
        if (double.TryParse(left, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var leftNumber)
            && double.TryParse(right, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.CompareOrdinal(left, right);
    }
}
