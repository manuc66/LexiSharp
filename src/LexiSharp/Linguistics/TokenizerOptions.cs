namespace LexiSharp.Linguistics;

/// <summary>
/// Configuration knobs for the <see cref="Tokenizer"/>.
/// </summary>
public sealed record TokenizerOptions
{
    /// <summary>A default, conservative configuration: lowercase, strip accents, keep words of length ≥ 2.</summary>
    public static TokenizerOptions Default { get; } = new();

    /// <summary>Ignore tokens belonging to the stop word set (see <see cref="StopWords"/>). Default: <c>false</c>.</summary>
    public bool RemoveStopWords { get; init; }

    /// <summary>
    /// Optional stemming applied to each term. When null, terms are left unstemmed, which is the
    /// default. <see cref="PorterStemmer"/> (English) ships with the library; for another
    /// language or algorithm, provide your own <see cref="IStemmer"/>.
    /// </summary>
    public IStemmer? Stemmer { get; init; }

    /// <summary>Smallest n-gram size produced (1 = unigrams only). Default: <c>1</c>.</summary>
    public int NGramMin { get; init; } = 1;

    /// <summary>Largest n-gram size produced. Default: <c>1</c>.</summary>
    public int NGramMax { get; init; } = 1;

    /// <summary>Keep tokens of a single character. Default: <c>false</c>.</summary>
    public bool KeepSingleCharTerms { get; init; }

    /// <summary>Custom stop word set (learned as a case-insensitive set). Ignored until <see cref="RemoveStopWords"/> is set.</summary>
    public IReadOnlySet<string>? StopWords { get; init; }

    /// <summary>
    /// Characters that join a word instead of ending it, when a word character sits on
    /// <b>both</b> sides. The value is the set of characters themselves, so
    /// <c>".,'’"</c> makes <c>data.worldbank.org</c> and <c>don't</c> single terms while
    /// <c>hello.</c> still ends at the period. Default: none, so every non-word character ends a
    /// word.
    /// </summary>
    /// <remarks>
    /// There is a published segmentation that does exactly this — the Unicode text-segmentation
    /// word boundaries — and its joiner set is what this option exists to reproduce, because the
    /// choice of word boundaries changes document frequencies, and document frequencies change
    /// every score that weights a term. Comparing against a system segmented differently is not a
    /// comparison of ranking quality, and this is the switch that makes the comparison honest.
    /// <para>
    /// The cost is not free and is not yet measured: a word boundary now depends on the character
    /// that follows a non-word character, which the bulk ASCII skip cannot answer on its own, so
    /// enabling this takes the scan off that path. The default path is untouched.
    /// </para>
    /// </remarks>
    public string? WordJoiners { get; init; }

    /// <summary>
    /// Which characters join a word under which condition, when the flat <see cref="WordJoiners"/> set
    /// is not enough. Default: <see cref="WordSegmentation.Flat"/>, which joins nothing.
    /// </summary>
    /// <remarks>
    /// A joiner set on its own is not enough, because whether a character joins depends on what sits
    /// either side of it. Measured against the implementation whose published figures this option
    /// exists to reproduce: a comma joins two digits and splits two letters; a colon does the exact
    /// opposite; a full stop and an apostrophe do both; an underscore joins anything. Flattening all
    /// of those into one set gets <c>1,2,3</c> right and <c>0335204279.pdf</c> wrong — and being wrong
    /// on one word out of a million still changes document frequencies, which changes every score.
    /// <para>
    /// What is implemented is the set of characters and conditions that were <b>measured</b>, not the
    /// whole of the Unicode annex those rules come from: eighteen separators, across digit and letter
    /// on both sides. Characters outside that set — an Arabic comma, an ideographic full stop, a
    /// Hangul syllable — are not implemented, because nothing has established here what they do.
    /// </para>
    /// </remarks>
    public WordSegmentation WordSegmentation { get; init; } = WordSegmentation.Flat;

    /// <summary>
    /// Strip diacritics, so <c>café</c> indexes as <c>cafe</c>. Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Folding merges spellings, which raises the document frequency of the folded term and makes
    /// accented and unaccented spellings match each other. That is usually what an English index
    /// wants, and it is a measurement rather than a correctness question: on BEIR ArguAna, folding
    /// on and off moves BM25 nDCG@10 by about 0.003 in the same direction, which is why an analysis
    /// that claims to reproduce a published figure has to state which way it went.
    /// </remarks>
    public bool FoldDiacritics { get; init; } = true;

    /// <summary>
    /// Remove a trailing possessive <c>'s</c> or <c>’s</c> from a token, before the stop word list is
    /// tested. Default: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The position in the pipeline is the whole point, and it was not obvious. Trimming the possessive
    /// inside the stemmer works and is wrong: by then the stop word list has already been consulted, and
    /// it was consulted on <c>it's</c>, not on <c>it</c>. So <c>it's</c> survives as a stop word it
    /// should never have been, and only afterwards becomes <c>it</c> — a term that indexes a word the
    /// caller asked to remove.
    /// <para>
    /// Measured against the implementation whose published figures this option exists to reproduce, on
    /// BEIR ArguAna: trimming inside the stemmer indexed <c>it</c> 3 501 times where the reference has
    /// 3 134, and indexed a <c>that</c> (83 occurrences) and a <c>there</c> (36) the reference does not
    /// have at all. Trimming here accounts for all three to the occurrence: the reference removes
    /// <c>it's</c> as a stop word, and its <c>it</c> comes from <c>its</c>, which is not a possessive
    /// and is not on the list.
    /// </para>
    /// <para>
    /// Only the two-character suffix counts. <c>aren't</c> and <c>ba'ath</c> are left alone, and a
    /// trailing apostrophe is not part of the word at all — a tokenizer that joins an apostrophe only
    /// between word characters never produces one.
    /// </para>
    /// </remarks>
    public bool StripPossessives { get; init; }

    /// <summary>
    /// Whether a term containing U+0130 — the Turkish dotted capital I — is lowercased to a plain
    /// <c>i</c>. Default: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// One character, and it is the only one. Measured against the reference implementation's own
    /// lowercasing over the eight ranges a European corpus contains — 505 code points whose lowercase
    /// differs from themselves — this is the single position where its answer and .NET's invariant casing
    /// disagree.
    /// <para>
    /// Which lowercase matters as much as which character. The Unicode <b>simple</b> mapping sends U+0130
    /// to U+0069, one character; the <b>full</b> mapping sends it to U+0069 U+0307, adding a combining dot
    /// above. The reference index holds a five-character <c>celil</c>, and feeding it the six-character
    /// <c>celi̇l</c> returns the dot intact — so its pipeline uses the simple mapping, and so must this
    /// one. U+01F0 settles it independently: the full mapping would decompose it into <c>j</c> plus a
    /// caron, and it comes back as a single U+01F0.
    /// </para>
    /// <para>
    /// That is not a bug in either implementation. Invariant casing is the right answer for a Turkish
    /// index, where <c>İ</c> and <c>i</c> are different letters and folding them together merges two
    /// words. It is the wrong answer for a language-independent index, and it is why an analysis
    /// reproducing a published figure has to say which of the two it used.
    /// </para>
    /// <para>
    /// On BEIR ArguAna this is one term in 23,895 — <c>celİl</c> against <c>celil</c> — and it changes
    /// no document frequency, no term frequency and no score. It is here because the vocabulary is then
    /// identical term for term, and a vocabulary that differs by one name is a difference a reader has to
    /// account for rather than one they can assume away.
    /// </para>
    /// </remarks>
    public bool FoldTurkishDottedI { get; init; }

    /// <summary>The active stop word set: an explicit set if provided, otherwise <see cref="Linguistics.StopWords.English"/>.</summary>
    public IReadOnlySet<string> GetStopWords() => StopWords ?? Linguistics.StopWords.English;

    internal TokenizerOptions Sanitize() => this with
    {
        NGramMin = Math.Max(1, NGramMin),
        NGramMax = Math.Max(Math.Max(1, NGramMin), NGramMax),
    };
}
/// <summary>
/// How a character between two word characters affects the word, when <see cref="TokenizerOptions.WordSegmentation"/>
/// is not <see cref="Flat"/>.
/// </summary>
public enum WordSegmentation
{
    /// <summary>Every non-word character ends a word. The default, and what a plain tokenizer means.</summary>
    Flat = 0,

    /// <summary>
    /// A measured table of separators and the condition each one joins under. Use it to match an
    /// implementation that segments on these boundaries; the flat set cannot express the conditions.
    /// </summary>
    UnicodeWordBoundaries = 1,
}
