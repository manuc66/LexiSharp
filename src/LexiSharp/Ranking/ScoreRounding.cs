namespace LexiSharp.Ranking;

/// <summary>
/// What a search does to its scores after ranking, before returning them.
/// </summary>
/// <remarks>
/// Scores are usually returned as the ranking arithmetic produced them. One system does not: before
/// writing a run it rounds every score to four decimals and then walks down each run of scores that
/// fall within one ten-thousandth of each other, one millionth at a time, so that documents the
/// arithmetic scored identically come out distinguishable. That is a property of what gets written
/// down, not of the ranking — the order it produces is the order the scores already had — but it is
/// part of what such a system returns, so a caller comparing against one has to produce it.
/// <para>
/// <see cref="FourDecimals"/> is here for that comparison and for nothing else. It is a lossy step by
/// construction: it merges scores that differ by less than half a ten-thousandth, and it makes a
/// document's returned score depend on the other documents returned beside it, so the same query
/// against the same index can report two different scores for it at two different page sizes. Turning
/// it on is a decision about the comparison being made, not an improvement.
/// </para>
/// </remarks>
internal enum ScoreRounding
{
    /// <summary>Return the score the ranking arithmetic produced. The default.</summary>
    None = 0,

    /// <summary>
    /// Round each returned score to four decimals, then subtract one millionth per position from every
    /// score that follows another within a ten-thousandth of it.
    /// </summary>
    /// <remarks>
    /// <b>Measured</b> on BEIR ArguAna against the run that system wrote for the same index and the
    /// same 1,406 queries: of 14,168 paired (query, document) scores, 6.18% matched on the raw bits
    /// before this step and <b>97.76%</b> after it. The residue is one step of the ten-thousandth
    /// grid, on values that sit within a rounding boundary of it — this system's scores are single
    /// precision, so their fourth decimal is decided by an accumulation this repository performs in
    /// double, and on 2.24% of pairs the two land on opposite sides of the boundary. Closing that
    /// would mean reproducing single-precision accumulation, which is a worse arithmetic than the one
    /// used here.
    /// </remarks>
    FourDecimals = 1,
}