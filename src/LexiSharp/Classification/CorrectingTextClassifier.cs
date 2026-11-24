using System.Text;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// <see cref="ITextClassifier"/> decorator that corrects selected unknown terms before
/// classifying, predicting on both the original and the corrected text and keeping the corrected
/// result only when it gains enough confidence.
/// </summary>
/// <remarks>
/// <para>
/// Unknown does not mean misspelled. By default only alphabetic tokens of at least
/// <c>minimumLength</c> characters are candidates, and a token embedded in a path, URL or
/// identifier (adjacent to <c>/ \ ~ . : - _</c>) is left alone. A caller with a domain-specific
/// notion of "correctable" can supply its own predicate.
/// </para>
/// <para>
/// Corrections replace the token at its source span, so punctuation, separators and casing of the
/// rest of the text are preserved. The corrected prediction must beat the original by
/// <c>minimumGain</c> (default 0.05) to be used.
/// </para>
/// </remarks>
public sealed class CorrectingTextClassifier : ITextClassifier
{
    private static readonly char[] IdentifierSeparators = ['/', '\\', '~', '.', ':', '-', '_'];

    private readonly ITextClassifier _inner;
    private readonly ITermCorrector _corrector;
    private readonly ISpanTokenizer _tokenizer;
    private readonly IReadOnlySet<string> _vocabulary;
    private readonly Func<string, bool> _correctable;
    private readonly double _minimumGain;
    private readonly bool _excludeIdentifiers;

    /// <summary>Wraps <paramref name="inner"/> with correction over <paramref name="vocabulary"/>.</summary>
    public CorrectingTextClassifier(
        ITextClassifier inner,
        ITermCorrector corrector,
        IEnumerable<string> vocabulary,
        ISpanTokenizer? tokenizer = null,
        int minimumLength = 4,
        double minimumGain = 0.05,
        Func<string, bool>? correctable = null,
        bool excludeIdentifiers = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(corrector);
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (minimumLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength), minimumLength, "Must be positive.");
        }

        if (!double.IsFinite(minimumGain) || minimumGain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGain), minimumGain, "Must be non-negative.");
        }

        _inner = inner;
        _corrector = corrector;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _vocabulary = new HashSet<string>(vocabulary, StringComparer.Ordinal);
        _correctable = correctable ?? (term =>
            term.Length >= minimumLength && term.All(char.IsLetter));
        _minimumGain = minimumGain;
        _excludeIdentifiers = excludeIdentifiers;
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        (string corrected, bool changed) = Correct(text);
        if (!changed)
        {
            return _inner.Predict(text, limit, excludedCategories);
        }

        IReadOnlyList<ClassificationResult> originalResults = _inner.Predict(text, limit, excludedCategories);
        IReadOnlyList<ClassificationResult> correctedResults = _inner.Predict(corrected, limit, excludedCategories);

        if (correctedResults.Count == 0)
        {
            return originalResults;
        }

        if (originalResults.Count == 0)
        {
            return correctedResults;
        }

        return correctedResults[0].Probability >= originalResults[0].Probability + _minimumGain
            ? correctedResults
            : originalResults;
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private (string Text, bool Changed) Correct(string text)
    {
        IReadOnlyList<TokenSpan> spans = _tokenizer.TokenizeWithSpans(text);
        var builder = new StringBuilder(text.Length);
        int cursor = 0;
        bool changed = false;

        foreach (TokenSpan span in spans)
        {
            // N-gram tokenizers emit overlapping spans; only a span that starts at or after the
            // cursor can be replaced without rewriting text an earlier span already covered.
            if (span.Start < cursor)
            {
                continue;
            }

            builder.Append(text, cursor, span.Start - cursor);
            string replacement = span.Term;

            if (!_vocabulary.Contains(span.Term)
                && _correctable(span.Term)
                && (!_excludeIdentifiers || !IsEmbeddedInIdentifier(text, span)))
            {
                IReadOnlyList<(string Term, double Weight)> candidates = _corrector.Correct(span.Term);
                if (candidates.Count > 0
                    && !string.Equals(candidates[0].Term, span.Term, StringComparison.Ordinal))
                {
                    replacement = ApplyCase(text.AsSpan(span.Start, span.Length), candidates[0].Term);
                    changed = true;
                }
            }

            builder.Append(replacement);
            cursor = span.Start + span.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return (builder.ToString(), changed);
    }

    private static bool IsEmbeddedInIdentifier(string text, TokenSpan span)
    {
        int before = span.Start - 1;
        int after = span.Start + span.Length;

        return (before >= 0 && Array.IndexOf(IdentifierSeparators, text[before]) >= 0)
            || (after < text.Length && Array.IndexOf(IdentifierSeparators, text[after]) >= 0);
    }

    private static string ApplyCase(ReadOnlySpan<char> original, string replacement)
    {
        if (original.Length == 0 || replacement.Length == 0)
        {
            return replacement;
        }

        bool allUpper = true;
        foreach (char c in original)
        {
            if (char.IsLetter(c) && !char.IsUpper(c))
            {
                allUpper = false;
                break;
            }
        }

        if (allUpper && original.Length > 1)
        {
            return replacement.ToUpperInvariant();
        }

        if (char.IsUpper(original[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }
}
