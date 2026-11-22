using System.Text;

namespace LexiSharp.Linguistics;

/// <summary>
/// The Unicode character classes the reference word tokenizer tests directly, which the base class
/// library does not expose: the emoji property, the ideographic script and the word-continuing
/// format class.
/// </summary>
/// <remarks>
/// <para>
/// The tables are pinned to Unicode 12.1 on purpose. The tokenizer this library reproduces — Lucene
/// 10.5.2's <c>StandardTokenizer</c> — is generated from a grammar declared as <c>%unicode 12.1</c>,
/// so its answers are the 12.1 answers even where a later Unicode version reassigns a code point.
/// Two of those reassignments are visible here: U+0600..U+0605 are <c>Format</c> in 12.1 and become
/// <c>Prepend</c> later, and the <c>Extended_Pictographic</c> property was laid out over reserved
/// ranges that later shrank. Using the newer tables would part company with the reference on
/// characters assigned after 12.1 — rare in a corpus, but not impossible in a URL or a name.
/// </para>
/// <para>
/// This is deliberately not a general-purpose Unicode classification API: it exists so that
/// <see cref="WordSegmentation.UnicodeWordBoundaries"/> can emit the same tokens as that grammar.
/// The English and identifier analyses never call it.
/// </para>
/// </remarks>
internal static class WordBreakClasses
{
    /// <summary>
    /// Whether <paramref name="rune"/> carries the <c>Extended_Pictographic</c> property — the set
    /// the reference grammar starts an emoji token on. Not every <c>OtherSymbol</c> is emoji:
    /// <c>©</c> and <c>™</c> are, while <c>°</c> and <c>■</c> are not, and the reference drops the
    /// latter where it emits the former.
    /// </summary>
    public static bool IsEmoji(Rune rune)
    {
        if (rune.Value < 0xA9)
            return false;

        return InRanges(rune.Value, EmojiRanges);
    }

    /// <summary>
    /// The <c>Extended_Pictographic</c> code point ranges of Unicode 12.1, as adjacent
    /// <c>[start, end]</c> pairs sorted ascending.
    /// </summary>
    private static readonly int[] EmojiRanges =
    [
        0xA9, 0xA9, 0xAE, 0xAE, 0x203C, 0x203C, 0x2049, 0x2049, 0x2122, 0x2122, 0x2139, 0x2139, 0x2194, 0x2199, 0x21A9, 0x21AA,
        0x231A, 0x231B, 0x2328, 0x2328, 0x2388, 0x2388, 0x23CF, 0x23CF, 0x23E9, 0x23F3, 0x23F8, 0x23FA, 0x24C2, 0x24C2, 0x25AA, 0x25AB,
        0x25B6, 0x25B6, 0x25C0, 0x25C0, 0x25FB, 0x25FE, 0x2600, 0x2605, 0x2607, 0x2612, 0x2614, 0x2685, 0x2690, 0x2705, 0x2708, 0x2712,
        0x2714, 0x2714, 0x2716, 0x2716, 0x271D, 0x271D, 0x2721, 0x2721, 0x2728, 0x2728, 0x2733, 0x2734, 0x2744, 0x2744, 0x2747, 0x2747,
        0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2763, 0x2767, 0x2795, 0x2797, 0x27A1, 0x27A1, 0x27B0, 0x27B0,
        0x27BF, 0x27BF, 0x2934, 0x2935, 0x2B05, 0x2B07, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x3030, 0x3030, 0x303D, 0x303D,
        0x3297, 0x3297, 0x3299, 0x3299, 0x1F000, 0x1F0FF, 0x1F10D, 0x1F10F, 0x1F12F, 0x1F12F, 0x1F16C, 0x1F171, 0x1F17E, 0x1F17F, 0x1F18E, 0x1F18E,
        0x1F191, 0x1F19A, 0x1F1AD, 0x1F1E5, 0x1F201, 0x1F20F, 0x1F21A, 0x1F21A, 0x1F22F, 0x1F22F, 0x1F232, 0x1F23A, 0x1F23C, 0x1F23F, 0x1F249, 0x1F3FA,
        0x1F400, 0x1F53D, 0x1F546, 0x1F64F, 0x1F680, 0x1F6FF, 0x1F774, 0x1F77F, 0x1F7D5, 0x1F7FF, 0x1F80C, 0x1F80F, 0x1F848, 0x1F84F, 0x1F85A, 0x1F85F,
        0x1F888, 0x1F88F, 0x1F8AE, 0x1F8FF, 0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1FFFD,
    ];

    /// <summary>
    /// Whether <paramref name="rune"/> is a CJK ideograph: a character with the <c>Han</c> script
    /// that the reference grammar tokenizes one code point at a time.
    /// </summary>
    /// <remarks>
    /// The exceptions are the script's word letters: U+3005 and U+303B, the ideographic iteration
    /// marks, are <c>Script=Han</c> but <c>Word_Break=ALetter</c>, so the reference folds them into
    /// an alphanumeric run instead of emitting them alone. U+3007, the ideographic number zero,
    /// stays here because it is <c>Word_Break=Other</c>.
    /// </remarks>
    public static bool IsIdeographic(Rune rune)
    {
        if (rune.Value < 0x2E80)
            return false;

        return InRanges(rune.Value, IdeographicRanges);
    }

    /// <summary>
    /// The <c>Script=Han</c> code points of Unicode 12.1 that are not word letters, as adjacent
    /// <c>[start, end]</c> pairs sorted ascending.
    /// </summary>
    private static readonly int[] IdeographicRanges =
    [
        0x2E80, 0x2E99, 0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x3007, 0x3007, 0x3021, 0x3029, 0x3038, 0x303A, 0x3400, 0x4DB5, 0x4E00, 0x9FEF,
        0xF900, 0xFA6D, 0xFA70, 0xFAD9, 0x20000, 0x2A6D6, 0x2A700, 0x2B734, 0x2B740, 0x2B81D, 0x2B820, 0x2CEA1, 0x2CEB0, 0x2EBE0, 0x2F800, 0x2FA1D,
    ];

    /// <summary>
    /// Whether <paramref name="rune"/> is a Hiragana code point: a character whose
    /// <c>Word_Break</c> class is <c>Hiragana</c>, which the reference grammar emits one code point
    /// at a time, unlike <c>Katakana</c>, which it keeps in a run.
    /// </summary>
    /// <remarks>
    /// Measured against the reference: <c>ひらがな</c> is four tokens and <c>カタカナ</c> is one.
    /// The small kana and the iteration marks are Hiragana too, so <c>きょう</c> is three tokens and
    /// <c>ゝゞ</c> is two. The prolonged sound mark U+30FC is Katakana, not Hiragana, which is why
    /// <c>ひらがなー</c> ends in a token of its own.
    /// </remarks>
    public static bool IsHiragana(Rune rune)
    {
        if (rune.Value < 0x3041)
            return false;

        return InRanges(rune.Value, HiraganaRanges);
    }

    /// <summary>
    /// The <c>Script=Hiragana</c> code points of Unicode 12.1, as adjacent
    /// <c>[start, end]</c> pairs sorted ascending.
    /// </summary>
    private static readonly int[] HiraganaRanges =
    [
        0x3041, 0x3096, 0x309D, 0x309F, 0x1B001, 0x1B11E, 0x1B150, 0x1B152, 0x1F200, 0x1F200,
    ];

    /// <summary>
    /// Whether <paramref name="rune"/> is a Katakana code point, whose <c>Word_Break</c> class is
    /// <c>Katakana</c> rather than <c>ALetter</c>.
    /// </summary>
    /// <remarks>
    /// The reference keeps a Katakana run together — <c>カタカナ</c> is one token — but its
    /// MidLetter and MidNumLet rules do not join a Katakana character to a letter, so
    /// <c>カ·ナ</c> and <c>q·カナ</c> are two tokens. A separator's join test reads this class.
    /// </remarks>
    public static bool IsKatakana(Rune rune)
    {
        if (rune.Value < 0x30A1)
            return false;

        return InRanges(rune.Value, KatakanaRanges);
    }

    /// <summary>
    /// The <c>Word_Break=Katakana</c> code points of Unicode 12.1, as adjacent
    /// <c>[start, end]</c> pairs sorted ascending.
    /// </summary>
    private static readonly int[] KatakanaRanges =
    [
        0x3031, 0x3035, 0x309B, 0x309C, 0x30A0, 0x30FA, 0x30FC, 0x30FF, 0x31F0, 0x31FF,
        0x32D0, 0x32FE, 0x3300, 0x3357, 0xFF66, 0xFF70, 0xFF71, 0xFF9D,
        0x1B000, 0x1B000, 0x1B164, 0x1B167,
    ];

    /// <summary>
    /// Whether <paramref name="rune"/> is a format character the reference's word-break data lets a
    /// word continue through: a soft hyphen, a byte-order mark, a left-to-right mark, and the other
    /// <c>Word_Break=Format</c> code points.
    /// </summary>
    /// <remarks>
    /// A format character cannot open a word, but it does not end one: a byte-order mark between
    /// the digits of <c>1990</c>, or a left-to-right mark after <c>bank</c>, stays inside the term
    /// in the reference index. U+200B is deliberately absent — it is <c>Word_Break=Other</c>, and
    /// the reference splits a word around it, which this library already did.
    /// </remarks>
    public static bool IsFormatExtend(Rune rune)
    {
        if (rune.Value < 0xAD)
            return false;

        return InRanges(rune.Value, FormatRanges);
    }

    /// <summary>
    /// The <c>Word_Break=Format</c> and <c>Word_Break=ZWJ</c> code points of Unicode 12.1 that are
    /// format characters, plus U+200C, whose word-break class is <c>Extend</c>, as adjacent
    /// <c>[start, end]</c> pairs sorted ascending.
    /// </summary>
    private static readonly int[] FormatRanges =
    [
        0xAD, 0xAD, 0x600, 0x605, 0x61C, 0x61C, 0x6DD, 0x6DD, 0x70F, 0x70F, 0x8E2, 0x8E2, 0x180E, 0x180E, 0x200C, 0x200F,
        0x202A, 0x202E, 0x2060, 0x2064, 0x2066, 0x206F, 0xFEFF, 0xFEFF, 0xFFF9, 0xFFFB, 0x110BD, 0x110BD, 0x110CD, 0x110CD, 0x13430, 0x13438,
        0x1BCA0, 0x1BCA3, 0x1D173, 0x1D17A, 0xE0001, 0xE0001, 0xE0020, 0xE007F,
    ];

    /// <summary>
    /// Whether <paramref name="value"/> falls inside one of the <c>[start, end]</c> pairs in
    /// <paramref name="ranges"/>.
    /// </summary>
    private static bool InRanges(int value, ReadOnlySpan<int> ranges)
    {
        int low = 0;
        int high = (ranges.Length / 2) - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;

            if (value < ranges[middle * 2])
                high = middle - 1;
            else if (value > ranges[(middle * 2) + 1])
                low = middle + 1;
            else
                return true;
        }

        return false;
    }
}
