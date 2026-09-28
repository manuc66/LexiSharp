using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LexiSharp.Linguistics;
using Xunit;

// CA1861 ("prefer a static readonly field over a constant array argument") is suppressed on
// the lines below. Its premise is a call repeated with the same literal, allocating each
// time. These are one-shot fixtures, and the literal belongs beside the assertion that reads
// it -- hoisting it into a field saves nothing that is measured, and moves the data away
// from the test that fails on it.

namespace LexiSharp.Tests;

/// <summary>
/// Tests for <see cref="PorterStemmer"/>: conformance against the author's published reference
/// vocabulary, the documented input contract, and the properties that hold for any input.
/// </summary>
public class PorterStemmerTests
{
    private static readonly PorterStemmer Stemmer = new();

    /// <summary>
    /// The reference vocabulary published with the algorithm: <c>voc.txt</c> holds one word per
    /// line, <c>output.txt</c> the stem of the word on the same line. See
    /// <c>TestData/Porter/README.md</c> for provenance and licensing.
    /// </summary>
    [Fact]
    public void Stem_MatchesTheAuthorReferenceVocabulary()
    {
        string words = Path.Combine(AppContext.BaseDirectory, "TestData", "Porter", "voc.txt");
        string expected = Path.Combine(AppContext.BaseDirectory, "TestData", "Porter", "output.txt");

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
    // The examples spelled out in the reference implementation.
    [InlineData("caresses", "caress")]
    [InlineData("ponies", "poni")]
    [InlineData("ties", "ti")]
    [InlineData("caress", "caress")]
    [InlineData("cats", "cat")]
    [InlineData("feed", "feed")]
    [InlineData("agreed", "agre")]
    [InlineData("plastered", "plaster")]
    [InlineData("bled", "bled")]
    [InlineData("motoring", "motor")]
    [InlineData("sing", "sing")]
    [InlineData("conflated", "conflat")]
    [InlineData("troubled", "troubl")]
    [InlineData("sized", "size")]
    [InlineData("hopping", "hop")]
    [InlineData("tanned", "tan")]
    [InlineData("falling", "fall")]
    [InlineData("hissing", "hiss")]
    [InlineData("fizzed", "fizz")]
    [InlineData("failing", "fail")]
    [InlineData("filing", "file")]
    [InlineData("happy", "happi")]
    [InlineData("sky", "sky")]
    // The three departures of the reference encoding from the published paper.
    [InlineData("as", "as")]          // the paper would strip the -s
    [InlineData("is", "is")]
    [InlineData("possibly", "possibl")] // "bli" -> "ble" instead of the paper's "abli" -> "able"
    [InlineData("archaeology", "archaeolog")] // the extra "logi" -> "log" rule
    // Search-shaped vocabulary: inflections have to land on a shared stem.
    [InlineData("indexing", "index")]
    [InlineData("indexes", "index")]
    [InlineData("retrieval", "retriev")]
    [InlineData("retrieving", "retriev")]
    [InlineData("documents", "document")]
    [InlineData("running", "run")]
    [InlineData("queries", "queri")]
    [InlineData("normalized", "normal")]
    [InlineData("normalization", "normal")]
    [InlineData("stemming", "stem")]
    [InlineData("stems", "stem")]
    [InlineData("relevance", "relev")]
    [InlineData("repositories", "repositori")]
    // The algorithm over-stems by design — its own FAQ says the goal is to bring variant forms
    // together, not to produce real words. These are the visible price, pinned here so a change
    // in behaviour is a test failure rather than a surprise:
    [InlineData("engine", "engin")]      // the final -e goes, measure 2
    [InlineData("relate", "relat")]
    [InlineData("relational", "relat")]
    public void Stem_FoldsInflections(string word, string expected) =>
        Assert.Equal(expected, Stemmer.Stem(word));

    [Theory]
    [InlineData("")]        // nothing to strip
    [InlineData("a")]       // one letter: left alone
    [InlineData("is")]      // two letters: left alone, unlike the published paper
    [InlineData("café")]   // non-ASCII: the tokenizer removes diacritics before this point
    [InlineData("naïve")]
    [InlineData("中文")]
    [InlineData("aaron")]   // no suffix to strip: returned unchanged
    [InlineData("search")]  // ... including words that merely end in a letter the steps test for
    [InlineData("index")]   // ... and words that look like stop words
    [InlineData("the")]
    public void Stem_ReturnsTermsOutsideTheAlgorithmUnchanged(string term) =>
        Assert.Equal(term, Stemmer.Stem(term));

    [Fact]
    public void Stem_ReturnsTheSameInstanceWhenNothingIsStripped()
    {
        // The allocation claim in the documentation: an untouched term costs no new string.
        const string term = "search";

        Assert.Same(term, Stemmer.Stem(term));
        Assert.NotSame("caresses", Stemmer.Stem("caresses"));
    }

    [Fact]
    public void Stem_KeepsDigitsAsConsonants()
    {
        Assert.Equal("covid19", Stemmer.Stem("covid19"));
        Assert.Equal("utf8", Stemmer.Stem("utf8s"));
    }

    [Fact]
    public void Stem_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => Stemmer.Stem(null!));
    }

    [Fact]
    public void Stem_ThroughTheTokenizerFoldsTheDocumentAndTheQuery()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new PorterStemmer() });

        Assert.Equal(new[] { "index" }, tokenizer.Tokenize("indexes")); // NOSONAR:CA1861
        Assert.Equal(new[] { "index" }, tokenizer.Tokenize("indexing")); // NOSONAR:CA1861
        Assert.Equal(new[] { "index" }, tokenizer.Tokenize("Indexes")); // NOSONAR:CA1861
        Assert.Equal(new[] { "retriev", "retriev" }, tokenizer.Tokenize("retrieval, retrieving")); // NOSONAR:CA1861
    }

    // A small alphabet keeps the generator cheap while still reaching every step; 'y' is what
    // drives the consonant/vowel rule, so it has to be in.
    private const string Alphabet = "abceghilmnoprstuy";

    private static readonly Arbitrary<string> Words =
        Arb.From(Gen.Choose(0, 24).Select(
            len => new string(
                Enumerable.Range(0, len)
                    .Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)])
                    .ToArray())));

    [PropertyAttribute]
    public Property Stem_NeverReturnsALongerTerm() =>
        Prop.ForAll(Words, word => Stemmer.Stem(word).Length <= word.Length);

    [PropertyAttribute]
    public Property Stem_NeverEmptiesATerm() =>
        Prop.ForAll(Words, word => word.Length < 3 || Stemmer.Stem(word).Length >= 1);

    [PropertyAttribute]
    public Property Stem_KeepsTheInputAlphabet() =>
        Prop.ForAll(Words, word =>
        {
            string stem = Stemmer.Stem(word);

            return stem.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
        });

    [Fact]
    public void Stem_IsDeterministicUnderConcurrency()
    {
        // The documented "one instance can be reused" claim, checked rather than asserted: the
        // same instance is shared by concurrent callers and must return the sequential results.
        string[] words = File
            .ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "Porter", "voc.txt"))
            .Where(w => w.Length > 0 && w.Length < 12)
            .Take(2000)
            .ToArray();

        string[] expected = words.Select(PorterStemmer.Default.Stem).ToArray();

        var results = new string[words.Length][];

        Parallel.For(0, 4, worker =>
        {
            var local = new string[words.Length];

            for (int i = worker; i < words.Length; i += 4)
                local[i] = Stemmer.Stem(words[i]);

            results[worker] = local;
        });

        for (int i = 0; i < words.Length; i++)
            Assert.Equal(expected[i], results[i % 4][i]);
    }
}
