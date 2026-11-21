namespace LexiSharp.Ranking;

/// <summary>
/// The largest contribution one query term can make under a BM25-family scorer, given the largest
/// frequency and the smallest document length among the documents that carry it.
/// </summary>
/// <remarks>
/// <para>
/// One function per formula, because the three are not interchangeable and a bound that used the
/// wrong one would be a plausible number rather than a compile error. Every function is monotone in
/// the two statistics in the direction the bound needs: increasing in the frequency and decreasing in
/// the length, so evaluating at their extremes bounds every document even when the two extremes come
/// from different documents.
/// </para>
/// <para>
/// The same arithmetic is what a block-max traversal would compare against a threshold. It lives in
/// one place so the normalizer and a future pruned pass cannot disagree about what a block's bound is.
/// </para>
/// </remarks>
internal static class Bm25UpperBound
{
    /// <summary>The BM25 contribution bound: <c>idf · maxTf · (k1 + 1) / (maxTf + k1 · norm)</c>.</summary>
    public static double Bm25(
        double idf,
        int maxFrequency,
        int minDocumentLength,
        double k1,
        double b,
        double saturation,
        double averageLength)
    {
        if (maxFrequency <= 0 || averageLength <= 0 || !(idf > 0))
            return 0;

        double normalization = 1.0 - b + (b * minDocumentLength / averageLength);

        return idf * maxFrequency * saturation / (maxFrequency + (k1 * normalization));
    }

    /// <summary>The BM25+ bound: BM25's fraction with the lower bound added inside the term sum.</summary>
    public static double Bm25Plus(
        double idf,
        int maxFrequency,
        int minDocumentLength,
        double k1,
        double b,
        double delta,
        double averageLength)
    {
        if (maxFrequency <= 0 || averageLength <= 0 || !(idf > 0))
            return 0;

        double normalization = 1.0 - b + (b * minDocumentLength / averageLength);
        double fraction = maxFrequency * (k1 + 1.0) / (maxFrequency + (k1 * normalization));

        return idf * (fraction + delta);
    }

    /// <summary>The BM25L bound: BM25's compression before the lower bound is applied.</summary>
    public static double Bm25L(
        double idf,
        int maxFrequency,
        int minDocumentLength,
        double k1,
        double b,
        double delta,
        double averageLength)
    {
        if (maxFrequency <= 0 || averageLength <= 0 || !(idf > 0))
            return 0;

        double normalization = 1.0 - b + (b * minDocumentLength / averageLength);

        // A normalization of zero is the limit of an infinitely compressed frequency: the fraction
        // tends to k1 + 1, which is finite even though the compressed frequency is not.
        if (normalization <= 0)
            return idf * (k1 + 1.0);

        double compressed = maxFrequency / normalization;

        return idf * (k1 + 1.0) * (compressed + delta) / (k1 + compressed + delta);
    }
}
