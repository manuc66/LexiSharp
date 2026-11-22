using LexiSharp.Core;

namespace LexiSharp.Linguistics;

/// <summary>
/// <see cref="ITokenizer"/> decorator that canonicalizes terms using a <see cref="SynonymMap"/>:
/// every member of an equivalence group (and the source of a one-way edge) is rewritten to a
/// single representative term, so downstream classifiers and indexes see one form instead of
/// several. Terms with no synonym pass through unchanged.
/// </summary>
/// <remarks>
/// The map is resolved with the wrapped tokenizer at construction time, exactly as
/// <c>RankedTextSearchEngine</c> does: each entry must tokenize to exactly one term. The decorator
/// is immutable once built and safe for concurrent use.
/// </remarks>
public sealed class SynonymTokenizer : ITokenizer
{
    private readonly ITokenizer _inner;
    private readonly Dictionary<string, string> _canonical;

    /// <summary>Wraps <paramref name="inner"/> and canonicalizes its output with <paramref name="synonyms"/>.</summary>
    /// <param name="inner">The tokenizer whose output is canonicalized.</param>
    /// <param name="synonyms">The synonym map; null or empty means identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    /// <exception cref="ArgumentException">A synonym entry does not tokenize to exactly one term.</exception>
    public SynonymTokenizer(ITokenizer inner, SynonymMap? synonyms)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _canonical = BuildCanonical(inner, synonyms);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(string text)
    {
        IReadOnlyList<string> tokens = _inner.Tokenize(text);
        if (_canonical.Count == 0 || tokens.Count == 0)
            return tokens;

        var result = new string[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
            result[i] = _canonical.TryGetValue(tokens[i], out string? canonical) ? canonical : tokens[i];

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(ReadOnlySpan<char> text)
    {
        IReadOnlyList<string> tokens = _inner.Tokenize(text);
        if (_canonical.Count == 0 || tokens.Count == 0)
            return tokens;

        var result = new string[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
            result[i] = _canonical.TryGetValue(tokens[i], out string? canonical) ? canonical : tokens[i];

        return result;
    }

    /// <inheritdoc />
    public void TokenizeInto(ReadOnlySpan<char> text, List<string> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        IReadOnlyList<string> tokens = _inner.Tokenize(text);

        foreach (string token in tokens)
        {
            destination.Add(_canonical.TryGetValue(token, out string? canonical) ? canonical : token);
        }
    }

    /// <summary>The wrapped tokenizer.</summary>
    public ITokenizer Inner => _inner;

    /// <summary>The canonical map applied by this tokenizer.</summary>
    public IReadOnlyDictionary<string, string> CanonicalMap => _canonical;

    private static Dictionary<string, string> BuildCanonical(ITokenizer tokenizer, SynonymMap? synonyms)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);

        if (synonyms is null || synonyms.IsEmpty)
            return canonical;

        // One-way edges: source -> target (target is the canonical form).
        foreach (var (source, target) in synonyms.OneWay)
        {
            string sourceTerm = RequireSingleTerm(tokenizer, source);
            string targetTerm = RequireSingleTerm(tokenizer, target);
            canonical[sourceTerm] = targetTerm;
        }

        // Equivalence groups: first member (registration order) is the canonical form.
        foreach (string[] group in synonyms.Groups)
        {
            var members = new string[group.Length];

            for (int i = 0; i < group.Length; i++)
                members[i] = RequireSingleTerm(tokenizer, group[i]);

            string representative = members[0];

            for (int i = 0; i < members.Length; i++)
                canonical[members[i]] = representative;
        }

        return canonical;
    }

    private static string RequireSingleTerm(ITokenizer tokenizer, string entry)
    {
        IReadOnlyList<string> terms = tokenizer.Tokenize(entry);

        if (terms.Count != 1)
        {
            throw new ArgumentException(
                $"Synonym entries must tokenize to exactly one term, but '{entry}' produced {terms.Count}.",
                nameof(entry));
        }

        return terms[0];
    }
}
