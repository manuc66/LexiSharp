using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="DocumentLengthQuantization"/>, on its own.
/// </summary>
/// <remarks>
/// Rounding a document length down is not something this library does by default and should not: it
/// always errs one way, it maps distinct lengths onto one value, and it makes scores slightly higher.
/// The option exists so that a caller reproducing a figure published by an implementation that stores
/// lengths in one byte is not comparing rankings built from different statistics.
/// </remarks>
public class DocumentLengthQuantizationTests
{
    /// <summary>
    /// (exact length, length the index reports) for the codes where the two differ, read out of a
    /// reference implementation's table. This is the contract: <c>QuantizedLength</c> is the round
    /// trip — encode a length to the largest code at or below it, then read that code back — and it
    /// is not the same as decoding a code, which is why these pairs are pairs and not a table.
    /// </summary>
    private static readonly (int Exact, int Quantized)[] Table =
    [
        (40, 40), (41, 40), (42, 42), (54, 54), (55, 54), (56, 56), (57, 56), (60, 60),
        (63, 60), (64, 64), (65, 64), (84, 84), (88, 88), (96, 96), (104, 104), (111, 104),
        (112, 112), (144, 144), (152, 152), (168, 168), (248, 248), (264, 264), (280, 280),
        (312, 312), (408, 408), (440, 440), (443, 440), (469, 440), (472, 472), (504, 504),
        (536, 536), (592, 536), (728, 728), (737, 728), (1200, 1176),
    ];

    [Fact]
    public void ExactIsTheDefault()
    {
        Assert.Equal(DocumentLengthQuantization.Exact, default(DocumentLengthQuantization));
        Assert.Equal(DocumentLengthQuantization.Exact, new InMemoryTextIndex().DocumentLengthQuantization);
    }

    /// <summary>
    /// The whole point of the option: the arithmetic must equal the reference table exactly. A
    /// rounding that is off by one bucket is a different ranking, not a rounding.
    /// </summary>
    [Fact]
    public void TheArithmeticReproducesTheTable()
    {
        foreach ((int exact, int quantized) in Table)
        {
            Assert.Equal(quantized, InMemoryTextIndex.QuantizedLength(exact));
        }
    }

    /// <summary>
    /// Rounding is to the nearest representable length at or below, which is not the same as rounding
    /// to nearest: 469 belongs with 443 and 469 alike under this rule, because no representable length
    /// lies between them.
    /// </summary>
    [Fact]
    public void RoundingAlwaysGoesDownAndNeverUp()
    {
        for (int length = 0; length < 1200; length++)
        {
            int quantized = InMemoryTextIndex.QuantizedLength(length);

            Assert.True(quantized <= length, $"{length} rounded up to {quantized}");
        }
    }

    /// <summary>
    /// The loss is real, and it is what makes this worth reproducing rather than dismissing: two
    /// documents that differ by twenty-six terms can be penalized identically.
    /// </summary>
    [Fact]
    public void DocumentsOfDifferentLengthCanQuantizeToTheSameValue()
    {
        Assert.Equal(InMemoryTextIndex.QuantizedLength(443), InMemoryTextIndex.QuantizedLength(469));
        Assert.NotEqual(443, 469);
    }

    /// <summary>Lengths up to 40 are stored exactly, so the option is a no-op on short documents.</summary>
    [Fact]
    public void ShortDocumentsAreNotRounded()
    {
        for (int length = 0; length <= 40; length++)
        {
            Assert.Equal(length, InMemoryTextIndex.QuantizedLength(length));
        }
    }

    [Fact]
    public void TheIndexReportsTheRoundedLengthOnlyWhenAsked()
    {
        var documents = new[]
        {
            new SearchDocument("a", string.Join(' ', Enumerable.Repeat("term", 111))),
        };

        var exact = new InMemoryTextIndex();
        exact.Index(documents);

        var quantized = new InMemoryTextIndex(
            documentLengthQuantization: DocumentLengthQuantization.OneByte);
        quantized.Index(documents);

        Assert.Equal(111, exact.DocumentLength("a"));
        Assert.Equal(104, quantized.DocumentLength("a"));
    }
}