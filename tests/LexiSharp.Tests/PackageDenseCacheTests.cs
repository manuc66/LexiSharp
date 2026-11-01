using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public sealed class PackageDenseCacheTests
{
    [Fact]
    public void CacheMatches_RequiresThePackageVersionAndShape()
    {
        string path = Path.Combine(Path.GetTempPath(), "lexisharp-package-cache-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, $$"""
                {
                  "model": "{{DenseModels.ModelId}}",
                  "packageVersion": 1,
                  "docCount": 2,
                  "queryCount": 3,
                  "maxSeqLength": 512
                }
                """);

            Assert.True(PackageDenseEmbedder.CacheMatches(path, documentCount: 2, queryCount: 3, maxSeqLength: 512));
            Assert.False(PackageDenseEmbedder.CacheMatches(path, documentCount: 2, queryCount: 3, maxSeqLength: 256));

            File.WriteAllText(path, $$"""
                {
                  "model": "{{DenseModels.ModelId}}",
                  "packageVersion": 0,
                  "docCount": 2,
                  "queryCount": 3,
                  "maxSeqLength": 512
                }
                """);

            Assert.False(PackageDenseEmbedder.CacheMatches(path, documentCount: 2, queryCount: 3, maxSeqLength: 512));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
