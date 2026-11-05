namespace LexiSharp.Core;

/// <summary>
/// Corrects a single query term — the query-side seam for spell correction, typo tolerance and
/// keyboard-aware matching. The vocabulary is a construction concern of the implementation, not
/// an argument of every call: a corrector is built once over the index's vocabulary and re-reads
/// it lazily, so it stays correct as the index grows and does not re-scan the dictionary per term.
/// </summary>
/// <remarks>
/// <para>
/// The seam is deliberately small: one term in, a ranked list of candidate replacements out. The
/// consuming decorator decides how to fuse the candidates with the original term (OR them, keep
/// the original as the primary, weight by distance). LexiSharp runs no model itself; a corrector
/// may be a Damerau-Levenshtein walk, a keyboard-distance table, a SymSpell lookup, or a
/// consumer-supplied model.
/// </para>
/// <para>
/// An empty result means "no correction" — the term is kept as-is. The original term, when it is
/// present in the vocabulary, should be returned first with the highest weight, so a corrector
/// never makes a correct term worse.
/// </para>
/// </remarks>
public interface ITermCorrector
{
    /// <summary>Human readable name of the strategy, e.g. <c>"damerau"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Returns candidate replacements for <paramref name="term"/>, ordered by descending weight.
    /// Empty when nothing differs from the input or no candidate is within reach.
    /// </summary>
    /// <param name="term">The raw term to correct.</param>
    IReadOnlyList<(string Term, double Weight)> Correct(string term);
}

/// <summary>
/// A corrector that returns the term unchanged — the safe default that never alters a query.
/// </summary>
public sealed class PassthroughTermCorrector : ITermCorrector
{
    /// <inheritdoc />
    public string Name => "passthrough";

    /// <inheritdoc />
    public IReadOnlyList<(string Term, double Weight)> Correct(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return new[] { (term, 1.0) };
    }
}

/// <summary>
/// A corrector based on Damerau-Levenshtein distance (insertion, deletion, substitution and
/// adjacent transposition), walked over the vocabulary. Distance 1 and 2 candidates are returned,
/// weighted by <c>1 / (1 + distance)</c>, with the original term first when it is in the
/// vocabulary.
/// </summary>
/// <remarks>
/// The vocabulary is held as a snapshot taken at construction; rebuild the corrector when the
/// index changes substantially. For a dynamic index, pass a lazy source via the overload.
/// </remarks>
public sealed class DamerauLevenshteinTermCorrector : ITermCorrector
{
    private readonly IReadOnlyList<string> _vocabulary;

    /// <param name="vocabulary">The terms the index holds.</param>
    /// <param name="maxDistance">Maximum edit distance to consider (default 2).</param>
    public DamerauLevenshteinTermCorrector(IEnumerable<string> vocabulary, int maxDistance = 2)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary.Distinct(StringComparer.Ordinal).ToList();
        MaxDistance = Math.Max(1, maxDistance);
    }

    /// <summary>Maximum edit distance considered.</summary>
    public int MaxDistance { get; }

    /// <inheritdoc />
    public string Name => "damerau-levenshtein";

    /// <inheritdoc />
    public IReadOnlyList<(string Term, double Weight)> Correct(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var results = new List<(string Term, double Weight)>();

        // Original term first, if present.
        if (_vocabulary.Contains(term, StringComparer.Ordinal))
            results.Add((term, 1.0));

        foreach (var candidate in _vocabulary)
        {
            if (string.Equals(candidate, term, StringComparison.Ordinal))
                continue;

            int distance = DamerauLevenshtein(term, candidate);

            if (distance > 0 && distance <= MaxDistance)
                results.Add((candidate, 1.0 / (1.0 + distance)));
        }

        return results
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Term, StringComparer.Ordinal)
            .ToList();
    }

    private static int DamerauLevenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[,] d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
