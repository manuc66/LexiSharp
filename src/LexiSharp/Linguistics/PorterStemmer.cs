using System.Buffers;
using System.Text;

namespace LexiSharp.Linguistics;

/// <summary>
/// English stemmer implementing Porter's suffix-stripping algorithm: an <see cref="IStemmer"/>
/// that folds inflected English forms onto a common stem (<c>caresses</c> → <c>caress</c>,
/// <c>motoring</c> → <c>motor</c>, <c>hopping</c> → <c>hop</c>).
/// </summary>
/// <remarks>
/// <para>
/// Written for this library against the canonical ANSI C encoding published by the algorithm's
/// author — M.F. Porter, "An algorithm for suffix stripping", <i>Program</i> 14(3):130-137
/// (1980), reference implementation at
/// <see href="https://www.tartarus.org/~martin/PorterStemmer/">tartarus.org/~martin/PorterStemmer</see>.
/// That page states the encodings are "free of charge for any purpose" and that the licensing is
/// never more restrictive than the BSD license; the reference is used here as the specification,
/// including its three marked departures from the published paper: <c>logi</c> → <c>log</c> and
/// <c>bli</c> → <c>ble</c> in step 2, and words of one or two letters left alone. Conformance is
/// pinned by a test against the author's own reference vocabulary (23,531 words).
/// </para>
/// <para>
/// <b>Scope.</b> English only, and only the algorithm's English rules — this is not a Snowball
/// stemmer. Porter describes the algorithm as "slightly inferior to the Snowball English or
/// Porter2 stemmer" and "frozen". It over-stems by design, his FAQ being that the goal is to
/// bring variant forms together, not to produce real words: <c>relate</c>, <c>relates</c> and
/// <c>relational</c> all become <c>relat</c>, and <c>engine</c> becomes <c>engin</c>. No claim is
/// made here about relevance on your data: the retrieval effect is measured only on NFCorpus
/// and SciFact, in the evaluation harness, and over-stemming is a known way to lose precision.
/// </para>
/// <para>
/// <b>Input contract.</b> The algorithm is defined for lowercase ASCII. Terms that are shorter
/// than three characters or contain a non-ASCII character are returned unchanged — the tokenizer
/// has already removed diacritics and lowercased by the time terms reach a stemmer, so this
/// guard only fires for direct callers. Uppercase letters are treated as consonants, exactly as
/// the reference does, so lowercase the input first. Digits count as consonants.
/// </para>
/// <para>
/// <b>Cost.</b> The term is copied into a pooled buffer, because the algorithm rewrites in place.
/// The only allocation a call makes is the stem string, and only when the stem differs from the
/// input: a term with no matching suffix is returned as the very same instance. No state is
/// shared between calls, so one instance can be reused across threads. The added cost of
/// stemming is otherwise unmeasured — the evaluation harness reports wall-clock time per
/// config, but a single run is not timing evidence.
/// </para>
/// </remarks>
public sealed class PorterStemmer : IStemmer
{
    /// <summary>Words of one or two letters are left alone (a departure from the published paper).</summary>
    private const int MinimumLength = 3;

    /// <summary>A shared instance: the type keeps no mutable state, so there is nothing to construct per call site.</summary>
    public static PorterStemmer Default { get; } = new();

    /// <inheritdoc />
    /// <remarks>
    /// A possessive is <b>not</b> removed here. The published algorithm removes one as its first step,
    /// but where that removal happens decides what else sees the word: <see cref="TokenizerOptions.StripPossessives"/>
    /// does it, and it has to, because a stop word list is tested between this stage and the tokenizer.
    /// A term that reached this method as <c>Adam's</c> becomes <c>Adam'</c>, which is a word no query
    /// will ever contain.
    /// </remarks>
    public string Stem(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        if (term.Length < MinimumLength)
            return term;

        char[] buffer = ArrayPool<char>.Shared.Rent(term.Length);

        try
        {
            // The algorithm rewrites the word in place: suffixes are overwritten by their
            // replacement, so the buffer has to hold the whole original word, not just the stem.
            Span<char> word = buffer.AsSpan(0, term.Length);
            term.AsSpan().CopyTo(word);

            int length = Stem(word);

            // Unchanged words keep their original instance — see the remarks on allocation.
            return word[..length].SequenceEqual(term) ? term : new string(word[..length]);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Stems a mutable ASCII word in place and returns the length of the resulting stem.
    /// </summary>
    /// <param name="word">The word to stem; never longer than the buffer it came from.</param>
    private static int Stem(Span<char> word)
    {
        // k is the index of the last character of the stem (inclusive), never decreasing, and
        // j is written by Ends as the index of the last character preceding the matched suffix.
        int k = word.Length - 1;
        int j = 0;

        // Words of one or two letters are left alone: a departure from the published algorithm.
        if (word.Length < MinimumLength)
            return word.Length;

        Step1ab(word, ref k, ref j);

        // After 1ab the stem can be a single letter, and the remaining steps would read before
        // the start of the word; the reference guards them the same way.
        if (k > 0)
        {
            Step1c(word, ref k, ref j);
            Step2(word, ref k, ref j);
            Step3(word, ref k, ref j);
            Step4(word, ref k, ref j);
            Step5(word, ref k, ref j);
        }

        return k + 1;
    }

    /// <summary>True when <paramref name="word"/> holds a consonant at <paramref name="i"/>.</summary>
    private static bool Consonant(ReadOnlySpan<char> word, int i) =>
        word[i] switch
        {
            'a' or 'e' or 'i' or 'o' or 'u' => false,
            // 'y' is a consonant at the start of a word, and elsewhere a consonant exactly when
            // the letter before it is a vowel.
            'y' => i == 0 || !Consonant(word, i - 1),
            _ => true,
        };

    /// <summary>
    /// The measure <c>m</c> of <c>word[0..j]</c>: how many vowel-consonant sequences it holds.
    /// </summary>
    /// <remarks>
    /// A leading consonant run measures 0, so <c>&lt;c&gt;&lt;v&gt;</c> gives 0, <c>&lt;c&gt;vc&lt;v&gt;</c>
    /// gives 1, and each further <c>vc</c> pair adds one. The measure is what every rule in steps
    /// 1b to 5 tests: <c>m &gt; 0</c> means "there is a real word here", <c>m &gt; 1</c> means
    /// "enough of one to strip an ending".
    /// </remarks>
    private static int Measure(ReadOnlySpan<char> word, int j)
    {
        int count = 0;
        int i = 0;

        // Skip the leading consonant run: it is not part of any sequence.
        while (i <= j && Consonant(word, i))
            i++;

        i++;

        while (true)
        {
            while (i <= j && !Consonant(word, i))
                i++;

            if (i > j)
                return count;

            i++;
            count++;

            while (i <= j && Consonant(word, i))
                i++;

            if (i > j)
                return count;

            i++;
        }
    }

    /// <summary>True when <c>word[0..j]</c> holds at least one vowel.</summary>
    private static bool VowelInStem(ReadOnlySpan<char> word, int j)
    {
        for (int i = 0; i <= j; i++)
        {
            if (!Consonant(word, i))
                return true;
        }

        return false;
    }

    /// <summary>True when the two characters ending at <paramref name="j"/> are one doubled consonant.</summary>
    private static bool DoubleConsonant(ReadOnlySpan<char> word, int j) =>
        j >= 1 && word[j] == word[j - 1] && Consonant(word, j);

    /// <summary>
    /// True when the three characters ending at <paramref name="i"/> form a
    /// consonant-vowel-consonant whose last letter is not <c>w</c>, <c>x</c> or <c>y</c>.
    /// </summary>
    /// <remarks>
    /// This is the short-syllable test that decides whether a dropped <c>-e</c> has to be put
    /// back: <c>cav(e)</c>, <c>lov(e)</c>, <c>crim(e)</c> get it, <c>snow</c>, <c>box</c> and
    /// <c>tray</c> do not.
    /// </remarks>
    private static bool ShortSyllable(ReadOnlySpan<char> word, int i) =>
        i >= 2 &&
        Consonant(word, i) &&
        !Consonant(word, i - 1) &&
        Consonant(word, i - 2) &&
        word[i] is not ('w' or 'x' or 'y');

    /// <summary>
    /// True when the word ends with <paramref name="suffix"/>, in which case
    /// <paramref name="j"/> receives the index of the last character before it.
    /// </summary>
    /// <remarks>
    /// <paramref name="j"/> is set to 0 when there is no match; every caller reads it only after a
    /// successful one, mirroring the reference implementation's single global offset.
    /// </remarks>
    private static bool Ends(ReadOnlySpan<char> word, int k, string suffix, out int j)
    {
        int length = suffix.Length;

        // Rejecting on the last character first is the reference implementation's speed-up: most
        // calls are decided by this one comparison.
        if (word[k] != suffix[^1] || length > k + 1)
        {
            j = 0;
            return false;
        }

        j = k - length;

        // The suffix starts one past j — j is -1 when the suffix is the whole remaining word.
        return word.Slice(j + 1, length).SequenceEqual(suffix);
    }

    /// <summary>Overwrites the suffix after <paramref name="j"/> with <paramref name="replacement"/>.</summary>
    private static void SetTo(Span<char> word, ref int k, int j, string replacement)
    {
        replacement.AsSpan().CopyTo(word.Slice(j + 1));
        k = j + replacement.Length;
    }

    /// <summary>
    /// Applies a step 2 or 3 replacement, which the algorithm only allows when the stem before
    /// the suffix has a positive measure.
    /// </summary>
    private static void Replace(Span<char> word, ref int k, int j, string replacement)
    {
        if (Measure(word, j) > 0)
            SetTo(word, ref k, j, replacement);
    }

    /// <summary>Steps 1a and 1b: plurals, then <c>-ed</c> and <c>-ing</c>.</summary>
    private static void Step1ab(Span<char> word, ref int k, ref int j)
    {
        // 1a: -sses -> -ss, -ies -> -i, and a final -s that is not the second of a doubled pair.
        if (word[k] == 's')
        {
            if (Ends(word, k, "sses", out j))
                k -= 2;
            else if (Ends(word, k, "ies", out j))
                SetTo(word, ref k, j, "i");
            else if (word[k - 1] != 's')
                k--;
        }

        // 1b: -eed only shortens when something is left of the stem (-feed stays -feed,
        // -agreed becomes -agre); -ed and -ing only go when a vowel precedes them.
        if (Ends(word, k, "eed", out j))
        {
            if (Measure(word, j) > 0)
                k--;
        }
        else if ((Ends(word, k, "ed", out j) || Ends(word, k, "ing", out j)) && VowelInStem(word, j))
        {
            k = j;

            if (Ends(word, k, "at", out j))
                SetTo(word, ref k, j, "ate");
            else if (Ends(word, k, "bl", out j))
                SetTo(word, ref k, j, "ble");
            else if (Ends(word, k, "iz", out j))
                SetTo(word, ref k, j, "ize");
            else if (DoubleConsonant(word, k))
            {
                k--;

                // -ll, -ss and -zz keep both letters, every other doubled consonant loses one:
                // -hopping -> -hop, -filing -> -file.
                if (word[k] is 'l' or 's' or 'z')
                    k++;
            }
            else if (Measure(word, k) == 1 && ShortSyllable(word, k))
            {
                // j is k here, so this appends the -e that keeps a short syllable closed.
                word[k + 1] = 'e';
                k++;
            }
        }
    }

    /// <summary>Step 1c: a final <c>-y</c> becomes <c>-i</c> when the stem holds another vowel.</summary>
    private static void Step1c(Span<char> word, ref int k, ref int j)
    {
        if (Ends(word, k, "y", out j) && VowelInStem(word, j))
            word[k] = 'i';
    }

    /// <summary>Step 2: double suffixes to single ones (<c>-ational</c> → <c>-ate</c>).</summary>
    private static void Step2(Span<char> word, ref int k, ref int j)
    {
        // The switch is the reference's speed-up, and part of its semantics: only suffixes
        // sharing the second-to-last letter of the word are considered, and at most one rule
        // fires per step — the longest match, whether or not its measure condition holds.
        switch (word[k - 1])
        {
            case 'a':
                if (Ends(word, k, "ational", out j)) { Replace(word, ref k, j, "ate"); break; }
                if (Ends(word, k, "tional", out j)) { Replace(word, ref k, j, "tion"); break; }
                break;
            case 'c':
                if (Ends(word, k, "enci", out j)) { Replace(word, ref k, j, "ence"); break; }
                if (Ends(word, k, "anci", out j)) { Replace(word, ref k, j, "ance"); break; }
                break;
            case 'e':
                if (Ends(word, k, "izer", out j)) { Replace(word, ref k, j, "ize"); break; }
                break;
            case 'l':
                // "bli" rather than the paper's "abli" is a marked departure of the reference.
                if (Ends(word, k, "bli", out j)) { Replace(word, ref k, j, "ble"); break; }
                if (Ends(word, k, "alli", out j)) { Replace(word, ref k, j, "al"); break; }
                if (Ends(word, k, "entli", out j)) { Replace(word, ref k, j, "ent"); break; }
                if (Ends(word, k, "eli", out j)) { Replace(word, ref k, j, "e"); break; }
                if (Ends(word, k, "ousli", out j)) { Replace(word, ref k, j, "ous"); break; }
                break;
            case 'o':
                if (Ends(word, k, "ization", out j)) { Replace(word, ref k, j, "ize"); break; }
                if (Ends(word, k, "ation", out j)) { Replace(word, ref k, j, "ate"); break; }
                if (Ends(word, k, "ator", out j)) { Replace(word, ref k, j, "ate"); break; }
                break;
            case 's':
                if (Ends(word, k, "alism", out j)) { Replace(word, ref k, j, "al"); break; }
                if (Ends(word, k, "iveness", out j)) { Replace(word, ref k, j, "ive"); break; }
                if (Ends(word, k, "fulness", out j)) { Replace(word, ref k, j, "ful"); break; }
                if (Ends(word, k, "ousness", out j)) { Replace(word, ref k, j, "ous"); break; }
                break;
            case 't':
                if (Ends(word, k, "aliti", out j)) { Replace(word, ref k, j, "al"); break; }
                if (Ends(word, k, "iviti", out j)) { Replace(word, ref k, j, "ive"); break; }
                if (Ends(word, k, "biliti", out j)) { Replace(word, ref k, j, "ble"); break; }
                break;
            case 'g':
                // The "logi" rule is the third marked departure: it is what equates
                // "archaeology" with "archaeological".
                if (Ends(word, k, "logi", out j)) { Replace(word, ref k, j, "log"); break; }
                break;
        }
    }

    /// <summary>Step 3: <c>-icate</c>, <c>-ative</c>, <c>-alize</c>, <c>-iciti</c>, <c>-ical</c>, <c>-ful</c>, <c>-ness</c>.</summary>
    private static void Step3(Span<char> word, ref int k, ref int j)
    {
        switch (word[k])
        {
            case 'e':
                if (Ends(word, k, "icate", out j)) { Replace(word, ref k, j, "ic"); break; }
                if (Ends(word, k, "ative", out j)) { Replace(word, ref k, j, ""); break; }
                if (Ends(word, k, "alize", out j)) { Replace(word, ref k, j, "al"); break; }
                break;
            case 'i':
                if (Ends(word, k, "iciti", out j)) { Replace(word, ref k, j, "ic"); break; }
                break;
            case 'l':
                if (Ends(word, k, "ical", out j)) { Replace(word, ref k, j, "ic"); break; }
                if (Ends(word, k, "ful", out j)) { Replace(word, ref k, j, ""); break; }
                break;
            case 's':
                if (Ends(word, k, "ness", out j)) { Replace(word, ref k, j, ""); break; }
                break;
        }
    }

    /// <summary>Step 4: <c>-ant</c>, <c>-ence</c>, <c>-ent</c> and friends, only past a measure of 1.</summary>
    private static void Step4(Span<char> word, ref int k, ref int j)
    {
        switch (word[k - 1])
        {
            case 'a':
                if (!Ends(word, k, "al", out j))
                    return;
                break;
            case 'c':
                if (!Ends(word, k, "ance", out j) && !Ends(word, k, "ence", out j))
                    return;
                break;
            case 'e':
                if (!Ends(word, k, "er", out j))
                    return;
                break;
            case 'i':
                if (!Ends(word, k, "ic", out j))
                    return;
                break;
            case 'l':
                if (!Ends(word, k, "able", out j) && !Ends(word, k, "ible", out j))
                    return;
                break;
            case 'n':
                if (!Ends(word, k, "ant", out j) && !Ends(word, k, "ement", out j) &&
                    !Ends(word, k, "ment", out j) && !Ends(word, k, "ent", out j))
                    return;
                break;
            case 'o':
                // "-sion" and "-tion" keep their s or t: only a stem of measure > 1 that ends in
                // s or t loses "-ion". The j >= 0 guard is the reference's fix for a word "ion".
                if (Ends(word, k, "ion", out j) && j >= 0 && word[j] is 's' or 't')
                    break;

                if (!Ends(word, k, "ou", out j))
                    return;

                break;
            case 's':
                if (!Ends(word, k, "ism", out j))
                    return;
                break;
            case 't':
                if (!Ends(word, k, "ate", out j) && !Ends(word, k, "iti", out j))
                    return;
                break;
            case 'u':
                if (!Ends(word, k, "ous", out j))
                    return;
                break;
            case 'v':
                if (!Ends(word, k, "ive", out j))
                    return;
                break;
            case 'z':
                if (!Ends(word, k, "ize", out j))
                    return;
                break;
            default:
                return;
        }

        if (Measure(word, j) > 1)
            k = j;
    }

    /// <summary>Step 5: drop a final <c>-e</c> that is not holding a short syllable, and <c>-ll</c> to <c>-l</c>.</summary>
    private static void Step5(Span<char> word, ref int k, ref int j)
    {
        // j is captured before the possible -e removal, as in the reference.
        j = k;

        if (word[k] == 'e')
        {
            int measure = Measure(word, k);

            if (measure > 1 || (measure == 1 && !ShortSyllable(word, k - 1)))
                k--;
        }

        if (word[k] == 'l' && DoubleConsonant(word, k) && Measure(word, j) > 1)
            k--;
    }
}
