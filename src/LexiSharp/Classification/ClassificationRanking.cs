using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// The one posterior order every classifier uses: probability descending, then category ordinal.
/// </summary>
/// <remarks>
/// The rule is a contract, not an implementation detail: a caller comparing two classifiers must
/// see the same tie-break, and six classifiers had copied the two-line comparison. Keeping it here
/// also means a change to the tie-break is one edit rather than six that can drift.
/// </remarks>
internal static class ClassificationRanking
{
    /// <summary>Orders already-built results by probability, then category.</summary>
    public static readonly IComparer<ClassificationResult> Results = new ResultComparer();

    /// <summary>Orders the category/probability tuples the blend ranks in place.</summary>
    public static readonly IComparer<(string Category, double Probability)> Tuples = new TupleComparer();

    /// <summary>Compares two scores; a higher probability comes first, then the lower category.</summary>
    public static int Compare(
        double leftProbability,
        string leftCategory,
        double rightProbability,
        string rightCategory)
    {
        int byProbability = rightProbability.CompareTo(leftProbability);

        return byProbability != 0
            ? byProbability
            : string.CompareOrdinal(leftCategory, rightCategory);
    }

    private sealed class ResultComparer : IComparer<ClassificationResult>
    {
        public int Compare(ClassificationResult? left, ClassificationResult? right)
        {
            if (left is null || right is null)
                return left is null ? (right is null ? 0 : 1) : -1;

            return ClassificationRanking.Compare(
                left.Probability, left.Category, right.Probability, right.Category);
        }
    }

    private sealed class TupleComparer : IComparer<(string Category, double Probability)>
    {
        public int Compare(
            (string Category, double Probability) left,
            (string Category, double Probability) right) =>
            ClassificationRanking.Compare(
                left.Probability, left.Category, right.Probability, right.Category);
    }
}
