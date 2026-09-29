using System.Globalization;
using System.Text.Json;

namespace LexiSharp.Eval;

/// <summary>
/// Pinned reference measurements, read from <c>reference/pinned.json</c>.
/// </summary>
/// <remarks>
/// The file holds two kinds of number and the distinction is the whole point. A
/// <see cref="RegressionPin"/> is a value this repository must keep producing: it is the assertion,
/// and its tolerance is tight because a real change to scoring, candidate generation, tokenization
/// or metrics moves it. A <see cref="ParityRow"/> is somebody else's published figure, kept with its
/// source: it is evidence, and it has no tolerance because nothing in this repository can be
/// changed to make it true.
/// </remarks>
internal sealed record PinnedReference
{
    public const string RelativePath = "reference/pinned.json";

    public int SchemaVersion { get; init; } = 1;
    public string Metric { get; init; } = "ndcg@10";
    public IReadOnlyList<RegressionPin> RegressionPins { get; init; } = [];
    public IReadOnlyList<IndexPin> IndexFingerprints { get; init; } = [];
    public IReadOnlyList<ParityRow> Parity { get; init; } = [];

    public static PinnedReference Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var self = new PinnedReference
        {
            SchemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            Metric = root.TryGetProperty("metric", out var metric) ? metric.GetString() ?? "ndcg@10" : "ndcg@10",
            RegressionPins = ReadArray(root, "regressionPins", pin => new RegressionPin(
                pin.GetProperty("config").GetString()!,
                pin.GetProperty("corpus").GetString()!,
                pin.GetProperty("analyzer").GetString()!,
                pin.GetProperty("ndcgGain").GetString()!,
                ReadBm25(pin.GetProperty("bm25")),
                pin.GetProperty("excludeQueryDocument").GetBoolean(),
                pin.GetProperty("queries").GetInt32(),
                pin.GetProperty("expectedNdcg").GetDouble(),
                pin.GetProperty("tolerance").GetDouble(),
                pin.TryGetProperty("note", out var note) ? note.GetString() : null)
            {
                QueryTermFrequency = pin.TryGetProperty("queryTermFrequency", out var qtf) && qtf.GetBoolean(),
            }),
            IndexFingerprints = ReadArray(root, "indexFingerprints", pin => new IndexPin(
                pin.GetProperty("corpus").GetString()!,
                pin.GetProperty("analyzer").GetString()!,
                pin.GetProperty("documents").GetInt32(),
                pin.GetProperty("nonEmptyDocuments").GetInt32(),
                pin.GetProperty("totalTerms").GetInt64(),
                pin.GetProperty("referenceTotalTerms").GetInt64(),
                pin.GetProperty("tolerance").GetDouble())),
            Parity = ReadArray(root, "parity", row => new ParityRow(
                row.GetProperty("corpus").GetString()!,
                row.GetProperty("lexisharpConfig").GetString()!,
                row.GetProperty("lexisharpNdcg").GetDouble(),
                row.GetProperty("referenceNdcg").GetDouble(),
                row.GetProperty("referenceParameters").GetProperty("k1").GetDouble(),
                row.GetProperty("referenceParameters").GetProperty("b").GetDouble(),
                row.GetProperty("alignedParameters").GetBoolean(),
                row.TryGetProperty("note", out var note) ? note.GetString() : null)),
        };

        if (self.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"{RelativePath} has schemaVersion {self.SchemaVersion}, this harness understands " +
                $"{CurrentSchemaVersion}. Re-record it against the current format rather than editing " +
                "the version by hand.");
        }

        return self;
    }

    public const int CurrentSchemaVersion = 1;

    private static IReadOnlyList<T> ReadArray<T>(JsonElement root, string name, Func<JsonElement, T> read)
    {
        if (!root.TryGetProperty(name, out var array))
            return [];

        var items = new List<T>();

        foreach (var element in array.EnumerateArray())
            items.Add(read(element));

        return items;
    }

    private static (double K1, double B) ReadBm25(JsonElement element) =>
        (element.GetProperty("k1").GetDouble(), element.GetProperty("b").GetDouble());

    /// <summary>One value this repository must keep producing, at a fixed configuration.</summary>
    public sealed record RegressionPin(
        string Config,
        string Corpus,
        string Analyzer,
        string NdcgGain,
        (double K1, double B) Bm25,
        bool ExcludeQueryDocument,
        int Queries,
        double ExpectedNdcg,
        double Tolerance,
        string? Note)
    {
        /// <summary>
        /// Whether the scorer counts a query term once per occurrence instead of once. Optional and
        /// false by default, which is the library's default, so a pinned file that predates this
        /// field is read as what it always meant rather than rejected.
        /// </summary>
        /// <remarks>
        /// This dimension exists because of a gap that was measured, not assumed. The setting was
        /// inert on the main search path for a whole release: the engine deduplicated the query
        /// before the scorer could see a repetition, so the two settings produced bit-identical
        /// rankings and the unit tests were the only thing that could catch it. A pin on the
        /// Distinct side alone cannot catch the reverse failure, where the mechanism works and the
        /// number moves.
        /// </remarks>
        public bool QueryTermFrequency { get; init; }

        /// <summary>Whether the measured value stays inside the band.</summary>
        public bool Accepts(double measured) => Math.Abs(measured - ExpectedNdcg) <= Tolerance;

        /// <summary>The command line that produces this configuration.</summary>
        public string CommandLine(string dataDir) =>
            $"dotnet run --project bench/LexiSharp.Eval -c Release -- --data {dataDir} "
            + $"--dataset {Corpus} --no-tuned --analyzer {Analyzer} --ndcg-gain {NdcgGain} "
            + $"--reference-bm25 {Bm25.K1.ToString("0.##", CultureInfo.InvariantCulture)},"
            + $"{Bm25.B.ToString("0.##", CultureInfo.InvariantCulture)}"
            + (ExcludeQueryDocument ? " --exclude-query-doc" : string.Empty)
            + (QueryTermFrequency ? " --query-term-frequency" : string.Empty);
    }

    /// <summary>One index fingerprint, and the reference it is compared against.</summary>
    public sealed record IndexPin(
        string Corpus,
        string Analyzer,
        int Documents,
        int NonEmptyDocuments,
        long TotalTerms,
        long ReferenceTotalTerms,
        double Tolerance);

    /// <summary>
    /// An independent implementation's figure for the same task. Evidence, not an assertion:
    /// <see cref="AlignedParameters"/> records whether the BM25 parameters matched, because a parity
    /// figure across different parameters is a different claim from a like-for-like one.
    /// </summary>
    public sealed record ParityRow(
        string Corpus,
        string LexisharpConfig,
        double LexisharpNdcg,
        double ReferenceNdcg,
        double ReferenceK1,
        double ReferenceB,
        bool AlignedParameters,
        string? Note)
    {
        public double Difference => LexisharpNdcg - ReferenceNdcg;
    }
}
