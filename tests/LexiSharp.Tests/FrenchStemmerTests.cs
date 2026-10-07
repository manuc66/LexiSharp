using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Tests for <see cref="FrenchStemmer"/>: conformance against the Snowball project's published
/// reference vocabulary, the documented input contract, and the properties any stemmer should
/// satisfy.
/// </summary>
public class FrenchStemmerTests
{
    private static readonly FrenchStemmer Stemmer = new();

    /// <summary>
    /// The reference vocabulary published with the algorithm: <c>voc.txt</c> holds one word per
    /// line, <c>output.txt</c> the stem of the word on the same line. See
    /// <c>TestData/SnowballFrench/README.md</c> for provenance and licensing.
    /// </summary>
    [Fact]
    public void Stem_MatchesTheSnowballReferenceVocabulary()
    {
        string words = Path.Combine(AppContext.BaseDirectory, "TestData", "SnowballFrench", "voc.txt");
        string expected = Path.Combine(AppContext.BaseDirectory, "TestData", "SnowballFrench", "output.txt");

        string[] inputs = File.ReadAllLines(words);
        string[] outputs = File.ReadAllLines(expected);

        Assert.Equal(inputs.Length, outputs.Length);
        Assert.NotEmpty(inputs);

        var mismatches = new List<string>();

        for (int i = 0; i < inputs.Length; i++)
        {
            string actual = Stemmer.Stem(inputs[i]);

            if (!string.Equals(actual, outputs[i], StringComparison.Ordinal))
                mismatches.Add($"{inputs[i]} -> {actual} (expected {outputs[i]})");
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {inputs.Length} words differ from the reference output:{Environment.NewLine}"
                + string.Join(Environment.NewLine, mismatches.Take(20)));
    }

    [Theory]
    // Every expectation below is taken from the project's own voc.txt/output.txt rather than from
    // memory: yeux and quand are *not* folded, contorsions stops at contors. Guessing what a
    // stemmer "should" do is how a test ends up asserting a claim the reference never makes.
    [InlineData("jouer", "jou")]          // u between vowels is marked, then -er goes
    [InlineData("ennuie", "ennui")]       // i between vowels is marked, then -e goes
    [InlineData("yeux", "yeux")]          // marked, but nothing here folds it away
    [InlineData("quand", "quand")]        // u after q is marked, and no suffix follows
    [InlineData("croyiez", "croi")]       // a marked Y is not a vowel, so the i beside it is untouched
    [InlineData("continu", "continu")]
    [InlineData("continua", "continu")]
    [InlineData("continuera", "continu")]
    [InlineData("continuer", "continu")]
    [InlineData("contorsions", "contors")]
    [InlineData("contractait", "contract")]
    [InlineData("contradictoires", "contradictoir")]
    [InlineData("contraindre", "contraindr")]
    [InlineData("contrainte", "contraint")]
    [InlineData("main", "main")]
    [InlineData("mains", "main")]
    [InlineData("maintenaient", "mainten")]
    [InlineData("maintint", "maintint")]
    [InlineData("maison", "maison")]
    [InlineData("maladresse", "maladress")]
    [InlineData("maladroite", "maladroit")]
    public void Stem_FoldsInflectedForms(string input, string expected) =>
        Assert.Equal(expected, Stemmer.Stem(input));

    [Theory]
    // The first step removes elisions, so a word that begins with one cannot be stemmed before
    // it is stripped — this is what pins that the step exists and runs first.
    [InlineData("qu'on", "on")]
    [InlineData("l'eau", "eau")]
    public void Stem_StripsLeadingElisions(string input, string expected) =>
        Assert.Equal(expected, Stemmer.Stem(input));

    [Fact]
    public void ReferenceOutputs_AreNotFixedPoints_WhichIsWhyNoIdempotencyTestExists()
    {
        // An earlier draft of this file asserted Stem(Stem(x)) == Stem(x). It failed on 1843 of
        // 21653 words — and the cause was not the implementation: the reference's *own* output is
        // not a fixed point of the reference algorithm. abraviations -> abrivi -> abrev, acacia ->
        // acaci -> acac, acceder -> accredit -> accred: a second pass runs rules the first pass
        // put the word into reach of.
        //
        // So idempotency is not a property of the Snowball French stemmer, and asserting it was
        // asserting something false about someone else's algorithm. This test pins the reason the
        // other one is absent, so it does not come back.
        string[] inputs = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "TestData", "SnowballFrench", "voc.txt"));
        string[] outputs = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "TestData", "SnowballFrench", "output.txt"));

        var stemmer = new FrenchStemmer();
        int unstable = 0;

        for (int i = 0; i < inputs.Length; i++)
        {
            if (!string.Equals(stemmer.Stem(outputs[i]), outputs[i], StringComparison.Ordinal))
                unstable++;
        }

        Assert.True(
            unstable > 0,
            "Every reference output is a fixed point, so an idempotency assertion would have been "
                + "correct after all — re-add one.");
    }

    [Fact]
    public void Stem_NeverReturnsNothing()
    {
        // A stemmer that ate its input would silently empty an index.
        string[] inputs = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "TestData", "SnowballFrench", "voc.txt"));

        var emptied = inputs.Where(input => input.Length > 0 && Stemmer.Stem(input).Length == 0);

        Assert.Empty(emptied);
    }

    [Fact]
    public void Stem_NullOrEmpty_IsReturnedUnchanged()
    {
        Assert.Equal(string.Empty, Stemmer.Stem(string.Empty));
        Assert.Null(Stemmer.Stem(null!));
    }

    [Fact]
    public void Stem_IsEqualToLengthOfOrShorterThanItsInput()
    {
        // Elisions remove characters, but no rule lengthens a word: the postlude restores
        // markers rather than adding them, so a stem is never longer than what was fed in.
        string[] inputs = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "TestData", "SnowballFrench", "voc.txt"));

        var grown = inputs
            .Where(input => Stemmer.Stem(input).Length > input.Length)
            .Take(10)
            .ToList();

        Assert.Empty(grown);
    }

    [Fact]
    public void StopWords_French_IsPopulated()
    {
        // The list ships from the same project as the stemmer, so a reader who has one has the
        // other and does not have to go looking for a stop word list to pair with FrenchStemmer.
        Assert.NotNull(StopWords.French);
        Assert.NotEmpty(StopWords.French);
        Assert.Contains("dans", StopWords.French);
        Assert.Contains("avec", StopWords.French);
        Assert.Contains("vous", StopWords.French);

        // The list omits a word precisely because it is a homonym of an ordinary content word —
        // été, est, son, as, avions. Pinned because dropping a word whose source deliberately
        // keeps it would be a silent divergence from the list this claims to ship.
        Assert.DoesNotContain("été", StopWords.French);
        Assert.DoesNotContain("son", StopWords.French);
    }
}
