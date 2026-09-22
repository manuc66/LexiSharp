using System.Buffers;
using System.Globalization;
using System.Text;

namespace LexiSharp.Linguistics;

/// <summary>
/// Splits raw text into normalized terms.
/// </summary>
/// <remarks>
/// Tokenization is **SIMD-accelerated**: <see cref="SearchValues{T}"/> backed by the
/// runtime's vectorized kernels (<c>Vector128</c>/<c>Vector256</c>) skips whole runs of
/// ASCII word characters in one pass, while non-ASCII text is handled rune by rune only
/// at the (typically rare) non-ASCII positions. Normalization pipeline:
/// <list type="number">
/// <item><description>Splitting on any non-letter-or-digit character (simd fast path for ASCII).</description></item>
/// <item><description>Unicode NFKD decomposition + removal of diacritics (é -&gt; e, ñ -&gt; n); ASCII terms short-circuit.</description></item>
/// <item><description>Lowercasing with the invariant culture.</description></item>
/// <item><description>Optional stop word removal and/or stemming through a consumer-provided <see cref="IStemmer"/>.</description></item>
/// <item><description>Optional grouping into term n-grams (« machine learning » becomes <c>machine learning</c>).</description></item>
/// </list>
/// <see cref="TokenizeWithSpans"/> runs the same pipeline while tracking source offsets;
/// <see cref="Tokenize"/> is its projection onto the terms alone.
/// </remarks>
public sealed class Tokenizer : ISpanTokenizer
{
    /// <summary>ASCII book characters (letters and digits); a 62-entry set eligible for SIMD search.</summary>
    private static readonly SearchValues<char> AsciiWordChars =
        SearchValues.Create("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    /// <summary>A raw split word with its half-open source range.</summary>
    private readonly record struct SourceWord(string Text, int Start, int Length);

    private readonly TokenizerOptions _options;
    private readonly IReadOnlySet<string>? _stopWords;

    /// <summary>A shared tokenizer with default options.</summary>
    public static Tokenizer Default { get; } = new();

    /// <summary>The normalized configuration this tokenizer runs with.</summary>
    public TokenizerOptions Options => _options;

    public Tokenizer(TokenizerOptions? options = null)
    {
        _options = (options ?? TokenizerOptions.Default).Sanitize();
        _stopWords = _options.RemoveStopWords ? _options.GetStopWords() : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(string text)
    {
        var spans = TokenizeWithSpans(text);

        if (spans.Count == 0)
            return Array.Empty<string>();

        var terms = new List<string>(spans.Count);

        for (int i = 0; i < spans.Count; i++)
            terms.Add(spans[i].Term);

        return terms;
    }

    /// <inheritdoc />
    public IReadOnlyList<TokenSpan> TokenizeWithSpans(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<TokenSpan>();

        var words = SplitWords(text);

        if (words.Count == 0)
            return Array.Empty<TokenSpan>();

        var spans = ProcessWords(words);

        if (!_useNgrams || spans.Count < 2)
            return spans;

        return BuildNgrams(spans);
    }

    private List<SourceWord> SplitWords(string text)
    {
        var words = new List<SourceWord>(32);
        var word = new StringBuilder(16);

        int wordStart = 0;
        int position = 0;

        while (position < text.Length)
        {
            // SIMD: locate the first character that is not an ASCII letter/digit.
            // Runs of ASCII word characters are consumed in bulk below.
            int relativeEnd = text.AsSpan(position).IndexOfAnyExcept(AsciiWordChars);
            int asciiRunEnd = relativeEnd < 0 ? text.Length : position + relativeEnd;

            if (asciiRunEnd > position)
            {
                if (word.Length == 0)
                    wordStart = position;

                word.Append(text.AsSpan(position, asciiRunEnd - position));
                position = asciiRunEnd;
            }

            if (position >= text.Length)
                break;

            // Only non-ASCII characters and ASCII separators reach this point.
            var rune = Rune.GetRuneAt(text, position);

            if (Rune.IsLetterOrDigit(rune))
            {
                if (word.Length == 0)
                    wordStart = position;

                word.Append(rune);
            }
            else
            {
                FlushWord(word, words, _options.KeepSingleCharTerms, wordStart, position);
            }

            position += rune.Utf16SequenceLength;
        }

        FlushWord(word, words, _options.KeepSingleCharTerms, wordStart, position);
        return words;
    }

    private List<TokenSpan> ProcessWords(List<SourceWord> words)
    {
        var spans = new List<TokenSpan>(words.Count);

        foreach (var raw in words)
        {
            var term = Normalize(raw.Text);

            if (_stopWords is not null && _stopWords.Contains(term))
                continue;

            if (_options.Stemmer is not null)
                term = _options.Stemmer.Stem(term);

            spans.Add(new TokenSpan(term, raw.Start, raw.Length));
        }

        return spans;
    }

    private List<TokenSpan> BuildNgrams(List<TokenSpan> spans)
    {
        var ngrams = new List<TokenSpan>(spans.Count);

        for (int n = _options.NGramMin; n <= _options.NGramMax; n++)
        {
            for (int i = 0; i + n <= spans.Count; i++)
            {
                var first = spans[i];
                var last = spans[i + n - 1];

                string term;
                if (n == 1)
                {
                    term = first.Term;
                }
                else
                {
                    var parts = new string[n];
                    for (int j = 0; j < n; j++)
                        parts[j] = spans[i + j].Term;

                    term = string.Join(' ', parts);
                }

                // The span covers the whole source region of the phrase, separators included.
                ngrams.Add(new TokenSpan(term, first.Start, last.End - first.Start));
            }
        }

        return ngrams;
    }

    private static void FlushWord(StringBuilder word, List<SourceWord> into, bool keepSingleChar, int start, int end)
    {
        if (word.Length == 0)
            return;

        if (word.Length >= 2 || keepSingleChar)
            into.Add(new SourceWord(word.ToString(), start, end - start));

        word.Clear();
    }

    /// <summary>Lowercases and removes diacritics from a single term.</summary>
    public static string Normalize(string term)
    {
        // Fast path: fully ASCII terms need no decomposition or accent removal.
        if (Ascii.IsValid(term))
            return term.ToLowerInvariant();

        var decomposed = term.Normalize(NormalizationForm.FormKD);
        var cleaned = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                cleaned.Append(ch);
        }

        return cleaned.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormC);
    }

    private bool _useNgrams => _options.NGramMax > 1;
}
