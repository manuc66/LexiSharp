using System.Globalization;
using System.Text;

namespace LexiSharp.Cli;

/// <summary>
/// Renders a string the tool did not author as one line a terminal can be handed: control and
/// format characters removed, line structure widened to one space, and the result cut at a length.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the corpus is input.</b> The tool prints document text, document ids and query text to a
/// console, and all three come from a corpus directory or an argument — a transcript, a scraped page,
/// a BEIR file. A terminal is an interpreter: <c>ESC[2J</c> clears the screen, <c>ESC]52;c;…</c>
/// writes the clipboard, <c>ESC]8;;…</c> turns the rest of the line into a link, and the C1 introducers
/// do the same in 8-bit mode. None of that is visible in the output, which is what makes it worse than
/// garbage: the reader sees a clean page.
/// </para>
/// <para>
/// <b>And why format characters, not only controls.</b> A right-to-left override (U+202E) makes
/// <c>gpj.exe</c> read as <c>exe.jpg</c>; a zero-width space (U+200B) makes two different strings look
/// identical. The reader's problem is the same one — text that says one thing and shows another — so
/// the same fix applies: a character with no visible glyph has no business in a one-line rendering of
/// untrusted text. Emoji joiners go with them; a family emoji may render as its members, which is a
/// cosmetic loss rather than a misleading page.
/// </para>
/// <para>
/// This is a rendering rule for the console front end, not a library contract: the engines return the
/// text they were given, because a caller writing to JSON or to a log has different rules. Emoji,
/// accents and every printable script pass through unchanged.
/// </para>
/// </remarks>
internal static class ConsoleText
{
    /// <summary>
    /// One line for the console: <paramref name="text"/> without invisible characters, at most
    /// <paramref name="maxLength"/> UTF-16 units, ending in <c>…</c> when it was cut, and never
    /// splitting a surrogate pair.
    /// </summary>
    /// <param name="text">The text to render.</param>
    /// <param name="maxLength">The longest rendering allowed; the default is no limit.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    public static string Line(string text, int maxLength = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        if (maxLength == 0)
            return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        bool truncated = false;

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (IsLineStructure(rune))
            {
                // Line structure becomes one space, so `a\r\nb` renders as `a b` rather than `ab`,
                // and a run of blank lines does not become a run of spaces.
                if (builder.Length > 0 && builder[^1] == ' ')
                    continue;

                if (!TryAppend(builder, " ", maxLength))
                {
                    truncated = true;
                    break;
                }

                continue;
            }

            if (IsInvisible(rune))
                continue;

            if (!TryAppend(builder, rune.ToString(), maxLength))
            {
                truncated = true;
                break;
            }
        }

        if (!truncated)
            return builder.ToString();

        // The ellipsis is part of the budget, and backing off is by whole rune: removing one char
        // from a surrogate pair would leave half an emoji.
        while (builder.Length > 0 && builder.Length + 1 > maxLength)
            builder.Length -= char.IsLowSurrogate(builder[^1]) ? 2 : 1;

        if (builder.Length + 1 > maxLength)
            return string.Empty;

        return builder.Append('…').ToString();
    }

    private static bool TryAppend(StringBuilder builder, string value, int maxLength)
    {
        if (builder.Length + value.Length > maxLength)
            return false;

        builder.Append(value);
        return true;
    }

    /// <summary>
    /// Whether the character ends a line or separates a word — the characters a one-line rendering
    /// turns into a space so the words on either side stay apart.
    /// </summary>
    private static bool IsLineStructure(Rune rune) => rune.Value switch
    {
        '\t' or '\n' or '\r' or '\u0085' or '\u2028' or '\u2029' => true,
        _ => false,
    };

    /// <summary>
    /// Whether the character has no visible glyph of its own: the controls (ESC among them), the
    /// format characters (bidi overrides, zero-width joiners and spaces, tag characters) and the two
    /// separators that end a line in some consumers.
    /// </summary>
    private static bool IsInvisible(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control => true,
        UnicodeCategory.Format => true,
        UnicodeCategory.LineSeparator => true,
        UnicodeCategory.ParagraphSeparator => true,
        _ => false,
    };
}
