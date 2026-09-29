using LexiSharp.ApiDocs;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Tests the line diff that <c>check</c> reports.
/// </summary>
/// <remarks>
/// The report is what a contributor reads when the gate fails, so the number it leads with has to
/// mean what it says. A change that removes rows shifts every line after it, and a diff that
/// compares by index calls the whole tail changed — the failure this class exists to prevent.
/// </remarks>
public class LineDiffTests
{
    [Fact]
    public void IdenticalFiles_ReportNoChange()
    {
        var difference = new LineDiff(["a", "b", "c"], ["a", "b", "c"]);

        Assert.False(difference.Changed);
        Assert.Equal(0, difference.Removed);
        Assert.Equal(0, difference.Added);
    }

    /// <summary>
    /// The trap. Five rows removed near the top of a 300-line page shifts everything below them;
    /// an index-wise comparison reports ~295 changed lines, and a reader looking at that number
    /// would go hunting for a rewrite that did not happen.
    /// </summary>
    [Fact]
    public void RemovingLines_ReportsTheRowsRemovedAndNotTheShiftedTail()
    {
        string[] committed = [.. Enumerable.Range(0, 300).Select(i => $"line {i}")];
        string[] generated = committed.Where(line => line is not ("line 10" or "line 11" or "line 12" or "line 13" or "line 14")).ToArray();

        var difference = new LineDiff(committed, generated);

        Assert.True(difference.Changed);
        Assert.Equal(5, difference.Removed);
        Assert.Equal(0, difference.Added);
        Assert.Equal(11, difference.FirstChangedLine);
        Assert.Equal("line 10", difference.FirstCommitted);
        Assert.Equal("line 15", difference.FirstGenerated);
    }

    [Fact]
    public void AddingLines_ReportsThemSeparately()
    {
        var difference = new LineDiff(["a", "c"], ["a", "b", "c"]);

        Assert.Equal(0, difference.Removed);
        Assert.Equal(1, difference.Added);
        Assert.Equal(2, difference.FirstChangedLine);
        Assert.Equal("c", difference.FirstCommitted);
        Assert.Equal("b", difference.FirstGenerated);
    }

    /// <summary>A line added and another removed at the same place: one of each, not a rewrite.</summary>
    [Fact]
    public void AReplacedLine_ReportsOneAddedAndOneRemoved()
    {
        var difference = new LineDiff(["a", "old", "c"], ["a", "new", "c"]);

        Assert.Equal(1, difference.Removed);
        Assert.Equal(1, difference.Added);
        Assert.Equal("old", difference.FirstCommitted);
        Assert.Equal("new", difference.FirstGenerated);
    }

    [Fact]
    public void TrailingNewlineLoss_IsReported()
    {
        var difference = new LineDiff(["a", "b"], ["a", "b", ""]);

        Assert.Equal(0, difference.Removed);
        Assert.Equal(1, difference.Added);
    }
}
