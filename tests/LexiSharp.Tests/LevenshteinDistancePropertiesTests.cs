using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LexiSharp.Similarity;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Property-based tests for <see cref="LevenshteinDistance"/>: the distance must satisfy the
/// metric laws (symmetry, triangle inequality) and the classic length bounds. A small
/// alphabet keeps the dynamic program cheap while still exploring all operation types.
/// </summary>
public class LevenshteinDistancePropertiesTests
{
    private const string Alphabet = "abc";

    // Mixed ASCII/Unicode alphabet and lengths up to 80: exercises both the 64-bit
    // bit-parallel fast path and the rolling dynamic-program fallback.
    private const string MixedAlphabet = "abcXYZ\u00e9\u00b5\u4e2d";

    // ASCII alphabet, lengths up to 80: exercises the 64-bit bit-parallel fast path (<= 64)
    // and the rolling dynamic-program fallback (> 64) through the same generator.
    private static readonly Arbitrary<string> Words =
        Arb.From(Gen.Choose(0, 80).Select(
            len => new string(
                Enumerable.Range(0, len)
                    .Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)])
                    .ToArray())));

    private static readonly Arbitrary<string> MixedWords =
        Arb.From(Gen.Choose(0, 80).Select(
            len => new string(
                Enumerable.Range(0, len)
                    .Select(_ => MixedAlphabet[Random.Shared.Next(MixedAlphabet.Length)])
                    .ToArray())));

    [PropertyAttribute]
    public Property Distance_IsSymmetric() =>
        Prop.ForAll(Words, Words, (a, b) =>
            LevenshteinDistance.Distance(a, b) == LevenshteinDistance.Distance(b, a));

    [PropertyAttribute]
    public Property Distance_SatisfiesTriangleInequality() =>
        Prop.ForAll(Words, Words, Words, (a, b, c) =>
            LevenshteinDistance.Distance(a, c)
                <= LevenshteinDistance.Distance(a, b) + LevenshteinDistance.Distance(b, c));

    [PropertyAttribute]
    public Property Distance_SpansLengthDifference() =>
        Prop.ForAll(Words, Words, (a, b) =>
        {
            int distance = LevenshteinDistance.Distance(a, b);
            return distance >= Math.Abs(a.Length - b.Length)
                && distance <= Math.Max(a.Length, b.Length);
        });

    [PropertyAttribute]
    public Property Distance_ToEmptyString_EqualsLength() =>
        Prop.ForAll(Words, a => LevenshteinDistance.Distance(string.Empty, a) == a.Length);

    [PropertyAttribute]
    public Property Distance_MatchesReferenceDynamicProgram() =>
        Prop.ForAll(Words, Words, (a, b) =>
            LevenshteinDistance.Distance(a, b) == ReferenceDistance(a, b));

    [PropertyAttribute]
    public Property Distance_NonAscii_MatchesReferenceDynamicProgram() =>
        Prop.ForAll(MixedWords, MixedWords, (a, b) =>
            LevenshteinDistance.Distance(a, b) == ReferenceDistance(a, b));

    [Fact]
    public void Distance_Over64Chars_UsesFallbackCorrectly()
    {
        var longA = new string('a', 70);
        var longB = new string('a', 68);

        Assert.Equal(2, LevenshteinDistance.Distance(longA, longB));
        Assert.Equal(3, LevenshteinDistance.Distance(longA, longB + "xyz"));
        Assert.Equal(0, LevenshteinDistance.Distance(longA, new string('a', 70)));
    }

    [Fact]
    public void Distance_UnicodeBmpCharacters()
    {
        Assert.Equal(1, LevenshteinDistance.Distance("caf\u00e9", "cafe"));
        Assert.Equal(1, LevenshteinDistance.Distance("\u4e2d\u6587\u6d4b\u8bd5", "\u4e2d\u6587\u6d4b"));
        Assert.Equal(4, LevenshteinDistance.Distance("\u4e2d\u6587\u6d4b\u8bd5", string.Empty));
        Assert.Equal(1, LevenshteinDistance.Distance("na\u00efve", "naive"));
    }

    /// <summary>Straightforward full-matrix dynamic program used as the oracle.</summary>
    private static int ReferenceDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
