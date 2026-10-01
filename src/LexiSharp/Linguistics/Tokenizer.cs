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

    /// <summary>
    /// Whether a trailing possessive is trimmed before the stop word list is consulted. Read once here
    /// because it is a pipeline order, not a per-token decision, and the order is the whole point.
    /// </summary>
    private readonly bool _stripPossessives;

    /// <summary>
    /// The characters that join a word rather than ending it, as a 128-bit ASCII mask, so the inner
    /// loop tests one <c>long</c> instead of scanning a string per character.
    /// </summary>
    /// <remarks>
    /// Built once per tokenizer from <see cref="TokenizerOptions.WordJoiners"/>, so a configuration
    /// that joins nothing — the default — pays one null test per character and nothing else.
    /// </remarks>
    private readonly ulong? _asciiJoiners;

    /// <summary>True when <c>_asciiJoiners</c> holds any ASCII joiner.</summary>
    private bool _hasAsciiJoiners => _asciiJoiners is { } mask && mask != 0;

    /// <summary>True when the tokenizer joins on at least one non-ASCII character.</summary>
    private HashSet<char>? _nonAsciiJoiners;

    /// <summary>
    /// True when a separator's decision depends on the characters either side of it, which is the
    /// whole content of <see cref="WordSegmentation.UnicodeWordBoundaries"/>.
    /// </summary>
    private bool _conditionalJoins;

    /// <summary>A shared tokenizer with default options.</summary>
    public static Tokenizer Default { get; } = new();

    /// <summary>The normalized configuration this tokenizer runs with.</summary>
    public TokenizerOptions Options => _options;

    /// <summary>Creates a tokenizer, using <c>TokenizerOptions.Default</c> when none is given.</summary>
    /// <param name="options">
    /// The configuration to sanitize and run with. Sanitizing is what keeps an impossible
    /// combination — an n-gram size below 1, a stemmer that is off while stemming is on — from
    /// reaching the hot path.
    /// </param>
    public Tokenizer(TokenizerOptions? options = null)
    {
        _options = (options ?? TokenizerOptions.Default).Sanitize();
        _stopWords = _options.RemoveStopWords ? _options.GetStopWords() : null;
        _stripPossessives = _options.StripPossessives;

        if (_options.WordJoiners is { Length: > 0 } joiners)
        {
            ulong mask = 0;

            foreach (char joiner in joiners)
            {
                if (joiner < 128)
                {
                    mask |= 1UL << joiner;
                }
                else
                {
                    // Membership, not a flag. "Some non-ASCII joiner is configured" is not the
                    // question — "is this character one of them" is, and answering the first made
                    // every non-ASCII character a joiner as soon as one was configured.
                    (_nonAsciiJoiners ??= []).Add(joiner);
                }
            }

            _asciiJoiners = mask == 0 ? null : mask;
        }

        _conditionalJoins = _options.WordSegmentation == WordSegmentation.UnicodeWordBoundaries;
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
        if (_asciiJoiners is not null || _nonAsciiJoiners is not null || _conditionalJoins)
        {
            ScanJoining(text, terms, spans);
            return;
        }

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
            else if (wordStart >= 0 && IsCombiningMark(rune))
            {
                // A mark inside a word extends it: `celi\u0307l` is one token, not two. It cannot open one
                // — a mark at the start of a token is dropped, which is the asymmetry this branch encodes.
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

    /// <summary>
    /// The scan used when <see cref="TokenizerOptions.WordJoiners"/> is set: a word ends at a
    /// non-word character unless that character joins and a word character follows it.
    /// </summary>
    /// <remarks>
    /// A separate path rather than a branch inside <see cref="Scan"/>, and the reason is the bulk
    /// skip. <see cref="AsciiWordChars"/> answers "is this character part of a word", which is not the
    /// question here — the question is "does a word continue past this character", and that depends on
    /// the character after it. Answering it inside the bulk-skip loop would mean re-deriving where each
    /// ASCII run ends, and the boundary cases are where that goes wrong: in <c>hello.,world</c> the
    /// period does not join (a comma follows) and the comma does not join (a period precedes), so the
    /// word is <c>hello</c> alone. A trim-leading-and-trailing pass over each run gets that wrong.
    /// <para>
    /// ASCII runs inside a word are still skipped in bulk, so the loss is confined to the joins
    /// themselves. What that costs against the default path is not measured.
    /// </para>
    /// </remarks>
    private void ScanJoining(ReadOnlySpan<char> text, List<string>? terms, List<TokenSpan>? spans)
    {
        int position = 0;

        while (position < text.Length)
        {
            bool beginsWord = StartsWordAt(text, position);

            // An unconditional joiner can open a token when a word character follows it: measured
            // `_630888` indexes whole and `_alpha` as `_alpha`, so the underscore is part of the word
            // here — while `-alpha` is just `alpha`, so this is the underscore's rule and not a rule
            // about any separator. Tested here rather than inside the run scan, because the run scan
            // only asks whether a word continues past a character it is already inside.
            //
            // A run of joiners on its own is not a token: `_`, `__` and `___` alone all produce nothing.
            // So the joiners have to lead to a word character, not merely to another joiner, and
            // `ContinuesWord` is what finds the word character at the far end of the run.
            if (!beginsWord)
            {
                beginsWord = text[position] < 128
                    && RuleFor(text[position]) == JoinRule.Always
                    && position + 1 < text.Length
                    && LeadsWordAt(text, position + 1);
            }

            if (!beginsWord)
            {
                position++;
                continue;
            }

            int start = position;
            position++;

            // An unconditional joiner that opens the token belongs to it, even though nothing precedes
            // it for the "word character on both sides" test to succeed on. Measured: `_630888` indexes
            // whole, `_alpha` as `_alpha` and `__alpha` as `__alpha` — while `-alpha` is just `alpha`,
            // so this is the underscore's rule and not a rule about any separator. Every leading joiner
            // is consumed here, including a run of them, because the run scan below only asks whether a
            // word continues past a character it is already inside.
            while (position < text.Length && RuleFor(text[position]) == JoinRule.Always
                && position + 1 < text.Length && ContinuesWord(text, position + 1))
            {
                position++;
            }

            while (position < text.Length)
            {
                if (text[position] < 128)
                {
                    // Bulk-skip the rest of an ASCII word run: everything up to the first character
                    // that is not an ASCII letter or digit is inside the word. A negative result means
                    // every remaining character matches, so the run reaches the end of the text — the
                    // case a token at the very end of the input would otherwise fall into.
                    int relativeEnd = text[position..].IndexOfAnyExcept(AsciiWordChars);

                    if (relativeEnd != 0)
                    {
                        position += relativeEnd < 0 ? text.Length - position : relativeEnd;
                        continue;
                    }
                }
                else if (StartsWordAt(text, position) || ContinuesWordAt(text, position))
                {
                    // A non-ASCII word character, or a combining mark that continues one. Decode to learn
                    // how many UTF-16 units it spans, since a surrogate pair is two chars and the boundary
                    // has to land between runes.
                    Rune.DecodeFromUtf16(text[position..], out _, out int runeLength);
                    position += runeLength;
                    continue;
                }

                if (JoinsToNext(text, position))
                {
                    position++;
                    continue;
                }

                break;
            }

            if (position - start < 2 && !_options.KeepSingleCharTerms)
                continue;

            string term = NormalizeToken(text.Slice(start, position - start));

            if (_stripPossessives && EndsWithPossessive(term))
                term = term[..^2];

            if (_stopWords is not null && _stopWords.Contains(term))
                continue;

            if (_options.Stemmer is not null)
                term = _options.Stemmer.Stem(term);

            terms?.Add(term);
            spans?.Add(new TokenSpan(term, start, position - start));
        }
    }

    /// <summary>
    /// Whether <paramref name="term"/> ends in the two-character possessive <c>'s</c> or <c>’s</c>.
    /// </summary>
    /// <remarks>
    /// No length floor, and that is measured rather than assumed: <c>z's</c> becomes <c>z</c>, so the
    /// possessive is removed before anything asks how long the result is. A floor here would leave
    /// <c>t's</c> as <c>t'</c>, which is a term no query contains — and it did, in a corpus of 23 895
    /// terms, four times over.
    /// </remarks>
    private static bool EndsWithPossessive(string term) =>
        term.Length >= 2 && term[^1] == 's' && (term[^2] == '\'' || term[^2] == '’');

    /// <summary>
    /// Whether the word continues at <paramref name="position"/>: either a character that can be part of
    /// a word, or an unconditional joiner — the run of leading underscores needs the second case.
    /// </summary>
    private bool ContinuesWord(ReadOnlySpan<char> text, int position) =>
        StartsWordAt(text, position) || RuleFor(text[position]) == JoinRule.Always;

    /// <summary>
    /// Whether a token that has opened on joiners reaches a word character: one immediately, or past a
    /// further run of joiners. Measured: `__alpha` is a token, `___` alone is not.
    /// </summary>
    private bool LeadsWordAt(ReadOnlySpan<char> text, int position)
    {
        while (position < text.Length)
        {
            if (StartsWordAt(text, position))
                return true;

            if (text[position] >= 128 || RuleFor(text[position]) != JoinRule.Always)
                return false;

            position++;
        }

        return false;
    }

    /// <summary>
    /// Whether the character at <paramref name="position"/> continues a word: a combining mark does, a
    /// mark cannot start one.
    /// </summary>
    /// <remarks>
    /// Measured, and the asymmetry is the point: <c>a◌̇b</c> is one token and <c>a◌̇</c> is one token,
    /// but <c>◌̇ab</c> is just <c>ab</c>, and a lone mark between two spaces is nothing at all. A mark
    /// modifies the character it follows, so it extends a word and cannot open one.
    /// <para>
    /// It matters because a scanner accepting only letters and digits cuts here, splitting one word into
    /// two. On the BEIR corpora in this repository the effect is tiny but real: ArguAna holds no combining
    /// mark at all, SciFact none either, NFCorpus four characters out of 5,779,318.
    /// </para>
    /// </remarks>
    private static bool ContinuesWordAt(ReadOnlySpan<char> text, int position)
    {
        if (text[position] < 128)
            return false;

        return Rune.DecodeFromUtf16(text[position..], out Rune rune, out _) == OperationStatus.Done
               && IsCombiningMark(rune);
    }

    /// <summary>
    /// Whether <paramref name="rune"/> is one of the three Unicode mark categories.
    /// </summary>
    /// <remarks>
    /// <see cref="Rune.GetUnicodeCategory"/> rather than <see cref="CharUnicodeInfo"/>: the latter takes
    /// a UTF-16 unit, so a mark outside the basic plane would be classified as an unpaired surrogate
    /// rather than as itself.
    /// </remarks>
    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>Whether a word starts at <paramref name="position"/>. A joiner never starts one.</summary>
    private bool StartsWordAt(ReadOnlySpan<char> text, int position)
    {
        char c = text[position];

        if (c < 128)
            return AsciiWordChars.Contains(c);

        var status = Rune.DecodeFromUtf16(text[position..], out var rune, out int length);

        return status == OperationStatus.Done && Rune.IsLetterOrDigit(rune) && length > 0;
    }

    /// <summary>
    /// Whether the non-word character at <paramref name="position"/> is a joiner with a word
    /// character immediately after it — the only condition under which the word continues.
    /// </summary>
    private bool JoinsToNext(ReadOnlySpan<char> text, int position)
    {
        if (_conditionalJoins && JoinsUnderCondition(text, position))
            return true;

        char joiner = text[position];

        if (joiner < 128)
        {
            if (_asciiJoiners is not { } mask || (mask & (1UL << joiner)) == 0)
                return false;
        }
        else if (_nonAsciiJoiners is null || !_nonAsciiJoiners.Contains(joiner))
        {
            return false;
        }

        return position + 1 < text.Length && StartsWordAt(text, position + 1);
    }

    /// <summary>Whether the two characters a word character sits between join, digit to letter excluded.</summary>
    private enum CharClass
    {
        /// <summary>Not a word character: the word has ended on both sides.</summary>
        Other,

        /// <summary>An ASCII digit. The "numeric" class, as measured.</summary>
        Digit,

        /// <summary>A letter, ASCII or not. The "alphabetic" class, as measured.</summary>
        Letter,
    }

    /// <summary>
    /// The measured separator table: which of them join, and under which condition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry was established by tokenizing the two sides against the implementation being
    /// matched, with the separator between them, for a digit and a letter on each side. Nothing here
    /// is inferred from a specification: a comma joining two digits and splitting two letters, a colon
    /// doing the opposite, and a full stop or an apostrophe doing both, are three different rules and a
    /// single character set cannot express them.
    /// </para>
    /// <para>
    /// Every separator not listed here splits, including the hyphen — which is why
    /// <c>environment-friendly</c> is two terms in both implementations, and why this table is not a
    /// list of "things that glue words together" but a list of exceptions to splitting.
    /// </para>
    /// </remarks>
    private enum JoinRule
    {
        /// <summary>Never joins.</summary>
        Never,

        /// <summary>Joins two digits and splits everything else: <c>,</c> and <c>;</c>.</summary>
        BetweenDigits,

        /// <summary>Joins two letters and splits everything else: <c>:</c>.</summary>
        BetweenLetters,

        /// <summary>Joins two digits or two letters, splits across the classes: <c>.</c> and the apostrophes.</summary>
        WithinOneClass,

        /// <summary>Joins any two word characters: <c>_</c> and the soft hyphen.</summary>
        Always,
    }

    private static JoinRule RuleFor(char separator) => separator switch
    {
        ',' or ';' => JoinRule.BetweenDigits,
        ':' => JoinRule.BetweenLetters,
        '.' or '\'' or '’' or '‘' => JoinRule.WithinOneClass,
        '_' or '­' => JoinRule.Always,
        _ => JoinRule.Never,
    };

    /// <summary>Evaluates <see cref="RuleFor"/> for the separator at <paramref name="position"/>.</summary>
    private static bool JoinsUnderCondition(ReadOnlySpan<char> text, int position)
    {
        JoinRule rule = RuleFor(text[position]);

        if (rule == JoinRule.Never)
            return false;

        if (rule == JoinRule.Always)
            return true;

        // Both sides must be word characters, and both are inside the current word or immediately
        // after it, so the left one is the character the separator interrupts.
        if (position + 1 >= text.Length || !StartsWordAtStatic(text, position + 1))
            return false;

        if (position == 0)
            return false;

        CharClass left = ClassAt(text, position - 1);
        CharClass right = ClassAt(text, position + 1);

        if (left == CharClass.Other || right == CharClass.Other)
            return false;

        return rule switch
        {
            JoinRule.BetweenDigits => left == CharClass.Digit && right == CharClass.Digit,
            JoinRule.BetweenLetters => left == CharClass.Letter && right == CharClass.Letter,
            _ => left == right,
        };
    }

    private static CharClass ClassAt(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];

        if (c < 128)
        {
            if (c >= '0' && c <= '9')
                return CharClass.Digit;

            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                return CharClass.Letter;

            return CharClass.Other;
        }

        if (Rune.DecodeFromUtf16(text[index..], out var rune, out _) != OperationStatus.Done)
            return CharClass.Other;

        if (Rune.IsDigit(rune))
            return CharClass.Digit;

        return Rune.IsLetter(rune) ? CharClass.Letter : CharClass.Other;
    }

    private static bool StartsWordAtStatic(ReadOnlySpan<char> text, int position)
    {
        char c = text[position];

        if (c < 128)
            return AsciiWordChars.Contains(c);

        return Rune.DecodeFromUtf16(text[position..], out var rune, out _) == OperationStatus.Done
            && Rune.IsLetterOrDigit(rune);
    }

    /// <summary>Normalizes, filters and stores one word range, honoring single-char/stop-word/stemming options.</summary>
    private void EmitWord(ReadOnlySpan<char> text, int start, int end, List<string>? terms, List<TokenSpan>? spans)
    {
        int length = end - start;

        if (length < 2 && !_options.KeepSingleCharTerms)
            return;

        string term = NormalizeToken(text.Slice(start, length));

        if (_stripPossessives && EndsWithPossessive(term))
            term = term[..^2];

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

    private string NormalizeToken(ReadOnlySpan<char> term)
    {
        if (_options.FoldDiacritics)
            return Normalize(term);

        // Folding off: lowercase only. Still lowercased — an index has to be case-insensitive to be
        // an index — but the diacritics stay, so `café` and `cafe` are two terms.
        return Ascii.IsValid(term) ? NormalizeAscii(term) : Lower(term.ToString());
    }

    private string NormalizeToken(string term)
    {
        if (_options.FoldDiacritics)
            return Normalize(term);

        return Lower(term);
    }

    /// <summary>
    /// Lowercases <paramref name="term"/>, optionally applying the one code point .NET's invariant casing
    /// leaves alone that the reference implementation folds.
    /// </summary>
    /// <remarks>
    /// <c>ToLowerInvariant</c> returns the same instance when nothing needs folding, which is the common
    /// case and the reason this is a separate call rather than an inline cast.
    /// </remarks>
    private string Lower(string term)
    {
        if (_options.FoldTurkishDottedI && term.AsSpan().IndexOf(TurkishDottedI) >= 0)
        {
            // U+0069, one character — the Unicode *simple* mapping. The full mapping would append U+0307,
            // a combining dot above, and the reference does not: its index holds a five-character
            // `celil`, and it returns a six-character `celi̇l` with the dot intact. Measured, both ways.
            var builder = new StringBuilder(term.Length);

            foreach (char ch in term)
            {
                builder.Append(ch == TurkishDottedI ? 'i' : ch);
            }

            return builder.ToString().ToLowerInvariant();
        }

        return term.ToLowerInvariant();
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

    /// <summary>
    /// U+0130, the Turkish dotted capital I: the one code point this library's casing and the reference
    /// implementation's disagree on, measured over 505 positions whose lowercase differs.
    /// </summary>
    private const char TurkishDottedI = 'İ';
}
