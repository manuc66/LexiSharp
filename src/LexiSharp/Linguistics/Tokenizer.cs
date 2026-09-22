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
/// A raw word is always a contiguous slice of the source, so scanning tracks a single
/// <c>[start, end)</c> range and normalizes straight from the span — no intermediate word
/// buffer. <see cref="TokenizeWithSpans(string)"/> runs the same scan while tracking source
/// offsets; <see cref="Tokenize(string)"/> emits terms alone.
/// </remarks>
public sealed class Tokenizer : ISpanTokenizer
{
    /// <summary>ASCII book characters (letters and digits); a 62-entry set eligible for SIMD search.</summary>
    private static readonly SearchValues<char> AsciiWordChars =
        SearchValues.Create("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

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
    public IReadOnlyList<string> Tokenize(string text) => Tokenize(text.AsSpan());

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return Array.Empty<string>();

        var terms = new List<string>(16);
        Scan(text, terms, null);

        if (terms.Count == 0)
            return Array.Empty<string>();

        if (!_useNgrams || terms.Count < 2)
            return terms;

        return BuildNgrams(terms);
    }

    /// <inheritdoc />
    public IReadOnlyList<TokenSpan> TokenizeWithSpans(string text) => TokenizeWithSpans(text.AsSpan());

    /// <inheritdoc />
    public IReadOnlyList<TokenSpan> TokenizeWithSpans(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return Array.Empty<TokenSpan>();

        var spans = new List<TokenSpan>(16);
        Scan(text, null, spans);

        if (spans.Count == 0)
            return Array.Empty<TokenSpan>();

        if (!_useNgrams || spans.Count < 2)
            return spans;

        return BuildNgrams(spans);
    }

    /// <summary>
    /// Single pass over the source: locates words as half-open <c>[start, end)</c> ranges and
    /// emits each into <paramref name="terms"/> and/or <paramref name="spans"/> (exactly one is
    /// non-null). Normalization, stop-word removal and stemming happen at emission, straight
    /// from the source slice.
    /// </summary>
    private void Scan(ReadOnlySpan<char> text, List<string>? terms, List<TokenSpan>? spans)
    {
        int wordStart = -1;
        int position = 0;

        while (position < text.Length)
        {
            // SIMD: locate the first character that is not an ASCII letter/digit.
            // Runs of ASCII word characters are consumed in bulk below.
            int relativeEnd = text[position..].IndexOfAnyExcept(AsciiWordChars);
            int asciiRunEnd = relativeEnd < 0 ? text.Length : position + relativeEnd;

            if (asciiRunEnd > position)
            {
                if (wordStart < 0)
                    wordStart = position;

                position = asciiRunEnd;
            }

            if (position >= text.Length)
                break;

            // Only non-ASCII characters and ASCII separators reach this point.
            var status = Rune.DecodeFromUtf16(text[position..], out var rune, out _);

            if (status != OperationStatus.Done)
                throw new ArgumentException("The input contains an invalid Unicode code point.", nameof(text));

            if (Rune.IsLetterOrDigit(rune))
            {
                if (wordStart < 0)
                    wordStart = position;
            }
            else if (wordStart >= 0)
            {
                EmitWord(text, wordStart, position, terms, spans);
                wordStart = -1;
            }

            position += rune.Utf16SequenceLength;
        }

        if (wordStart >= 0)
            EmitWord(text, wordStart, text.Length, terms, spans);
    }

    /// <summary>Normalizes, filters and stores one word range, honoring single-char/stop-word/stemming options.</summary>
    private void EmitWord(ReadOnlySpan<char> text, int start, int end, List<string>? terms, List<TokenSpan>? spans)
    {
        int length = end - start;

        if (length < 2 && !_options.KeepSingleCharTerms)
            return;

        string term = Normalize(text.Slice(start, length));

        if (_stopWords is not null && _stopWords.Contains(term))
            return;

        if (_options.Stemmer is not null)
            term = _options.Stemmer.Stem(term);

        terms?.Add(term);
        spans?.Add(new TokenSpan(term, start, length));
    }

    /// <summary>Builds the n-gram term list (unigrams first, then longer grams) from a term list.</summary>
    private List<string> BuildNgrams(List<string> terms)
    {
        var ngrams = new List<string>(terms.Count);

        for (int n = _options.NGramMin; n <= _options.NGramMax && n <= terms.Count; n++)
        {
            if (n == 1)
            {
                for (int i = 0; i < terms.Count; i++)
                    ngrams.Add(terms[i]);

                continue;
            }

            var parts = new string[n];

            for (int i = 0; i + n <= terms.Count; i++)
            {
                for (int j = 0; j < n; j++)
                    parts[j] = terms[i + j];

                ngrams.Add(string.Join(' ', parts));
            }
        }

        return ngrams;
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

    /// <summary>Lowercases and removes diacritics from a single term.</summary>
    public static string Normalize(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        // Fast path: fully ASCII terms need no decomposition or accent removal, and
        // ToLowerInvariant returns the same instance when nothing needs folding.
        if (Ascii.IsValid(term))
            return term.ToLowerInvariant();

        return NormalizeSlow(term);
    }

    /// <summary>Lowercases and removes diacritics from a single term given as a source slice.</summary>
    public static string Normalize(ReadOnlySpan<char> term)
    {
        // Fast path: fully ASCII terms need no decomposition or accent removal.
        if (Ascii.IsValid(term))
            return NormalizeAscii(term);

        return NormalizeSlow(term.ToString());
    }

    private static string NormalizeAscii(ReadOnlySpan<char> term)
    {
        for (int i = 0; i < term.Length; i++)
        {
            if (term[i] is >= 'A' and <= 'Z')
            {
                // Folding needed: materialize the slice, then lowercase it.
                return term.ToString().ToLowerInvariant();
            }
        }

        // Already lowercase: the source slice is the normalized term.
        return term.ToString();
    }

    private static string NormalizeSlow(string term)
    {
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
