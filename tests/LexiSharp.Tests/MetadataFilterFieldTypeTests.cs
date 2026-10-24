using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class MetadataFilterFieldTypeTests
{
    [Fact]
    public void StringArray_Equal_MatchesMembership()
    {
        var doc = new SearchDocument("d", "t", new Dictionary<string, string> { ["tags"] = "ml|deep-learning|nlp" });

        var filter = new MetadataFilter("tags", MetadataFilterOperator.Equal, "ml", FieldType.StringArray);
        Assert.True(filter.Matches(doc));

        var miss = new MetadataFilter("tags", MetadataFilterOperator.Equal, "vision", FieldType.StringArray);
        Assert.False(miss.Matches(doc));
    }

    [Fact]
    public void StringArray_NotEqual_MatchesAbsence()
    {
        var doc = new SearchDocument("d", "t", new Dictionary<string, string> { ["tags"] = "ml|nlp" });

        var filter = new MetadataFilter("tags", MetadataFilterOperator.NotEqual, "vision", FieldType.StringArray);
        Assert.True(filter.Matches(doc));
    }

    [Fact]
    public void StringArray_Contains_MatchesSubstringInElement()
    {
        var doc = new SearchDocument("d", "t", new Dictionary<string, string> { ["tags"] = "deep-learning|nlp" });

        var filter = new MetadataFilter("tags", MetadataFilterOperator.Contains, "learn", FieldType.StringArray);
        Assert.True(filter.Matches(doc));
    }
}