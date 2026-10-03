using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Property-based tests for <see cref="WindowBm25Scorer"/>'s central identity: the whole-document
/// window is BM25 with no length term, on any corpus, for any query and any <c>k1</c>.
/// </summary>
/// <remarks>
/// The identity is what makes a windowed-versus-whole comparison a comparison of one scorer against
/// itself, so it is checked over generated corpora rather than on the four documents the example-based
/// test uses. Two shapes are generated on purpose: a query may repeat a term, which exercises the
/// deduplication both scorers apply before summing, and a document may hold a term several times
/// apart, which is where a window sweep and a whole-document pass first differ.
/// </remarks>
public class WindowBm25ScorerPropertiesTests
{
    private static readonly string[] Vocabulary = ["alpha", "beta", "gamma", "delta", "epsilon"];

    /// <summary>
    /// Between one and six documents over a five-term vocabulary, each one to twelve tokens long.
    /// </summary>
    private static Gen<SearchDocument[]> Corpora() =>
        from count in Gen.Choose(1, 6)
        from lengths in Gen.Choose(1, 12).ArrayOf(count)
        from tokens in Gen.Elements(Vocabulary).ArrayOf(lengths.Sum())
        select Build(lengths, tokens);

    /// <summary>A query of one to four terms, repeats allowed.</summary>
    private static Gen<string[]> Queries() =>
        from length in Gen.Choose(1, 4)
        from terms in Gen.Elements(Vocabulary).ArrayOf(length)
        select terms;

    private static readonly Arbitrary<(SearchDocument[] Documents, string[] Query, double K1)> Cases =
        Arb.From(
            from documents in Corpora()
            from query in Queries()
            from k1 in Gen.Elements(0.0, 0.5, 1.2, 1.5, 3.0)
            select (documents, query, k1));

    [Property(MaxTest = 500)]
    public Property WholeDocumentWindow_IsBm25WithNoLengthTerm() =>
        Prop.ForAll(Cases, testCase =>
        {
            var index = new InMemoryTextIndex();
            index.Index(testCase.Documents);

            var windowed = new WindowBm25Scorer(includeWholeDocument: true, k1: testCase.K1);
            var bm25 = new Bm25Scorer(testCase.K1, b: 0);

            foreach (var document in testCase.Documents)
            {
                // Exact equality, deliberately without a tolerance: with b = 0 the normalization is
                // exactly 1 and both scorers evaluate the same expression, so any divergence is a
                // defect rather than a rounding difference to absorb.
                if (bm25.Score(document.Id, testCase.Query, index)
                    != windowed.Score(document.Id, testCase.Query, index))
                {
                    return false;
                }
            }

            return true;
        });

    [Property(MaxTest = 500)]
    public Property Score_OfANonMatchingDocument_IsZero() =>
        Prop.ForAll(Cases, testCase =>
        {
            var index = new InMemoryTextIndex();
            index.Index(testCase.Documents);

            var scorer = new WindowBm25Scorer([1, 3, 8]);

            // The ITermOverlapScorer promise, over generated corpora rather than on one: no window
            // of any width holds a term the document does not contain.
            foreach (var document in testCase.Documents)
            {
                var absent = scorer.Score(document.Id, ["absentterm"], index);

                if (absent != 0)
                    return false;
            }

            return true;
        });

    private static SearchDocument[] Build(int[] lengths, string[] tokens)
    {
        var documents = new SearchDocument[lengths.Length];
        int consumed = 0;

        for (int i = 0; i < lengths.Length; i++)
        {
            documents[i] = new SearchDocument(
                $"doc-{i}",
                string.Join(' ', tokens.Skip(consumed).Take(lengths[i])));

            consumed += lengths[i];
        }

        return documents;
    }
}