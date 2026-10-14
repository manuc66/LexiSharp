using LexiSharp.Core;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// A host that does nothing but the operation, so a sampler reads the work rather than the harness.
/// </summary>
/// <remarks>
/// A profile taken under BenchmarkDotNet measures the harness's forced collections, and one taken inside
/// dotnet test measures a test host's idle thread pool. Both were tried; this is the third instrument.
/// Temporary: it goes when the measurement it exists for is recorded.
/// </remarks>
internal static class ProfileHost
{
    public static void Run(int iterations)
    {
        var random = new Random(7);
        var words = new string[100_000];

        for (int i = 0; i < words.Length; i++)
            words[i] = "term" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var documents = new SearchDocument[100_000];

        for (int i = 0; i < documents.Length; i++)
        {
            var builder = new System.Text.StringBuilder(1200);

            for (int w = 0; w < 124; w++)
            {
                builder.Append(words[random.Next(words.Length)]);
                builder.Append(' ');
            }

            documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), builder.ToString());
        }

        for (int i = 0; i < iterations; i++)
        {
            var store = new SegmentedTextIndex(Path.Combine(Path.GetTempPath(), "lexisharp-profile-" + i));
            store.Index(documents);

            Console.WriteLine($"iteration {i} done");
        }
    }
}
