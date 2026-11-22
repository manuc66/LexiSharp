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

    /// <summary>True when the tokenizer joins on at least one non-ASCII character.</summary>
    private readonly HashSet<char>? _nonAsciiJoiners;

    /// <summary>
    /// True when a separator's decision depends on the characters either side of it, which is the
    /// whole content of <see cref="WordSegmentation.UnicodeWordBoundaries"/>.
    /// </summary>
    private readonly bool _conditionalJoins;

    /// <summary>Characters that are terms on their own, from <see cref="TokenizerOptions.SymbolTerms"/>.</summary>
    private readonly HashSet<char>? _symbolTerms;

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

        if (_options.SymbolTerms is { Length: > 0 } symbols)
            _symbolTerms = [.. symbols];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(string text) => Tokenize(text.AsSpan());

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return Array.Empty<string>();

        var terms = new List<string>(16);
        var none = new NormalizedTermsBuilder(default, enabled: false);
        Scan(text, terms, null, ref none);

        if (terms.Count == 0)
            return Array.Empty<string>();

        if (!_useNgrams || terms.Count < 2)
            return terms;

        return BuildNgrams(terms);
    }

    /// <inheritdoc />
    public IReadOnlyList<TokenSpan> TokenizeWithSpans(string text) => TokenizeWithSpans(text.AsSpan());

    /// <inheritdoc />
    public void TokenizeInto(ReadOnlySpan<char> text, List<string> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // The n-gram pass renumbers the list — a minimum above 1 drops the unigrams the scan
        // produced — and the version that builds a fresh list gets that for free. Reproducing it
        // here would mean removing terms the caller's list already holds, so that configuration
        // delegates to the allocating path rather than filling in place.
        if (_useNgrams)
        {
            foreach (string term in Tokenize(text))
                destination.Add(term);

            return;
        }

        var none = new NormalizedTermsBuilder(default, enabled: false);
        Scan(text, destination, null, ref none);
    }

    /// <inheritdoc />
    public int TokenizeNormalized(ReadOnlySpan<char> text, Span<NormalizedTerm> destination)
    {
        if (text.IsEmpty)
            return 0;

        // A term is its own source slice only where nothing in the pipeline replaces it. An n-gram
        // pass renumbers the terms, a stop-word list is asked a question a span cannot answer, and a
        // stemmer or the possessive trim builds a new term outright; the joining scans emit through
        // a path that does not fill this buffer. Each of those materializes, so the caller is told to
        // take the string path rather than handed a term it cannot resolve.
        if (_useNgrams ||
            _stopWords is not null ||
            _options.Stemmer is not null ||
            _stripPossessives ||
            _asciiJoiners is not null ||
            _nonAsciiJoiners is not null ||
            _conditionalJoins)
        {
            return -1;
        }

        var normalized = new NormalizedTermsBuilder(destination);
        Scan(text, null, null, ref normalized);

        return normalized.Complete();
    }

    /// <summary>
    /// Whether a source slice is already its own normalized form: ASCII, with no uppercase letter.
    /// Every other character is one that folding, lowercasing or decomposition could replace.
    /// </summary>
    private static bool IsNormalizedLiteral(ReadOnlySpan<char> slice)
    {
        for (int i = 0; i < slice.Length; i++)
        {
            char c = slice[i];

            if (c >= 128 || (c >= 'A' && c <= 'Z'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The destination of <see cref="TokenizeNormalized"/>, counting what it holds and noticing when
    /// it did not hold everything. A disabled builder is what the scans that do not fill it carry.
    /// </summary>
    private ref struct NormalizedTermsBuilder
    {
        private readonly Span<NormalizedTerm> _destination;
        private readonly bool _enabled;

        public NormalizedTermsBuilder(Span<NormalizedTerm> destination, bool enabled = true)
        {
            _destination = destination;
            _enabled = enabled;
            Count = 0;
            Overflowed = false;
        }

        public int Count { get; private set; }

        public bool Overflowed { get; private set; }

        public readonly bool Enabled => _enabled;

        public void Add(NormalizedTerm term)
        {
            if (Count >= _destination.Length)
            {
                Overflowed = true;
                return;
            }

            _destination[Count++] = term;
        }

        public readonly int Complete() => Overflowed ? -1 : Count;
    }

    /// <inheritdoc />
    public IReadOnlyList<TokenSpan> TokenizeWithSpans(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return Array.Empty<TokenSpan>();

        var spans = new List<TokenSpan>(16);
        var none = new NormalizedTermsBuilder(default, enabled: false);
        Scan(text, null, spans, ref none);

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
    private void Scan(ReadOnlySpan<char> text, List<string>? terms, List<TokenSpan>? spans, ref NormalizedTermsBuilder normalized)
    {
        if (_asciiJoiners is not null || _nonAsciiJoiners is not null || _conditionalJoins)
        {
            // The joining scans emit through their own path, and TokenizeNormalized declines a
            // joining configuration, so `normalized` is never enabled here.
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
                EmitWord(text, wordStart, position, terms, spans, ref normalized);
                wordStart = -1;
            }

            position += rune.Utf16SequenceLength;
        }

        if (wordStart >= 0)
            EmitWord(text, wordStart, text.Length, terms, spans, ref normalized);
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
            if (EmitStandaloneAt(text, ref position, terms, spans))
                continue;

            if (!BeginsWordAt(text, position))
            {
                position++;
                continue;
            }

            int start = position;
            position = ConsumeLeadingJoiners(text, position + 1);
            position = ScanWordRun(text, position);

            EmitWordRun(text, start, position, terms, spans);
        }
    }

    /// <summary>
    /// Emits the character at <paramref name="position"/> as a term of its own when it is one, and
    /// advances past it.
    /// </summary>
    /// <remarks>
    /// A configured symbol is a term of its own and does not extend the word beside it:
    /// <c>Aspirin®</c> is <c>aspirin</c> and <c>®</c>, not <c>aspirin®</c>. Under the measured
    /// boundaries an emoji, an ideograph, a Hiragana character and a complete emoji sequence are
    /// the same kind of term. Each starts before the word logic because none may open a run.
    /// <para>
    /// The length is the grammar's, not one code point's. The reference appends <c>Format</c>,
    /// <c>Extend</c> and <c>ZWJ</c> characters to the token they follow, so <c>尺̇</c> and
    /// <c>ひ̇</c> are one term each, and it keeps an emoji sequence together: a ZWJ family, a flag
    /// pair, a skin-tone modifier, a keycap. Both were measured against the reference tokenizer.
    /// </para>
    /// </remarks>
    private bool EmitStandaloneAt(
        ReadOnlySpan<char> text, ref int position, List<string>? terms, List<TokenSpan>? spans)
    {
        if (_symbolTerms is not null && text[position] >= 128 && _symbolTerms.Contains(text[position]))
        {
            int symbolLength = _conditionalJoins ? ExtendedLengthAt(text, position) : 1;

            EmitStandalone(text, position, symbolLength, terms, spans);
            position += symbolLength;
            return true;
        }

        if (!_conditionalJoins)
            return false;

        // The keycap bases are ASCII, so this runs before the non-ASCII gate below.
        int keycapLength = KeycapLengthAt(text, position);

        if (keycapLength > 0)
        {
            EmitStandalone(text, position, keycapLength, terms, spans);
            position += keycapLength;
            return true;
        }

        if (text[position] < 128)
            return false;

        int standaloneLength = StandaloneLengthAt(text, position);

        if (standaloneLength == 0)
            return false;

        EmitStandalone(text, position, standaloneLength, terms, spans);
        position += standaloneLength;
        return true;
    }

    /// <summary>
    /// The length of the standalone token at <paramref name="position"/>, or zero when the
    /// character there does not open one.
    /// </summary>
    private static int StandaloneLengthAt(ReadOnlySpan<char> text, int position)
    {
        if (Rune.DecodeFromUtf16(text[position..], out Rune rune, out _) != OperationStatus.Done)
            return 0;

        if (IsRegionalIndicator(rune))
            return RegionalIndicatorLengthAt(text, position);

        if (WordBreakClasses.IsEmoji(rune) || IsEmojiModifier(rune))
            return EmojiLengthAt(text, position);

        if (WordBreakClasses.IsIdeographic(rune) || WordBreakClasses.IsHiragana(rune))
            return ExtendedLengthAt(text, position);

        return 0;
    }

    /// <summary>
    /// The length of the emoji token at <paramref name="position"/>: one emoji element, then any
    /// number of <c>ZWJ</c>-joined elements.
    /// </summary>
    /// <remarks>
    /// The reference's grammar treats <c>ZWJ</c> as a joiner only when an emoji element follows it;
    /// a trailing <c>ZWJ</c> stays in the token it could not join. Measured: <c>👨‍👩</c> is one
    /// term, <c>👨‍👩</c> with a letter after it is the same term plus the letter, and a lone
    /// <c>👨‍</c> keeps its joiner.
    /// </remarks>
    private static int EmojiLengthAt(ReadOnlySpan<char> text, int position)
    {
        int length = EmojiElementLengthAt(text, position);

        if (length == 0)
            return 0;

        int end = position + length;

        while (end < text.Length)
        {
            int probe = end;

            while (probe < text.Length && text[probe] == '\u200D')
                probe++;

            if (probe == end)
                break;

            int joined = EmojiElementLengthAt(text, probe);

            if (joined == 0)
            {
                // The joiners belong to the token even though nothing follows them.
                end = probe;
                break;
            }

            end = probe + joined;
        }

        return end - position;
    }

    /// <summary>
    /// The length of one emoji element at <paramref name="position"/>, with the
    /// <c>Extend</c>/<c>Format</c> characters that continue it, or zero when none starts there.
    /// </summary>
    private static int EmojiElementLengthAt(ReadOnlySpan<char> text, int position)
    {
        if (Rune.DecodeFromUtf16(text[position..], out Rune rune, out int length) != OperationStatus.Done)
            return 0;

        // Skin-tone modifiers are Emoji but not Extended_Pictographic: they attach to a base, and
        // a bare one is a token of its own in the reference.
        if (!WordBreakClasses.IsEmoji(rune) && !IsEmojiModifier(rune))
            return 0;

        int end = position + length;

        while (end < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[end..], out Rune next, out int nextLength) != OperationStatus.Done)
                break;

            // ZWJ is left to the caller: it either joins the next element or ends the token.
            if (next.Value == 0x200D || !ContinuesEmojiElement(next))
                break;

            end += nextLength;
        }

        return end - position;
    }

    /// <summary>Whether <paramref name="rune"/> continues the emoji element before it.</summary>
    private static bool ContinuesEmojiElement(Rune rune) =>
        IsCombiningMark(rune) || IsEmojiModifier(rune) || WordBreakClasses.IsFormatExtend(rune);

    /// <summary>
    /// The length of the regional-indicator pair at <paramref name="position"/>, or zero for a lone
    /// indicator.
    /// </summary>
    /// <remarks>
    /// The reference emits a flag as two regional indicators and the extenders after each; a single
    /// indicator is not a token at all. Measured: <c>🇫🇷</c> is one term, <c>🇫</c> is none.
    /// </remarks>
    private static int RegionalIndicatorLengthAt(ReadOnlySpan<char> text, int position)
    {
        int first = ExtendedLengthAt(text, position);
        int second = position + first;

        if (second >= text.Length
            || Rune.DecodeFromUtf16(text[second..], out Rune secondRune, out _) != OperationStatus.Done
            || !IsRegionalIndicator(secondRune))
        {
            return 0;
        }

        return first + ExtendedLengthAt(text, second);
    }

    /// <summary>
    /// The length of the keycap sequence at <paramref name="position"/>, or zero when the character
    /// there does not open one.
    /// </summary>
    /// <remarks>
    /// Only <c>#</c> and <c>*</c> are handled here; a digit keycap is a word and stays one. The
    /// reference's numeric and word rules match a longer run than its keycap rule — <c>1️⃣2️⃣</c> is
    /// one numeric token and <c>1️⃣a</c> one word — while <c>#️⃣</c> has no longer rule to lose to and
    /// would otherwise be dropped. Measured: <c>#️⃣</c> and <c>#⃣</c> are terms, <c>#</c> alone is
    /// not, and <c>#️⃣*️⃣</c> is two.
    /// </remarks>
    private static int KeycapLengthAt(ReadOnlySpan<char> text, int position)
    {
        char c = text[position];

        if (c is not '#' and not '*')
            return 0;

        int end = position + 1;

        while (end < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[end..], out Rune next, out int nextLength) != OperationStatus.Done)
                return 0;

            if (next.Value is 0xFE0E or 0xFE0F or 0x20E3)
                break;

            if (!IsCombiningMark(next) && !WordBreakClasses.IsFormatExtend(next))
                return 0;

            end += nextLength;
        }

        if (end < text.Length && text[end] == '\uFE0F')
            end++;

        if (end >= text.Length || text[end] != '\u20E3')
            return 0;

        end++;

        while (end < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[end..], out Rune next, out int nextLength) != OperationStatus.Done
                || !ContinuesEmojiElement(next))
            {
                break;
            }

            end += nextLength;
        }

        return end - position;
    }

    /// <summary>
    /// The length of the character at <paramref name="position"/> and the
    /// <c>Extend</c>/<c>Format</c> characters that follow it.
    /// </summary>
    /// <remarks>
    /// This is UAX#29's "X (Extend | Format | ZWJ)* → X", which the reference applies to an
    /// ideograph, a Hiragana character and a configured symbol as well. Measured: <c>尺̇</c>,
    /// <c>が</c> and <c>尺\uFEFF</c> are one term each, and the character after the last one
    /// starts a new token.
    /// </remarks>
    private static int ExtendedLengthAt(ReadOnlySpan<char> text, int position)
    {
        if (Rune.DecodeFromUtf16(text[position..], out _, out int length) != OperationStatus.Done)
            return 0;

        int end = position + length;

        while (end < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[end..], out Rune next, out int nextLength) != OperationStatus.Done
                || !(IsCombiningMark(next) || WordBreakClasses.IsFormatExtend(next)))
            {
                break;
            }

            end += nextLength;
        }

        return end - position;
    }

    private static bool IsRegionalIndicator(Rune rune) =>
        rune.Value is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsEmojiModifier(Rune rune) =>
        rune.Value is >= 0x1F3FB and <= 0x1F3FF;

    /// <summary>Whether a token starts at <paramref name="position"/>.</summary>
    /// <remarks>
    /// An unconditional joiner can open a token when a word character follows it: measured
    /// <c>_630888</c> indexes whole and <c>_alpha</c> as <c>_alpha</c>, so the underscore is part of
    /// the word — while <c>-alpha</c> is just <c>alpha</c>. A run of joiners on its own is not a
    /// token, so the joiners have to lead to a word character, not merely to another joiner.
    /// U+202F joins the same way from the other side of the ASCII boundary: the reference's
    /// word-break data calls it <c>ExtendNumLet</c>, so a leading one belongs to the token that
    /// follows it (FiQA's section sign + U+202F + <c>1950.6</c> is one term).
    /// </remarks>
    private bool BeginsWordAt(ReadOnlySpan<char> text, int position)
    {
        if (StartsWordAt(text, position))
            return true;

        if (text[position] < 128
            && RuleFor(text[position]) == JoinRule.Always
            && position + 1 < text.Length
            && LeadsWordAt(text, position + 1))
        {
            return true;
        }

        return IsExtendNumLetAt(text, position)
            && position + 1 < text.Length
            && LeadsWordAt(text, position + 1);
    }

    /// <summary>
    /// Consumes the unconditional joiners that open a token and returns the position after them.
    /// </summary>
    /// <remarks>
    /// A leading joiner belongs to the token even though nothing precedes it for the "word
    /// character on both sides" test to succeed on: <c>_630888</c> indexes whole, <c>_alpha</c> as
    /// <c>_alpha</c>, <c>__alpha</c> as <c>__alpha</c> — while <c>-alpha</c> is just <c>alpha</c>.
    /// The whole run is consumed here because <see cref="ScanWordRun"/> only asks whether a word
    /// continues past a character it is already inside.
    /// </remarks>
    private int ConsumeLeadingJoiners(ReadOnlySpan<char> text, int position)
    {
        while (position < text.Length
            && RuleFor(text[position]) == JoinRule.Always
            && position + 1 < text.Length
            && ContinuesWord(text, position + 1))
        {
            position++;
        }

        return position;
    }

    /// <summary>
    /// Walks the word that starts at <paramref name="position"/> and returns where it ends.
    /// </summary>
    /// <remarks>
    /// ASCII runs are skipped in bulk: everything up to the first non-letter or non-digit is inside
    /// the word. A non-ASCII word character, a combining mark or format character that continues
    /// one, or U+202F advances by its rune length, since a surrogate pair is two chars and the
    /// boundary has to land between runes. An ideograph always ends the run: the reference emits it
    /// alone, so <c>abc尺def</c> is three terms and not one.
    /// </remarks>
    private int ScanWordRun(ReadOnlySpan<char> text, int position)
    {
        while (position < text.Length)
        {
            if (_conditionalJoins && (IsIdeographicAt(text, position) || IsHiraganaAt(text, position)))
                break;

            if (text[position] < 128)
            {
                int relativeEnd = text[position..].IndexOfAnyExcept(AsciiWordChars);

                if (relativeEnd != 0)
                {
                    position += relativeEnd < 0 ? text.Length - position : relativeEnd;
                    continue;
                }
            }
            else if (StartsWordAt(text, position) || ContinuesWordAt(text, position) || IsExtendNumLetAt(text, position))
            {
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

        return position;
    }

    /// <summary>
    /// Emits the word occupying <c>[start, end)</c>, chopped the way the reference's scanner chops a
    /// token longer than its input buffer.
    /// </summary>
    /// <remarks>
    /// The reference tokenizer fills a buffer of 255 UTF-16 units and emits what it has when a token
    /// reaches the end of it, then continues scanning the rest as a new token: a 468-character token
    /// is indexed as one of 255 and one of 213, both terms. That is a property of the scanner, not a
    /// length filter — nothing is dropped — and it is reproduced only for
    /// <see cref="WordSegmentation.UnicodeWordBoundaries"/>, where the scanner being reproduced is
    /// what the mode means. The identifiers analysis also runs through this scan and must keep
    /// emitting a long identifier whole.
    /// <para>
    /// A boundary that would land inside a surrogate pair moves back one unit, because the reference
    /// scanner holds a trailing high surrogate over to the next refill rather than splitting it.
    /// A chunk that begins with a mark or a format character drops it: the scanner that refilled
    /// there cannot open a token on a character that never opens one, so the mark at the boundary
    /// of a 255-unit word disappears while the word continues. Measured: 255 <c>a</c>s, a combining
    /// mark and ten <c>b</c>s index as 255 <c>a</c>s and ten <c>b</c>s.
    /// </para>
    /// </remarks>
    private void EmitWordRun(
        ReadOnlySpan<char> text,
        int start,
        int end,
        List<string>? terms,
        List<TokenSpan>? spans)
    {
        var none = new NormalizedTermsBuilder(default, enabled: false);

        do
        {
            int chunkEnd = _conditionalJoins ? Math.Min(start + MaxTokenLength, end) : end;

            if (chunkEnd < end && chunkEnd - start > 1 && char.IsHighSurrogate(text[chunkEnd - 1]))
                chunkEnd--;

            while (start < chunkEnd && !BeginsWordAt(text, start))
                start++;

            if (start < chunkEnd)
                EmitWord(text, start, chunkEnd, terms, spans, ref none);

            start = chunkEnd;
        }
        while (start < end);
    }

    /// <summary>
    /// Emits a one-code-point term that the reference tokenizes on its own — a configured symbol,
    /// an emoji, an ideograph — through the same normalization, stop-word and stemming pipeline as
    /// a word.
    /// </summary>
    /// <remarks>
    /// <see cref="TokenizerOptions.KeepSingleCharTerms"/> still decides whether it survives, because
    /// that is the option a caller uses to ask for no one-character terms at all; the uax29 analysis
    /// sets it, so every symbol, emoji and ideograph reaches the index.
    /// </remarks>
    private void EmitStandalone(
        ReadOnlySpan<char> text,
        int start,
        int length,
        List<string>? terms,
        List<TokenSpan>? spans)
    {
        if (!_options.KeepSingleCharTerms)
            return;

        string term = NormalizeToken(text.Slice(start, length));

        if (_stopWords is not null && _stopWords.Contains(term))
            return;

        if (_options.Stemmer is not null)
            term = _options.Stemmer.Stem(term);

        terms?.Add(term);
        spans?.Add(new TokenSpan(term, start, length));
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
    /// Whether the character at <paramref name="position"/> continues a word: a combining mark or a
    /// format character does, and neither can start one.
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
    /// <para>
    /// A format character is the same kind of thing for the reference's word-break data: it cannot
    /// open a word and does not close one. FiQA holds two terms that depend on it — a byte-order
    /// mark inside <c>1990</c> and a left-to-right mark after <c>bank</c> — where this library
    /// split the word at the format character and lost the term.
    /// </para>
    /// </remarks>
    private bool ContinuesWordAt(ReadOnlySpan<char> text, int position)
    {
        if (text[position] < 128)
            return false;

        if (Rune.DecodeFromUtf16(text[position..], out Rune rune, out _) != OperationStatus.Done)
            return false;

        // Format characters extend a word only under the measured boundaries, where the reference's
        // word-break data says so. The identifiers analysis shares this scan and keeps its behavior:
        // a format character still ends the identifier there.
        return IsCombiningMark(rune) || (_conditionalJoins && WordBreakClasses.IsFormatExtend(rune));
    }

    /// <summary>
    /// Whether an ideograph starts at <paramref name="position"/> — a term of its own under the
    /// measured boundaries rather than part of the run beside it.
    /// </summary>
    private static bool IsIdeographicAt(ReadOnlySpan<char> text, int position)
    {
        if (text[position] < 0x2E80)
            return false;

        return Rune.DecodeFromUtf16(text[position..], out Rune rune, out _) == OperationStatus.Done
               && WordBreakClasses.IsIdeographic(rune);
    }

    /// <summary>Whether a Hiragana character starts at <paramref name="position"/>.</summary>
    private static bool IsHiraganaAt(ReadOnlySpan<char> text, int position)
    {
        if (text[position] < 0x3041)
            return false;

        return Rune.DecodeFromUtf16(text[position..], out Rune rune, out _) == OperationStatus.Done
               && WordBreakClasses.IsHiragana(rune);
    }

    /// <summary>
    /// Whether the character at <paramref name="position"/> is U+202F, the narrow no-break space,
    /// which the reference's word-break data classes as <c>ExtendNumLet</c> instead of a separator.
    /// </summary>
    /// <remarks>
    /// <c>ExtendNumLet</c> is a small class — the underscore, U+202F, four connector punctuation
    /// characters and their presentation forms — and only U+202F is handled here, because it is the
    /// only member a corpus in this repository has shown to matter: FiQA's section sign, U+202F and
    /// <c>1950.6</c> form one term in the reference, the space and all. The rest can join, split or
    /// be dropped without a measured consequence yet, and adding them speculatively would change
    /// documents no corpus here exercises. Only the measured boundaries consult this; the flat and
    /// identifier analyses still treat the character as a separator.
    /// </remarks>
    private bool IsExtendNumLetAt(ReadOnlySpan<char> text, int position) =>
        _conditionalJoins && text[position] == '\u202F';

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

        return status == OperationStatus.Done
            && Rune.IsLetterOrDigit(rune)
            && length > 0
            && !(_conditionalJoins
                && (WordBreakClasses.IsIdeographic(rune) || WordBreakClasses.IsHiragana(rune)));
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
        // The middle dot is a MidLetter in UAX#29 and a unit separator in practice: `mg·dl` is one
        // token in the reference, and this library split it, which was 16 of the 503 terms it was
        // short of NFCorpus's published count.
        '\u00B7' => JoinRule.BetweenLetters,
        '_' or '­' => JoinRule.Always,
        _ => JoinRule.Never,
    };

    /// <summary>Evaluates <see cref="RuleFor"/> for the separator at <paramref name="position"/>.</summary>
    private bool JoinsUnderCondition(ReadOnlySpan<char> text, int position)
    {
        JoinRule rule = RuleFor(text[position]);

        if (rule == JoinRule.Never)
            return false;

        if (rule == JoinRule.Always)
            return true;

        // Both sides must be word characters, and both are inside the current word or immediately
        // after it, so the left one is the character the separator interrupts.
        if (position + 1 >= text.Length || !StartsWordAt(text, position + 1))
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

    private CharClass ClassAt(ReadOnlySpan<char> text, int index)
    {
        // A combining mark takes its class from the character it modifies. The stop in `tɕɛ̝.bʌl`
        // sits between two letters, but the character immediately before it is U+031D, a mark; the
        // reference joins the full stop on both sides being letters, so the class has to be read
        // past the mark rather than off it. A format character is skipped for the same reason: it
        // belongs to the word it sits in and has no class of its own.
        while (index >= 0 && text[index] >= 128)
        {
            if (Rune.DecodeLastFromUtf16(text[..(index + 1)], out Rune mark, out int markLength) != OperationStatus.Done
                || (!IsCombiningMark(mark) && !WordBreakClasses.IsFormatExtend(mark)))
            {
                break;
            }

            index -= markLength;
        }

        if (index < 0)
            return CharClass.Other;

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

        // Han, Hiragana and Katakana are their own word-break classes in the reference's data, not
        // ALetter: a MidLetter separator between a letter and one of them does not join them. The
        // lines that split the ideograph and the Hiragana into their own terms are not a license to
        // treat them as word letters for the join tests around them.
        if (_conditionalJoins
            && (WordBreakClasses.IsIdeographic(rune)
                || WordBreakClasses.IsHiragana(rune)
                || WordBreakClasses.IsKatakana(rune)))
        {
            return CharClass.Other;
        }

        return Rune.IsLetter(rune) ? CharClass.Letter : CharClass.Other;
    }

    /// <summary>Normalizes, filters and stores one word range, honoring single-char/stop-word/stemming options.</summary>
    private void EmitWord(
        ReadOnlySpan<char> text,
        int start,
        int end,
        List<string>? terms,
        List<TokenSpan>? spans,
        ref NormalizedTermsBuilder normalized)
    {
        int length = end - start;

        if (length < 2 && !_options.KeepSingleCharTerms)
            return;

        if (normalized.Enabled)
        {
            var source = text.Slice(start, length);

            // The term is its own normalized form: the caller can resolve it by span, and no string
            // is built for it. TokenizeNormalized declines every configuration whose pipeline would
            // replace the term, so reaching here means folding is the only thing left to decide, and
            // an ASCII slice with no uppercase character is what folding leaves alone.
            if (IsNormalizedLiteral(source))
            {
                normalized.Add(new NormalizedTerm(start, length, null));
                return;
            }
        }

        string term = NormalizeToken(text.Slice(start, length));

        if (_stripPossessives && EndsWithPossessive(term))
            term = term[..^2];

        if (_stopWords is not null && _stopWords.Contains(term))
            return;

        if (_options.Stemmer is not null)
            term = _options.Stemmer.Stem(term);

        terms?.Add(term);
        spans?.Add(new TokenSpan(term, start, length));

        // A term that had to be normalized, stemmed or trimmed reaches the destination as the string
        // it became. TokenizeNormalized never runs those pipelines, so this is the rare arm.
        if (normalized.Enabled)
            normalized.Add(new NormalizedTerm(0, 0, term));
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

    /// <summary>
    /// The longest token the reference tokenizer emits before it continues with the rest as another
    /// token. Its scanner's buffer is 255 UTF-16 units, so a longer token is chopped at that width —
    /// not dropped, as the claim about Lucene's <c>maxTokenLength</c> is sometimes read.
    /// </summary>
    private const int MaxTokenLength = 255;
}
