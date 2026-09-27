using System.Text.Json;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Structural checks on the reference corpus at <c>bench/reference-corpus</c>. The corpus is a
/// committed artifact, so it can rot silently: a renamed document leaves every qrel pointing at
/// nothing, a query loses its judgments and drops out of the averages without anyone noticing.
/// These tests are the thing that makes a baseline trustworthy — without them, a passing
/// benchmark over a broken corpus is worse than no benchmark, because it looks like evidence.
/// </summary>
public class ReferenceCorpusTests
{
    private static string CorpusRoot()
    {
        // Walk up to the repository root rather than trusting a relative path through bin/,
        // which changes shape with the target framework and the configuration.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
                return Path.Combine(directory.FullName, "bench", "reference-corpus");

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static string Path_(params string[] parts) =>
        Path.Combine([CorpusRoot(), .. parts]);

    private static Dictionary<string, string> ReadQueries()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path_("queries.json")));
        var queries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
            queries[property.Name] = property.Value.GetString()!;

        return queries;
    }

    private static Dictionary<string, List<(string DocumentId, int Grade)>> ReadQrels()
    {
        var qrels = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);

        foreach (string line in File.ReadAllLines(Path_("qrels.tsv")))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            string[] columns = line.Split('\t');

            if (columns.Length != 3)
                throw new InvalidDataException($"Expected 'qid\\tdocid\\tgrade' but got: {line}");

            if (!qrels.TryGetValue(columns[0], out var judgments))
            {
                judgments = [];
                qrels[columns[0]] = judgments;
            }

            judgments.Add((columns[1], int.Parse(columns[2])));
        }

        return qrels;
    }

    private static HashSet<string> DocumentIds()
    {
        var root = Path_("corpus");
        return Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void TheCorpusExists()
    {
        Assert.True(Directory.Exists(Path_("corpus")), $"Missing corpus directory: {Path_("corpus")}");
        Assert.True(File.Exists(Path_("queries.json")));
        Assert.True(File.Exists(Path_("qrels.tsv")));
    }

    [Fact]
    public void EveryQrelPointsAtADocumentThatExists()
    {
        var documents = DocumentIds();
        var missing = ReadQrels()
            .SelectMany(pair => pair.Value.Select(judgment => (pair.Key, judgment.DocumentId)))
            .Where(entry => !documents.Contains(entry.DocumentId))
            .Select(entry => $"{entry.Key} -> {entry.DocumentId}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryQueryIsJudgedAndEveryJudgmentHasAQuery()
    {
        var queries = ReadQueries().Keys.ToHashSet(StringComparer.Ordinal);
        var qrels = ReadQrels().Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(queries.Except(qrels, StringComparer.Ordinal));
        Assert.Empty(qrels.Except(queries, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryQueryIdNamesTheFamilyItProbes()
    {
        // The per-query analysis and the family manifest join on this prefix, so a query whose id
        // does not carry it becomes invisible to the breakdown it is supposed to appear in.
        var families = new[] { "identifier", "synonym", "paraphrase", "rare-term", "multilingual", "long-document", "ambiguous", "metadata-conflict", "ordinary" };

        foreach (string id in ReadQueries().Keys)
            Assert.Contains(families, family => id.StartsWith(family + "-", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFamilyHasDocumentsAndAtLeastOneQuery()
    {
        var prefix = ReadQueries()
            .Keys
            .Select(id => id[..id.LastIndexOf('-')])
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(family => family, _ => 0, StringComparer.Ordinal);

        foreach (string id in ReadQueries().Keys)
        {
            string family = id[..id.LastIndexOf('-')];
            prefix[family]++;
        }

        var documentsPerFamily = DocumentIds()
            .Select(FamilyOf)
            .GroupBy(family => family, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (string family in prefix.Keys)
        {
            Assert.True(prefix[family] > 0, $"Family '{family}' has no query.");
            Assert.True(
                documentsPerFamily.GetValueOrDefault(family) > 0,
                $"Family '{family}' has a query but no document.");
        }
    }

    /// <summary>
    /// The family a document id belongs to: <c>ordinary/foo.md</c> is family
    /// <c>ordinary</c>, while a hard case nests one level deeper as
    /// <c>hard/&lt;family&gt;/foo.md</c>.
    /// </summary>
    private static string FamilyOf(string documentId)
    {
        string[] segments = documentId.Split('/');

        return segments.Length >= 3 && segments[0] == "hard" ? segments[1] : segments[0];
    }

    [Fact]
    public void GradesStayInsideTheDeclaredScale()
    {
        // The manifest and the qrels header both say 1..3, matching NFCorpus's three levels.
        foreach ((string query, var judgments) in ReadQrels())
        {
            foreach ((string documentId, int grade) in judgments)
                Assert.InRange(grade, 1, 3);
        }
    }

    [Fact]
    public void AQueryIsNeverJudgedTwiceForTheSameDocument()
    {
        var duplicates = ReadQrels()
            .SelectMany(pair => pair.Value
                .GroupBy(judgment => judgment.DocumentId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{pair.Key} -> {group.Key}"))
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryFamilyInTheManifestIsCoveredByAQuery()
    {
        // A family declared in the manifest but with no query is documentation nobody can check.
        using var document = JsonDocument.Parse(File.ReadAllText(Path_("manifest.json")));

        var declared = document.RootElement
            .GetProperty("families")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var queried = ReadQueries()
            .Keys
            .Select(id => id[..id.LastIndexOf('-')])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(declared.Except(queried, StringComparer.Ordinal));
    }

    [Fact]
    public void TheManifestStatesThatTheDenseCapabilityIsNotSemantic()
    {
        // The corpus is only honest if it says what its expectations are and are not about. A
        // reader who takes "dense misses" as a claim about semantic models would be misled.
        string manifest = File.ReadAllText(Path_("manifest.json"));

        Assert.Contains("HashingEmbeddingProvider", manifest, StringComparison.Ordinal);
        Assert.Contains("LOWER BOUND", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryDocumentsOutnumberTheMajorityOfTheCorpus()
    {
        // Not a ratio target: a floor. If the hard families ever dominate the corpus, IDF and
        // average document length stop meaning anything and every metric becomes noise.
        var documents = DocumentIds();
        int ordinary = documents.Count(id => id.StartsWith("ordinary/", StringComparison.Ordinal));

        Assert.True(
            ordinary > documents.Count / 2,
            $"Only {ordinary} of {documents.Count} documents are ordinary; the corpus is all edge cases.");
    }
}
