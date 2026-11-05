namespace LexiSharp.Core;

/// <summary>
/// A spell corrector that weights substitutions by keyboard proximity: swapping two adjacent
/// keys costs less than swapping two distant ones, and transpositions of adjacent characters
/// cost less than two independent substitutions. This is what makes it useful for real typos
/// (fat-finger errors) rather than generic edit distance.
/// </summary>
/// <remarks>
/// <para>
/// The layout is a parameter, not a constant: QWERTY, AZERTY and BÉPO place the same letters
/// at different positions, so a corrector hard-coded to one layout silently mis-scores on the
/// others. Pass the layout that matches the user's keyboard.
/// </para>
/// <para>
/// The vocabulary is held as a snapshot taken at construction; rebuild the corrector when the
/// index changes substantially.
/// </para>
/// </remarks>
public sealed class KeyboardAwareTermCorrector : ITermCorrector
{
    private readonly IReadOnlyList<string> _vocabulary;
    private readonly IReadOnlyDictionary<char, (int Row, int Col)> _layout;
    private readonly double _maxDistance;

    /// <param name="vocabulary">The terms the index holds.</param>
    /// <param name="layout">Keyboard layout used to price substitutions.</param>
    /// <param name="maxDistance">Maximum weighted edit distance to consider (default 2.0).</param>
    public KeyboardAwareTermCorrector(
        IEnumerable<string> vocabulary,
        KeyboardLayout layout = KeyboardLayout.Qwerty,
        double maxDistance = 2.0)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary.Distinct(StringComparer.Ordinal).ToList();
        _layout = KeyboardLayouts.Get(layout);
        _maxDistance = Math.Max(0.5, maxDistance);
    }

    /// <inheritdoc />
    public string Name => "keyboard-aware";

    /// <inheritdoc />
    public IReadOnlyList<(string Term, double Weight)> Correct(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var results = new List<(string Term, double Weight)>();

        if (_vocabulary.Contains(term, StringComparer.Ordinal))
            results.Add((term, 1.0));

        foreach (var candidate in _vocabulary)
        {
            if (string.Equals(candidate, term, StringComparison.Ordinal))
                continue;

            double distance = WeightedDamerauLevenshtein(term, candidate);

            if (distance > 0 && distance <= _maxDistance)
                results.Add((candidate, 1.0 / (1.0 + distance)));
        }

        return results
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Term, StringComparer.Ordinal)
            .ToList();
    }

    private double WeightedDamerauLevenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Standard DP with weighted substitution and adjacent transposition.
        int m = a.Length, n = b.Length;
        double[,] d = new double[m + 1, n + 1];

        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                double subCost = a[i - 1] == b[j - 1]
                    ? 0.0
                    : SubstitutionCost(a[i - 1], b[j - 1]);

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1.0, d[i, j - 1] + 1.0),
                    d[i - 1, j - 1] + subCost);

                if (i > 1 && j > 1
                    && a[i - 1] == b[j - 2]
                    && a[i - 2] == b[j - 1])
                {
                    // Adjacent transposition: cheaper than two substitutions.
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1.0);
                }
            }
        }

        return d[m, n];
    }

    private double SubstitutionCost(char x, char y)
    {
        if (_layout.TryGetValue(char.ToLowerInvariant(x), out var px)
            && _layout.TryGetValue(char.ToLowerInvariant(y), out var py))
        {
            int manhattan = Math.Abs(px.Row - py.Row) + Math.Abs(px.Col - py.Col);
            // Adjacent key ~1.25, same key 0, distant up to 2.0.
            return Math.Min(2.0, 1.0 + 0.25 * manhattan);
        }

        // Unknown key: treat as a normal substitution.
        return 1.0;
    }
}

/// <summary>Keyboard layouts supported by <see cref="KeyboardAwareTermCorrector"/>.</summary>
public enum KeyboardLayout
{
    /// <summary>US QWERTY layout.</summary>
    Qwerty,
    /// <summary>French AZERTY layout.</summary>
    Azerty,
    /// <summary>French BÉPO layout.</summary>
    Bépo,
}

/// <summary>Key positions for the supported layouts.</summary>
public static class KeyboardLayouts
{
    private static readonly IReadOnlyDictionary<char, (int, int)> Qwerty = Build(new[]
    {
        "qwertyuiop",
        "asdfghjkl",
        "zxcvbnm",
    });

    private static readonly IReadOnlyDictionary<char, (int, int)> Azerty = Build(new[]
    {
        "azertyuiop",
        "qsdfghjklm",
        "wxcvbn",
    });

    private static readonly IReadOnlyDictionary<char, (int, int)> Bépo = Build(new[]
    {
        "bépoè^vdljq",
        "auie,ctsrnm",
        "àyx.k'wghf",
    });

    /// <summary>Returns the key positions for <paramref name="layout"/>.</summary>
    public static IReadOnlyDictionary<char, (int Row, int Col)> Get(KeyboardLayout layout) =>
        layout switch
        {
            KeyboardLayout.Azerty => Azerty,
            KeyboardLayout.Bépo => Bépo,
            _ => Qwerty,
        };

    private static IReadOnlyDictionary<char, (int, int)> Build(string[] rows)
    {
        var map = new Dictionary<char, (int, int)>();

        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
            {
                map[rows[r][c]] = (r, c);
            }
        }

        return map;
    }
}
