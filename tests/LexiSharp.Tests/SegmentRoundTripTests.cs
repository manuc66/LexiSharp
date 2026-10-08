using System;
using System.Collections.Generic;
using System.IO;
using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A segment holds what the index held: the same documents, the same token counts, the same document
/// frequencies, and — the property everything else rests on — the same postings, entry for entry.
/// </summary>
/// <remarks>
/// The point of the last one is that folding a segment's postings must produce the same scores, and
/// therefore the same page, as folding the index in memory. Comparing entries rather than pages is what
/// makes a failure say *which* entry diverged.
/// </remarks>
public class SegmentRoundTripTests
{
    [Fact]
    public void TheSegmentHoldsTheSamePostingsAsTheIndex()
    {
        var index = Build(60);
        var spanIndex = (ISpanAccumulatingIndex)index;
        var reader = new SegmentReader(SegmentWriter.Write(index));

        int terms = 0;

        foreach (string term in ((IVocabularyIndex)index).Vocabulary)
        {
            Assert.True(spanIndex.TryResolvePostings(term, out var expected), term);
            Assert.True(reader.TryFindTerm(term, out int frequency, out var postings), term);

            Assert.Equal(expected.Count, frequency);
            Assert.Equal(expected.Count, postings.BlockCount == 0 ? 0 : expected.Count);

            int entry = 0;

            while (postings.MoveNext())
            {
                while (postings.TryReadEntry(out int ordinal, out int termFrequency))
                {
                    Assert.Equal(expected.Ordinals[entry], ordinal);
                    Assert.Equal(expected.Frequencies[entry], termFrequency);
                    entry++;
                }
            }

            Assert.Equal(expected.Count, entry);
            terms++;
        }

        Assert.Equal(terms, reader.TermCount);
    }

    [Fact]
    public void TheSegmentHoldsTheSameDocumentsAndLengths()
    {
        var index = Build(60);
        var reader = new SegmentReader(SegmentWriter.Write(index));

        Assert.Equal(index.Count, reader.DocumentCount);

        for (int ordinal = 0; ordinal < index.Count; ordinal++)
        {
            var expected = index.DocumentAt(ordinal)!;
            var actual = reader.Document(ordinal);

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(index.DocumentLength(expected.Id), reader.DocumentLength(ordinal));
        }

        Assert.Equal(
            ((IVocabularyIndex)index).Vocabulary.OrderBy(t => t, StringComparer.Ordinal),
            reader.Terms.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void TheSegmentDoesNotKnowATermTheIndexDoesNot()
    {
        var reader = new SegmentReader(SegmentWriter.Write(Build(20)));

        Assert.False(reader.TryFindTerm("quokka", out _, out _));
    }

    [Fact]
    public void TermsOutsideTheBasicPlaneAreStillFound()
    {
        // A fullwidth a (U+FF41) and a Deseret long i (U+10428) — the forms the tokenizer stores, since
        // it lowercases. In UTF-16 code-unit order the surrogate pair sorts first, and in code-point
        // order — which is UTF-8 byte order — it sorts last. A dictionary sorted the wrong way still
        // holds both terms and still fails to find one of them, which is the kind of bug a round-trip
        // test on ASCII vocabulary cannot see.
        var index = new InMemoryTextIndex(new LexiSharp.Linguistics.Tokenizer(
            new LexiSharp.Linguistics.TokenizerOptions { FoldDiacritics = false, KeepSingleCharTerms = true }));

        index.Index(new[]
        {
            new SearchDocument("1", "Ａ 𐐀"),
            new SearchDocument("2", "Ａ"),
        });

        var reader = new SegmentReader(SegmentWriter.Write(index));

        Assert.True(reader.TryFindTerm("ａ", out int fullwidth, out _), $"dictionary: {string.Join(" | ", reader.Terms)}");
        Assert.Equal(2, fullwidth);

        Assert.True(reader.TryFindTerm("𐐨", out int deseret, out _), $"dictionary: {string.Join(" | ", reader.Terms)}");
        Assert.Equal(1, deseret);
    }

    [Fact]
    public void ASegmentWithAGapIsRefused()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("1", "one two"),
            new SearchDocument("2", "three four"),
        });

        Assert.True(index.Remove("1"));

        Assert.Throws<NotSupportedException>(() => SegmentWriter.Write(index));
    }

    [Fact]
    public void BytesThatAreNotASegmentAreRefused()
    {
        var bytes = SegmentWriter.Write(Build(20));

        // Wrong magic, and a truncation: both are refused rather than read as something.
        var corrupt = (byte[])bytes.Clone();
        corrupt[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => new SegmentReader(corrupt));
        Assert.Throws<InvalidDataException>(() => new SegmentReader(bytes[..(bytes.Length - 8)]));
    }

    private static InMemoryTextIndex Build(int documents)
    {
        var index = new InMemoryTextIndex();
        var random = new Random(7);
        var words = new[] { "search", "engine", "index", "query", "score", "rank", "document", "token" };
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < documents; i++)
        {
            builder.Clear();

            for (int w = 0; w < 12; w++)
                builder.Append(words[random.Next(words.Length)]).Append(' ');

            index.Add(new SearchDocument("doc-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), builder.ToString()));
        }

        return index;
    }
}
