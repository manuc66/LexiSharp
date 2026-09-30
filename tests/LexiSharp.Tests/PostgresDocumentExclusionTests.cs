using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The two decisions every SQL engine in the retrieval package makes about
/// <see cref="SearchOptions.ExcludedDocumentIds"/>: how many rows to fetch, and which rows to keep.
/// </summary>
/// <remarks>
/// <para>
/// These are unit tests over the decision rather than over a database, and the reason is that the
/// decision is where the defect lived. Four of the five SQL engines never read the option at all, and
/// the fifth applied it while fetching exactly <c>Options.Window</c> rows — so a page came back one
/// result short for every excluded document inside the window. A test against a live server would
/// have caught the first; only a test on this arithmetic catches the second, because it is a page
/// that is <em>valid</em> and merely incomplete.
/// </para>
/// <para>
/// The in-memory engines' equivalent is
/// <c>ExcludingOneDocumentStillFillsThePage</c>. The two now state the same contract for both
/// implementations, which is the point: the same <see cref="SearchOptions"/> has to mean the same
/// thing whichever backend answers it.
/// </para>
/// </remarks>
public class PostgresDocumentExclusionTests
{
    private static SearchOptions Options(int limit = 10, int offset = 0, IReadOnlySet<string>? excluded = null) =>
        new(limit, Offset: offset, ExcludedDocumentIds: excluded);

    [Fact]
    public void FetchLimitIsTheWindowWhenNothingIsExcluded()
    {
        // The overwhelmingly common case, and it must not cost anything: no exclusions, no arithmetic
        // beyond reading the window back.
        Assert.Equal(10, PostgresDocumentExclusion.FetchLimit(Options()));
        Assert.Equal(10, PostgresDocumentExclusion.FetchLimit(Options(excluded: new HashSet<string>(StringComparer.Ordinal))));
    }

    [Fact]
    public void FetchLimitReachesPastEveryExcludedDocument()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };

        Assert.Equal(13, PostgresDocumentExclusion.FetchLimit(Options(excluded: excluded)));
    }

    [Fact]
    public void FetchLimitCoversTheOffsetToo()
    {
        // The window is Offset + Limit, not Limit: 10 + 10 = 20, plus one row for the excluded
        // document. Getting that wrong would truncate the page on any engine that paginates, which is
        // why the assertion uses a non-zero offset.
        Assert.Equal(21, PostgresDocumentExclusion.FetchLimit(Options(limit: 10, offset: 10, excluded: new HashSet<string>(StringComparer.Ordinal) { "a" })));
    }

    [Fact]
    public void FetchLimitSaturatesRatherThanOverflowing()
    {
        // A caller that asks for a page at int.MaxValue - 1 with two exclusions must not wrap to a
        // negative limit, which the driver would reject with an error that names neither cause.
        var huge = new SearchOptions(int.MaxValue, ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal) { "a", "b" });

        Assert.Equal(int.MaxValue, PostgresDocumentExclusion.FetchLimit(huge));
    }

    [Fact]
    public void OnlyTheListedIdsAreExcluded()
    {
        var options = Options(excluded: new HashSet<string>(StringComparer.Ordinal) { "a" });

        Assert.False(PostgresDocumentExclusion.Passes(options, "a"));
        Assert.True(PostgresDocumentExclusion.Passes(options, "b"));
    }

    [Fact]
    public void AnEmptySetExcludesNothing()
    {
        // The case a membership test written without the count check gets wrong, and the one the
        // in-memory suite already pins; the SQL engines need it stated for their own gate.
        var options = Options(excluded: new HashSet<string>(StringComparer.Ordinal));

        Assert.True(PostgresDocumentExclusion.Passes(options, "a"));
        Assert.True(PostgresDocumentExclusion.Passes(options, ""));
    }

    [Fact]
    public void AnUnsetSetExcludesNothing()
    {
        var options = Options();

        Assert.True(PostgresDocumentExclusion.Passes(options, "a"));
    }

    [Fact]
    public void ExcludingEveryDocumentLeavesNothing()
    {
        var options = Options(excluded: new HashSet<string>(StringComparer.Ordinal) { "a", "b" });

        Assert.False(PostgresDocumentExclusion.Passes(options, "a"));
        Assert.False(PostgresDocumentExclusion.Passes(options, "b"));
    }
}
