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

    private static readonly Arbitrary<string> Words =
        Arb.From(Gen.Choose(0, 24).Select(
            len => new string(
                Enumerable.Range(0, len)
                    .Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)])
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
}
