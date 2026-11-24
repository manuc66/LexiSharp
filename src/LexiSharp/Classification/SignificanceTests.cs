namespace LexiSharp.Classification;

/// <summary>Non-parametric significance tests for paired samples.</summary>
public static class SignificanceTests
{
    /// <inheritdoc cref="LexiSharp.Core.SignificanceTests.WilcoxonSignedRank(IReadOnlyList{double},IReadOnlyList{double})" />
    public static double WilcoxonSignedRank(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
        LexiSharp.Core.SignificanceTests.WilcoxonSignedRank(a, b);

    /// <inheritdoc cref="LexiSharp.Core.SignificanceTests.PairedBootstrap(IReadOnlyList{double},IReadOnlyList{double},int)" />
    public static double PairedBootstrap(IReadOnlyList<double> a, IReadOnlyList<double> b, int resamples = 1000) =>
        LexiSharp.Core.SignificanceTests.PairedBootstrap(a, b, resamples);
}
