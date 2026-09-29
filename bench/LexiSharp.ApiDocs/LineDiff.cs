namespace LexiSharp.ApiDocs;

/// <summary>
/// The difference between two versions of a line-oriented file, as added and removed lines.
/// </summary>
/// <remarks>
/// <para>
/// A longest-common-subsequence diff over whole lines, which is the right granularity for a
/// generated page: the interesting unit is a row that appeared or disappeared, not a character.
/// The matrix is filled from the end and walked backwards, so the first reported change is the
/// first one a reader would meet in the file rather than the last.
/// </para>
/// <para>
/// Quadratic in the number of lines, and that is a deliberate fit rather than an oversight: the
/// input is a Markdown page of a few hundred lines, and a linear algorithm would either be
/// approximate or would be more code than the number it reports is worth.
/// </para>
/// </remarks>
internal sealed class LineDiff
{
    private readonly string[] _committed;
    private readonly string[] _generated;
    private readonly int[,] _lengths;

    public LineDiff(string[] committed, string[] generated)
    {
        _committed = committed;
        _generated = generated;
        _lengths = new int[committed.Length + 1, generated.Length + 1];

        for (int i = committed.Length - 1; i >= 0; i--)
        {
            for (int j = generated.Length - 1; j >= 0; j--)
            {
                _lengths[i, j] = string.Equals(committed[i], generated[j], StringComparison.Ordinal)
                    ? _lengths[i + 1, j + 1] + 1
                    : Math.Max(_lengths[i + 1, j], _lengths[i, j + 1]);
            }
        }

        Walk();
    }

    /// <summary>Lines in the committed file that the generated one does not have.</summary>
    public int Removed { get; private set; }

    /// <summary>Lines in the generated file that the committed one does not have.</summary>
    public int Added { get; private set; }

    /// <summary>Whether the two files differ at all.</summary>
    public bool Changed => Removed > 0 || Added > 0;

    /// <summary>1-based line number where the first change is, or 0 when there is none.</summary>
    public int FirstChangedLine { get; private set; }

    /// <summary>The committed line at the first change, or an empty string.</summary>
    public string FirstCommitted { get; private set; } = "";

    /// <summary>The generated line at the first change, or an empty string.</summary>
    public string FirstGenerated { get; private set; } = "";

    private void Walk()
    {
        int i = 0;
        int j = 0;

        while (i < _committed.Length && j < _generated.Length)
        {
            if (string.Equals(_committed[i], _generated[j], StringComparison.Ordinal))
            {
                i++;
                j++;
                continue;
            }

            if (FirstChangedLine == 0)
            {
                FirstChangedLine = i + 1;
                FirstCommitted = Describe(_committed, i);
                FirstGenerated = Describe(_generated, j);
            }

            // Walk whichever side the common subsequence says is longer, so a removed block
            // counts once per line rather than once per shifted line.
            if (_lengths[i + 1, j] >= _lengths[i, j + 1])
            {
                Removed++;
                i++;
            }
            else
            {
                Added++;
                j++;
            }
        }

        if (FirstChangedLine == 0 && (i < _committed.Length || j < _generated.Length))
        {
            FirstChangedLine = i + 1;
            FirstCommitted = Describe(_committed, i);
            FirstGenerated = Describe(_generated, j);
        }

        Removed += _committed.Length - i;
        Added += _generated.Length - j;
    }

    private static string Describe(string[] lines, int index) =>
        index < lines.Length ? lines[index].TrimEnd() : "<end of file>";
}
