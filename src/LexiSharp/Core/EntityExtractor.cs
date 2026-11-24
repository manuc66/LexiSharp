using System.Text.RegularExpressions;

namespace LexiSharp.Core;

/// <summary>A candidate entity extracted from text.</summary>
/// <param name="Type">The entity type (e.g. <c>url</c>, <c>path</c>).</param>
/// <param name="Value">The matched text.</param>
/// <param name="Start">Start offset in the source text.</param>
/// <param name="Length">Length of the matched text.</param>
public sealed record ExtractedEntity(string Type, string Value, int Start, int Length);

/// <summary>The result of an extraction pass.</summary>
/// <param name="Entities">The extracted entities, in source order.</param>
public sealed record EntityExtractionResult(IReadOnlyList<ExtractedEntity> Entities);

/// <summary>Input to an extraction pass.</summary>
/// <param name="Text">The text to scan.</param>
public sealed record EntityExtractionRequest(string Text);

/// <summary>Pattern-based entity matcher.</summary>
public interface IEntityPattern
{
    /// <summary>Returns the entities matched by this pattern.</summary>
    IReadOnlyList<ExtractedEntity> Match(EntityExtractionRequest request);
}

/// <summary>Optional ML/NER seam for named entities.</summary>
public interface INamedEntityRecognizer
{
    /// <summary>Returns named entities recognized in <paramref name="text"/>.</summary>
    IReadOnlyList<ExtractedEntity> Recognize(string text);
}

/// <summary>
/// Generic entity extractor: runs configurable patterns (and an optional NER), then deduplicates
/// overlapping spans. No business-specific patterns are enabled by default.
/// </summary>
public sealed class EntityExtractor
{
    private readonly IReadOnlyList<IEntityPattern> _patterns;
    private readonly INamedEntityRecognizer? _ner;

    /// <summary>Creates an extractor over <paramref name="patterns"/> and an optional NER.</summary>
    public EntityExtractor(IEnumerable<IEntityPattern>? patterns = null, INamedEntityRecognizer? ner = null)
    {
        _patterns = (patterns ?? Array.Empty<IEntityPattern>()).ToList();
        if (_patterns.Any(pattern => pattern is null))
        {
            throw new ArgumentException("The pattern sequence contains a null entry.", nameof(patterns));
        }

        _ner = ner;
    }

    /// <summary>The patterns in use.</summary>
    public IReadOnlyList<IEntityPattern> Patterns => _patterns;

    /// <summary>Extracts entities from <paramref name="request"/>.</summary>
    public EntityExtractionResult Extract(EntityExtractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entities = new List<ExtractedEntity>();

        foreach (IEntityPattern pattern in _patterns)
        {
            entities.AddRange(pattern.Match(request));
        }

        if (_ner is not null)
        {
            entities.AddRange(_ner.Recognize(request.Text));
        }

        // Deduplicate overlapping spans, keeping the first (shortest first within the same start).
        var deduped = new List<ExtractedEntity>();
        foreach (ExtractedEntity entity in entities.OrderBy(e => e.Start).ThenBy(e => e.Length))
        {
            bool overlaps = deduped.Any(existing =>
                entity.Start < existing.Start + existing.Length &&
                existing.Start < entity.Start + entity.Length);
            if (!overlaps)
            {
                deduped.Add(entity);
            }
        }

        return new EntityExtractionResult(deduped);
    }

    /// <summary>Convenience overload for a raw text.</summary>
    public EntityExtractionResult Extract(string text) =>
        Extract(new EntityExtractionRequest(text));
}

/// <summary>Pattern backed by a regular expression.</summary>
public sealed class RegexEntityPattern : IEntityPattern
{
    private readonly Regex _regex;

    /// <summary>Creates a pattern of the given <paramref name="type"/> using <paramref name="pattern"/>.</summary>
    public RegexEntityPattern(string type, string pattern, RegexOptions options = RegexOptions.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        Type = type;
        _regex = new Regex(pattern, options | RegexOptions.Compiled);
    }

    /// <summary>The entity type produced.</summary>
    public string Type { get; }

    /// <inheritdoc />
    public IReadOnlyList<ExtractedEntity> Match(EntityExtractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<ExtractedEntity>();
        foreach (Match match in _regex.Matches(request.Text))
        {
            if (match.Success)
            {
                results.Add(new ExtractedEntity(Type, match.Value, match.Index, match.Length));
            }
        }

        return results;
    }
}

/// <summary>Generic built-in patterns that are not tied to any business domain.</summary>
public static class GenericEntityPatterns
{
    /// <summary>Matches http(s) URLs.</summary>
    public static IEntityPattern Url { get; } = new RegexEntityPattern(
        "url",
        @"https?://[^\s]+",
        RegexOptions.IgnoreCase);

    /// <summary>Matches file paths (Unix or Windows style).</summary>
    public static IEntityPattern FilePath { get; } = new RegexEntityPattern(
        "path",
        @"(?:[A-Za-z]:\\|/)[^\s]+");

    /// <summary>Matches domain-like names.</summary>
    public static IEntityPattern Domain { get; } = new RegexEntityPattern(
        "domain",
        @"\b(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}\b");
}
