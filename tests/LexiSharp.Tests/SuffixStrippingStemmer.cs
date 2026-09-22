using LexiSharp.Linguistics;

namespace LexiSharp.Tests;

/// <summary>
/// Tiny deterministic stemmer used only by tests to exercise the <see cref="IStemmer"/> seam.
/// Strips a trailing "ing", "ed" or "s".
/// </summary>
internal sealed class SuffixStrippingStemmer : IStemmer
{
    public string Stem(string term)
    {
        if (term.EndsWith("ing", StringComparison.Ordinal))
            return term[..^3];
        if (term.EndsWith("ed", StringComparison.Ordinal))
            return term[..^2];
        if (term.EndsWith('s'))
            return term[..^1];
        return term;
    }
}