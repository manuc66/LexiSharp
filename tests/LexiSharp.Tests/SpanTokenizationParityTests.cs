using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The span-tokenizing path names the same terms as the string path, which is what lets a writer avoid
/// materializing a string per token.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ITokenizer.Tokenize"/> allocates a string per token; <see cref="ISpanTokenizer.TokenizeNormalized"/>
/// returns terms as <c>[Start, Length)</c> slices of the source, with a string only for a term whose
/// normalized form is not a slice. The store's overlay builds its terms through the string path, and at
/// 100,000 documents that is 12.3M strings created and immediately dropped — measured at 69.81% of the
/// build's CPU in the collector, in a profile taken on a host that does nothing else.
/// </para>
/// <para>
/// Before the overlay can use the span path, the two have to be shown to name the same terms, in the
/// same order, for the same text. That is this test, and it is the authorisation for the change rather
/// than a check of it: if the paths disagree anywhere, the change is wrong before it is written.
/// </para>
/// </remarks>
public class SpanTokenizationParityTests
{
    private static readonly string[] Texts =
    {
        "the quick brown fox jumps over the lazy dog",
        "Statin use and breast cancer survival",
        "An UPPER-case Title, with punctuation: and-dashes!",
        "café naïve résumé — diacritics folded",
        "a b c",
        "",
        "   ",
        "one",
        "quoted \"phrase\" inside a sentence",
        "trailing punctuation...",
        "42 numbers 1,234 and 5.6",
        "hyphen-ated words and under_scores",
    };

    [Fact]
    public void TheSpanPathNamesTheSameTermsAsTheStringPath()
    {
        var tokenizer = Tokenizer.Default;
        var span = Assert.IsAssignableFrom<ISpanTokenizer>(tokenizer);
        int compared = 0;

        foreach (string text in Texts)
        {
            var expected = tokenizer.Tokenize(text);
            var buffer = new NormalizedTerm[text.Length + 32];

            int count = span.TokenizeNormalized(text, buffer);

            // -1 is the contract's "cannot serve this analysis as spans", and the caller then falls back
            // to Tokenize. The default analysis can serve it, so this test asserts the terms.
            Assert.True(count >= 0, $"the default analysis declined to serve '{text}' as spans");
            Assert.Equal(expected.Count, count);

            for (int i = 0; i < count; i++)
            {
                string named = buffer[i].Materialized
                    ?? text.Substring(buffer[i].Start, buffer[i].Length);

                Assert.Equal(expected[i], named);
                compared++;
            }
        }

        Assert.True(compared > 0, "the corpus compared no terms at all");
    }

    [Fact]
    public void AnAnalysisThatCannotBeServedAsSpansSaysSo()
    {
        // A stemmer's term is not a slice of the source, so the contract answers -1 and the caller
        // falls back. This is what makes the span path safe to prefer: it declines rather than
        // approximating, and a tokenizer that cannot do it need not implement anything.
        var stemmed = new Tokenizer(new TokenizerOptions { Stemmer = new PorterStemmer() });
        var span = Assert.IsAssignableFrom<ISpanTokenizer>(stemmed);

        int count = span.TokenizeNormalized("running quickly", new NormalizedTerm[32]);

        Assert.Equal(-1, count);

        // And the string path still names the stemmed terms.
        Assert.Equal(new[] { "run", "quickli" }, stemmed.Tokenize("running quickly"));
    }

    [Fact]
    public void ADocumentWhoseFieldDeclinesIsNotCountedTwice()
    {
        // The body's text can be served as spans and a field's cannot: the overlay has to fall back for
        // the whole document, and the fallback must not add to what the span path already recorded. It
        // did, before AddSpans probed the analysis before recording anything — the body's terms then
        // doubled, their frequencies doubled with them, and no error was raised. One term, one
        // position, and a frequency of one is the assertion that catches it.
        string directory = Path.Combine(Path.GetTempPath(), "lexisharp-decline-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SegmentedTextIndex(directory, new FieldDecliningTokenizer());

            store.Index(
            [
                new SearchDocument(
                    "doc-1",
                    "alpha beta",
                    TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "decline gamma" }),
            ]);

            using var committed = store.Commit();

            Assert.Equal(1, committed.TermFrequency("doc-1", "alpha"));
            Assert.Equal(1, committed.TermFrequency("doc-1", "beta"));
            Assert.Equal(1, committed.TermFrequency("doc-1", "decline"));
            Assert.Equal(1, committed.TermFrequency("doc-1", "gamma"));

            Assert.Equal([0], committed.GetTermPositions("doc-1", "alpha"));

            // The title's terms are part of the length and of the flat view, as the string path has them.
            Assert.Equal(4, committed.DocumentLength("doc-1"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A tokenizer that serves the body as spans and declines any text carrying a marker.</summary>
    private sealed class FieldDecliningTokenizer : ISpanTokenizer
    {
        private static readonly Tokenizer Inner = Tokenizer.Default;

        public IReadOnlyList<string> Tokenize(string text) => Inner.Tokenize(text);

        public IReadOnlyList<string> Tokenize(ReadOnlySpan<char> text) => Inner.Tokenize(text);

        public IReadOnlyList<TokenSpan> TokenizeWithSpans(string text) => Inner.TokenizeWithSpans(text);

        public int TokenizeNormalized(ReadOnlySpan<char> text, Span<NormalizedTerm> destination) =>
            text.Contains("decline", StringComparison.Ordinal)
                ? -1
                : Inner.TokenizeNormalized(text, destination);
    }
}
