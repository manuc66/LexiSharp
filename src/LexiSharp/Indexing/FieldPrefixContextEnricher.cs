using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// A model-free <see cref="IChunkContextEnricher"/> that prefixes a document's text with the
/// values of named <see cref="SearchDocument.Fields"/> — the deterministic, offline cousin of
/// a context-injection LLM, and the one that makes this seam testable without a model. With
/// <c>fields: ["title", "chapter"]</c>, a chunk whose fields carry the report and section
/// headings is indexed as those headings followed by the chunk's own text, so a query can
/// reach the chunk through the context it does not spell out — "the benefit rose 12%" becomes
/// reachable via <i>acme 2024</i> when those are the field values.
/// </summary>
/// <remarks>
/// Fields are prepended in constructor order, each value once, space-separated, followed by
/// the original text; missing fields are skipped. A document carrying none of the named fields
/// (or no fields at all) is returned unchanged. The document's fields, text fields and id are
/// untouched — this enricher only rewrites <see cref="SearchDocument.Text"/>.
/// </remarks>
public sealed class FieldPrefixContextEnricher : IChunkContextEnricher
{
    private readonly string[] _fields;

    /// <summary>
    /// Creates an enricher that prefixes the values of <paramref name="fields"/>, in order.
    /// </summary>
    /// <param name="fields">Field names whose values to prepend; each must be non-blank and the
    /// set must be distinct.</param>
    /// <exception cref="ArgumentException"><paramref name="fields"/> is empty or holds a blank
    /// or duplicated entry.</exception>
    public FieldPrefixContextEnricher(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var names = new List<string>();

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field))
                throw new ArgumentException("Context field names must be non-blank.", nameof(fields));

            if (names.Contains(field, StringComparer.Ordinal))
                throw new ArgumentException($"Context field '{field}' is listed more than once.", nameof(fields));

            names.Add(field);
        }

        if (names.Count == 0)
            throw new ArgumentException("At least one context field is required.", nameof(fields));

        _fields = names.ToArray();
    }

    /// <inheritdoc />
    public string Name => $"prefix({string.Join(",", _fields)})";

    /// <inheritdoc />
    public SearchDocument Enrich(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Fields is not { Count: > 0 } fields)
            return document;

        var builder = new StringBuilder();
        bool any = false;

        foreach (var field in _fields)
        {
            if (!fields.TryGetValue(field, out var value))
                continue;

            if (any)
                builder.Append(' ');

            builder.Append(value);
            any = true;
        }

        if (!any)
            return document;

        builder.Append(' ');
        builder.Append(document.Text);

        return document with { Text = builder.ToString() };
    }
}