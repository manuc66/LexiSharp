using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>
/// A precomputed sparse-vector set is missing, unreadable or incomplete. The harness reports this as
/// a refused measurement rather than scoring a corpus against vectors that do not describe it.
/// </summary>
internal sealed class SparseVectorsException : Exception
{
    public SparseVectorsException(string message) : base(message)
    {
    }

    public SparseVectorsException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// What the encoder recorded about a vector file, read beside the vectors rather than assumed.
/// </summary>
/// <remarks>
/// A row names the model, the truncation and the top-k, and those are the producer's facts: read
/// from a flag here they could drift from the file the run actually consumed. The manifest is
/// written by <c>sparse/encode_splade.py</c> in the same pass as the vectors.
/// </remarks>
/// <param name="Model">Human name of the model, e.g. <c>SPLADE-v3</c>.</param>
/// <param name="ModelFile">The ONNX file the vectors came from.</param>
/// <param name="ModelSha256">Digest of that file, so a row states which bytes of it.</param>
/// <param name="Quantised">Whether the export was a quantised one, which the row must say.</param>
/// <param name="Truncate">Tokens per text after truncation.</param>
/// <param name="TopK">Terms kept per text; <c>0</c> for every non-zero term.</param>
/// <param name="Tokenizer">How the tokenizer was obtained, verbatim from the producer.</param>
/// <param name="EncodeSeconds">Wall clock of the encode, for the record.</param>
/// <param name="TermsPerDocument">Mean non-zero terms per document, after pruning, as the encoder counted them.</param>
internal sealed record SparseVectorManifest(
    string Model,
    string ModelFile,
    string ModelSha256,
    bool Quantised,
    int Truncate,
    int TopK,
    string Tokenizer,
    double EncodeSeconds,
    double TermsPerDocument)
{
    /// <summary>The row's name: the model, what it was truncated and pruned to, and that it is offline.</summary>
    public string Description =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Model} (ONNX{(Quantised ? ", int8 weights" : string.Empty)}, {Truncate}-token truncation, "
            + $"{(TopK > 0 ? $"top-{TopK} terms" : "all non-zero terms")}, vectors precomputed offline)");
}

/// <summary>
/// The vectors a sparse row scores with: one mapping per text, keyed by the SHA-256 of that text.
/// </summary>
/// <remarks>
/// <para>
/// The format is the encoder's: <c>sha256(text) \t token:weight \t token:weight …</c>, one line per
/// text, the digest over the exact UTF-8 bytes. Text is the key because a document id can be
/// re-pointed and a digest cannot; a text whose digest is absent is a refusal here, not an empty
/// vector that reads as "no match".
/// </para>
/// <para>
/// LexiSharp never computes these vectors — the model runs in <c>sparse/encode_splade.py</c> once,
/// outside the harness, and this type only loads the result. That is the seam
/// <see cref="ISparseEmbeddingProvider"/> exists for, and this is the harness's implementation of it.
/// </para>
/// </remarks>
internal sealed class SparseVectorSet
{
    private SparseVectorSet(
        string directory,
        SparseVectorManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> documents,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> queries)
    {
        Directory = directory;
        Manifest = manifest;
        Documents = documents;
        Queries = queries;
    }

    /// <summary>Where the files live, for a message that says which directory was read.</summary>
    public string Directory { get; }

    /// <summary>What the producer recorded; the row is named from this.</summary>
    public SparseVectorManifest Manifest { get; }

    /// <summary>Passage vectors, keyed by <see cref="HashText"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Documents { get; }

    /// <summary>Query vectors, keyed by <see cref="HashText"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Queries { get; }

    /// <summary>The text a document is encoded from, in the producer and here, from one rule.</summary>
    /// <remarks>
    /// A BEIR record's title and body are concatenated rather than kept apart: the sparse engine has
    /// no field concept, so a document's whole text is the body with its title in front, which is
    /// the shape <c>sparse/encode_splade.py</c> hashes. A corpus whose titles were dropped on one
    /// side would read as a model's weaker ranking rather than as an encoding difference.
    /// </remarks>
    public static string DocumentText(string? title, string text) =>
        string.IsNullOrEmpty(title) ? text : title + "\n" + text;

    /// <summary>SHA-256 of the exact UTF-8 bytes, lowercase hex — the same digest the encoder writes.</summary>
    public static string HashText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Loads <c>&lt;corpus&gt;.meta.json</c>, <c>&lt;corpus&gt;.docs.tsv</c> and
    /// <c>&lt;corpus&gt;.queries.tsv</c> from <paramref name="directory"/>.
    /// </summary>
    /// <exception cref="SparseVectorsException">
    /// The directory, the manifest or a vector file is missing; a line is malformed; or the manifest
    /// does not describe a vector file this loader can read.
    /// </exception>
    public static SparseVectorSet Load(string directory, string corpus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpus);

        if (!System.IO.Directory.Exists(directory))
        {
            throw new SparseVectorsException(
                $"no precomputed vectors at '{directory}'. The sparse row consumes vectors produced "
                + "outside this harness; generate them with "
                + "`python bench/LexiSharp.Eval/sparse/encode_splade.py --model-dir <onnx-dir> "
                + $"--corpus <data>/{corpus}/corpus.jsonl --queries <data>/{corpus}/queries.jsonl "
                + $"--out {directory} --name {corpus}`. No row is produced without them.");
        }

        string metaPath = Path.Combine(directory, $"{corpus}.meta.json");

        if (!File.Exists(metaPath))
        {
            throw new SparseVectorsException(
                $"'{metaPath}' is missing: a vector file without its manifest cannot say which model, "
                + "truncation or top-k produced it, and the row would name a configuration it cannot "
                + "evidence. Re-run the encoder for this corpus.");
        }

        SparseVectorManifest manifest = ReadManifest(metaPath);

        return new SparseVectorSet(
            directory,
            manifest,
            ReadVectors(Path.Combine(directory, $"{corpus}.docs.tsv"), "documents"),
            ReadVectors(Path.Combine(directory, $"{corpus}.queries.tsv"), "queries"));
    }

    private static SparseVectorManifest ReadManifest(string path)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException error)
        {
            throw new SparseVectorsException($"'{path}' is not valid JSON: {error.Message}", error);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            T Required<T>(string name, JsonValueKind kind, Func<JsonElement, T> read)
            {
                if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != kind)
                {
                    throw new SparseVectorsException(
                        $"'{path}' has no {name} of kind {kind}: the file was not written by "
                        + "sparse/encode_splade.py, or it is truncated.");
                }

                return read(value);
            }

            double OptionalDouble(string name)
            {
                return root.TryGetProperty(name, out JsonElement value)
                       && value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : 0.0;
            }

            int truncate = Required("truncate", JsonValueKind.Number, value => value.GetInt32());
            int topK = Required("top_k", JsonValueKind.Number, value => value.GetInt32());

            if (truncate <= 0 || topK < 0)
            {
                throw new SparseVectorsException(
                    $"'{path}' records truncate={truncate}, top_k={topK}: top-k is never negative "
                    + "and the truncation is never zero, so this is not a manifest this loader "
                    + "understands.");
            }

            double termsMean = 0.0;

            if (root.TryGetProperty("documents", out JsonElement documents)
                && documents.ValueKind == JsonValueKind.Object
                && documents.TryGetProperty("terms_mean", out JsonElement perDocument)
                && perDocument.ValueKind == JsonValueKind.Number)
            {
                termsMean = perDocument.GetDouble();
            }

            return new SparseVectorManifest(
                Required("model", JsonValueKind.String, value => value.GetString()!),
                Required("model_file", JsonValueKind.String, value => value.GetString()!),
                Required("model_sha256", JsonValueKind.String, value => value.GetString()!),
                root.TryGetProperty("quantised", out JsonElement quantised)
                    && quantised.ValueKind == JsonValueKind.True,
                truncate,
                topK,
                root.TryGetProperty("tokenizer", out JsonElement tokenizer)
                    && tokenizer.ValueKind == JsonValueKind.String
                    ? tokenizer.GetString()!
                    : "unstated",
                OptionalDouble("seconds"),
                termsMean);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> ReadVectors(
        string path, string role)
    {
        if (!File.Exists(path))
        {
            throw new SparseVectorsException(
                $"'{path}' is missing: the {role} were never encoded for this corpus. Re-run "
                + "sparse/encode_splade.py with this corpus's corpus.jsonl and queries.jsonl.");
        }

        var vectors = new Dictionary<string, IReadOnlyDictionary<string, float>>(StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;

            if (line.Length == 0)
            {
                throw new SparseVectorsException($"'{path}' line {lineNumber} is empty.");
            }

            string[] fields = line.Split('\t');

            if (fields.Length < 2)
            {
                throw new SparseVectorsException(
                    $"'{path}' line {lineNumber} has {fields.Length} tab-separated field(s): a line is "
                    + "`sha256(text) \\t token:weight \\t …`, so a text with no term is not a line.");
            }

            string digest = fields[0];

            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            {
                throw new SparseVectorsException(
                    $"'{path}' line {lineNumber} starts with '{Truncate(digest, 20)}', which is not a "
                    + "SHA-256 digest in hex.");
            }

            digest = digest.ToLowerInvariant();

            var terms = new Dictionary<string, float>(StringComparer.Ordinal);

            for (int i = 1; i < fields.Length; i++)
            {
                string field = fields[i];
                int separator = field.LastIndexOf(':');

                // The last colon, because a vocabulary token can contain one — and a token that is
                // ':' itself is written ':1.5', where the only colon is the separator and the token
                // is the character before it. WordPiece tokens carry no whitespace, so nothing else
                // in the field can be mistaken for the split.
                if (separator < 0 || separator == field.Length - 1)
                {
                    throw new SparseVectorsException(
                        $"'{path}' line {lineNumber} term {i} is '{Truncate(field, 40)}', not "
                        + "`token:weight`.");
                }

                string term = separator == 0 ? ":" : field[..separator];

                if (!float.TryParse(
                        field[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float weight))
                {
                    throw new SparseVectorsException(
                        $"'{path}' line {lineNumber} term {i} has weight "
                        + $"'{Truncate(field[(separator + 1)..], 40)}', which is not a number.");
                }

                if (!float.IsFinite(weight) || weight <= 0)
                {
                    throw new SparseVectorsException(
                        $"'{path}' line {lineNumber} term '{Truncate(term, 40)}' has weight {weight}: the "
                        + "engines treat a non-positive weight as absent, so a producer that writes one "
                        + "does not mean what the file says.");
                }

                if (!terms.TryAdd(term, weight))
                {
                    throw new SparseVectorsException(
                        $"'{path}' line {lineNumber} lists term '{Truncate(term, 40)}' twice: "
                        + "ISparseEmbeddingProvider forbids duplicates in one vector.");
                }
            }

            if (vectors.TryGetValue(digest, out IReadOnlyDictionary<string, float>? existing))
            {
                // Identical text is a legitimate duplicate — two BEIR documents can share a title and
                // a body, and a vector is a function of the text, so the second line must be the
                // first one. It is refused when it is *not*: two different vectors for one text means
                // the file cannot say which one a lookup should return.
                if (SameVector(existing, terms))
                    continue;

                throw new SparseVectorsException(
                    $"'{path}' line {lineNumber} repeats the digest {digest} with a different vector: "
                    + "one text has one vector, and a file that says otherwise leaves the loaded set "
                    + "dependent on line order.");
            }

            vectors[digest] = terms;
        }

        if (vectors.Count == 0)
        {
            throw new SparseVectorsException($"'{path}' holds no vectors.");
        }

        return vectors;
    }

    private static bool SameVector(
        IReadOnlyDictionary<string, float> existing, IReadOnlyDictionary<string, float> candidate)
    {
        if (existing.Count != candidate.Count)
            return false;

        foreach ((string term, float weight) in candidate)
        {
            if (!existing.TryGetValue(term, out float value) || value != weight)
                return false;
        }

        return true;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}

/// <summary>
/// The vector set behind <see cref="ISparseEmbeddingProvider"/>, for the harness's sparse row.
/// </summary>
/// <remarks>
/// A text whose digest is not in the set throws. Returning an empty mapping instead would be read by
/// <c>SparseTextSearchEngine</c> as "this query activates nothing", which returns no results and
/// lowers the metric over a missing vector rather than reporting it.
/// </remarks>
internal sealed class SparseVectorProvider : ISparseEmbeddingProvider
{
    private readonly SparseVectorSet _set;

    public SparseVectorProvider(SparseVectorSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
        string text, EmbeddingUse use, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> vectors =
            use == EmbeddingUse.Query ? _set.Queries : _set.Documents;

        string digest = SparseVectorSet.HashText(text);

        if (!vectors.TryGetValue(digest, out IReadOnlyDictionary<string, float>? vector))
        {
            string role = use == EmbeddingUse.Query ? "query" : "passage";

            throw new SparseVectorsException(
                $"no precomputed {role} vector for sha256 {digest} (text begins "
                + $"'{Preview(text)}'): the vectors in '{_set.Directory}' were encoded from the same "
                + "texts the run reads, and this one is not among them. A vector for different text "
                + "would score a document nobody indexed, so no vector is used.");
        }

        return Task.FromResult(vector);
    }

    private static string Preview(string text)
    {
        string flattened = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flattened.Length <= 60 ? flattened : flattened[..60] + "…";
    }
}
