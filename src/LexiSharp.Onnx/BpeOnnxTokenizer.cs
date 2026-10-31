using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace LexiSharp.Onnx;

/// <summary>
/// Options for <see cref="BpeOnnxTokenizer"/>: the file carries the vocabulary and the merges,
/// the caller states the byte-level and special-token conventions.
/// </summary>
public sealed class BpeOnnxTokenizerOptions
{
    /// <summary>
    /// Whether the model is byte-level (GPT-2/RoBERTa style), which maps spaces to <c>Ġ</c> before
    /// the merges. When left <see langword="null"/> (the default), the tokenizer infers it from the
    /// file's <c>pre_tokenizer</c>; an explicit value always wins.
    /// </summary>
    public bool? ByteLevel { get; init; }

    /// <summary>Unknown token, or <see langword="null"/> when the vocabulary has none.</summary>
    public string? UnknownToken { get; init; }

    /// <summary>Prefix attached to a sub-word that does not start a word, if the checkpoint uses one.</summary>
    public string? ContinuingSubwordPrefix { get; init; }

    /// <summary>Suffix that marks the end of a word, if the checkpoint uses one.</summary>
    public string? EndOfWordSuffix { get; init; }

    /// <summary>Beginning-of-sentence token to emit, or <see langword="null"/> for none.</summary>
    public string? BeginningOfSentenceToken { get; init; }

    /// <summary>End-of-sentence token to emit, or <see langword="null"/> for none.</summary>
    public string? EndOfSentenceToken { get; init; }

    /// <summary>Special tokens to add to the vocabulary, or <see langword="null"/> to use only the file's <c>added_tokens</c>.</summary>
    public IReadOnlyDictionary<string, int>? SpecialTokens { get; init; }

    /// <summary>Optional normalizer from <c>Microsoft.ML.Tokenizers</c>.</summary>
    public Normalizer? Normalizer { get; init; }

    /// <summary>Optional pre-tokenizer from <c>Microsoft.ML.Tokenizers</c>.</summary>
    public PreTokenizer? PreTokenizer { get; init; }

    /// <summary>
    /// Id inserted between query and document by <see cref="BpeOnnxTokenizer.EncodePair"/>, or
    /// <see langword="null"/> when the checkpoint has no pair convention. Pair encoding throws
    /// <see cref="NotSupportedException"/> while this is null.
    /// </summary>
    public int? PairSeparatorId { get; init; }
}

/// <summary>
/// A byte-level BPE tokenizer loaded from a HuggingFace <c>tokenizer.json</c> — the file most
/// modern exports actually ship, where <c>vocab.json</c> and <c>merges.txt</c> are no longer
/// present separately.
/// </summary>
/// <remarks>
/// <para>
/// The reader takes <c>model.vocab</c>, <c>model.merges</c> and <c>added_tokens</c>; the
/// pre-tokenizer and normalizer are not interpreted from the file, because their JSON vocabulary is
/// open-ended. Pass the matching <see cref="BpeOnnxTokenizerOptions.PreTokenizer"/> and
/// <see cref="BpeOnnxTokenizerOptions.Normalizer"/> when the checkpoint needs one, and
/// <see cref="BpeOnnxTokenizerOptions.ByteLevel"/> for the GPT-2/RoBERTa convention.
/// </para>
/// <para>
/// Pair encoding is opt-in: most BPE cross-encoders define their own separator, so
/// <see cref="EncodePair"/> throws until <see cref="BpeOnnxTokenizerOptions.PairSeparatorId"/> is set.
/// </para>
/// </remarks>
public sealed class BpeOnnxTokenizer : IOnnxTokenizer
{
    private readonly BpeTokenizer _tokenizer;
    private readonly BpeOnnxTokenizerOptions _options;
    private readonly TemplateStep[]? _singleTemplate;
    private readonly TemplateStep[]? _pairTemplate;
    private readonly string _fingerprint;

    private readonly record struct TemplateStep(bool IsSpecial, bool IsSecondSequence, int Id, int TypeId);

    /// <summary>Loads a HuggingFace <c>tokenizer.json</c> whose model type is <c>BPE</c>.</summary>
    /// <param name="tokenizerJsonPath">Path to the checkpoint's <c>tokenizer.json</c>.</param>
    /// <param name="options">Byte-level, special-token and pair conventions; defaults apply when omitted.</param>
    /// <exception cref="ArgumentException">The path is empty or the file has no <c>model</c> section.</exception>
    /// <exception cref="NotSupportedException">The model type is not <c>BPE</c>.</exception>
    /// <exception cref="InvalidOperationException">The vocabulary or merges section is missing.</exception>
    public BpeOnnxTokenizer(string tokenizerJsonPath, BpeOnnxTokenizerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(tokenizerJsonPath))
            throw new ArgumentException("tokenizerJsonPath is required.", nameof(tokenizerJsonPath));

        _options = options ?? new BpeOnnxTokenizerOptions();

        string json = File.ReadAllText(tokenizerJsonPath);
        _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("model", out JsonElement model))
            throw new ArgumentException("The file has no 'model' section.", nameof(tokenizerJsonPath));

        string type = model.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;

        // Older exports (GPT-2's tokenizer.json among them) omit the type and only carry
        // vocab + merges; treat that shape as BPE rather than rejecting a usable file.
        if (type.Length > 0 && !string.Equals(type, "BPE", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"The tokenizer model is '{type}'; BpeOnnxTokenizer only reads BPE.");

        if (!model.TryGetProperty("vocab", out JsonElement vocabulary))
            throw new InvalidOperationException("The tokenizer model has no 'vocab' section.");

        bool byteLevel = _options.ByteLevel ?? InferByteLevel(root);

        Dictionary<string, int> tokens = [];

        foreach (JsonProperty entry in vocabulary.EnumerateObject())
            tokens[entry.Name] = entry.Value.GetInt32();

        List<string> merges = [];

        if (model.TryGetProperty("merges", out JsonElement mergeElement))
        {
            foreach (JsonElement merge in mergeElement.EnumerateArray())
            {
                merges.Add(merge.ValueKind == JsonValueKind.String
                    ? merge.GetString()!
                    : string.Join(' ', merge.EnumerateArray().Select(part => part.GetString())));
            }
        }

        Dictionary<string, int> special = _options.SpecialTokens is null
            ? []
            : new Dictionary<string, int>(_options.SpecialTokens);

        if (root.TryGetProperty("added_tokens", out JsonElement added))
        {
            foreach (JsonElement token in added.EnumerateArray())
            {
                if (token.TryGetProperty("content", out JsonElement content) && token.TryGetProperty("id", out JsonElement id))
                    special.TryAdd(content.GetString()!, id.GetInt32());
            }
        }

        string? unknown = _options.UnknownToken;

        if (unknown is null && model.TryGetProperty("unk_token", out JsonElement unknownElement))
            unknown = unknownElement.ValueKind == JsonValueKind.String ? unknownElement.GetString() : null;

        (TemplateStep[]? single, TemplateStep[]? pair) = ParsePostProcessor(root, special);

        _singleTemplate = single;
        _pairTemplate = pair;

        var bpeOptions = new BpeOptions(tokens)
        {
            Merges = merges,
            ByteLevel = byteLevel,
            SpecialTokens = special,
            UnknownToken = unknown,
            ContinuingSubwordPrefix = _options.ContinuingSubwordPrefix,
            EndOfWordSuffix = _options.EndOfWordSuffix,
            BeginningOfSentenceToken = _options.BeginningOfSentenceToken,
            EndOfSentenceToken = _options.EndOfSentenceToken,
            Normalizer = _options.Normalizer,
            PreTokenizer = _options.PreTokenizer ?? (byteLevel ? RobertaPreTokenizer.Instance : null),
        };

        _tokenizer = BpeTokenizer.Create(bpeOptions);
    }

    /// <inheritdoc />
    public string? Fingerprint => _fingerprint;

    /// <summary>Tokenizes one text and truncates to the cap.</summary>
    /// <param name="text">Text to tokenize.</param>
    /// <param name="maxSequenceLength">Hard cap on the returned token count; must be at least 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSequenceLength"/> is below 1.</exception>
    public OnnxTokenizedText Encode(string text, int maxSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxSequenceLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSequenceLength), maxSequenceLength, "The cap must be at least 1.");

        IReadOnlyList<int> ids = _tokenizer.EncodeToIds(text);

        if (_singleTemplate is null)
        {
            int keep = Math.Min(ids.Count, maxSequenceLength);
            int[] result = new int[keep];

            for (int i = 0; i < keep; i++)
                result[i] = ids[i];

            return new OnnxTokenizedText(result);
        }

        return Apply(ids, second: null, _singleTemplate, maxSequenceLength, maxFirst: 0);
    }

    /// <summary>Merges query and document using <see cref="BpeOnnxTokenizerOptions.PairSeparatorId"/>.</summary>
    /// <param name="query">The query side.</param>
    /// <param name="document">The document side.</param>
    /// <param name="maxSequenceLength">Hard cap on the merged sequence; must be at least 2.</param>
    /// <param name="maxQueryTokens">Cap on the query side; must be at least 1.</param>
    /// <exception cref="NotSupportedException">No <see cref="BpeOnnxTokenizerOptions.PairSeparatorId"/> is configured.</exception>
    public OnnxTokenizedText EncodePair(string query, string document, int maxSequenceLength, int maxQueryTokens)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);

        if (maxSequenceLength < 2)
            throw new ArgumentOutOfRangeException(nameof(maxSequenceLength), maxSequenceLength, "A pair needs room for at least the separator and one token.");

        if (maxQueryTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxQueryTokens), maxQueryTokens, "The query needs at least one token.");

        IReadOnlyList<int> queryIds = _tokenizer.EncodeToIds(query);
        IReadOnlyList<int> documentIds = _tokenizer.EncodeToIds(document);

        if (_pairTemplate is not null)
            return Apply(queryIds, documentIds, _pairTemplate, maxSequenceLength, maxQueryTokens);

        if (_options.PairSeparatorId is not int separator)
            throw new NotSupportedException(
                "This BPE tokenizer has no pair convention. Set BpeOnnxTokenizerOptions.PairSeparatorId, " +
                "or encode the concatenated text as a single sequence.");

        int queryKeep = Math.Min(queryIds.Count, Math.Min(maxQueryTokens, maxSequenceLength - 1));
        int documentKeep = Math.Min(documentIds.Count, maxSequenceLength - queryKeep - 1);

        int[] ids = new int[queryKeep + 1 + documentKeep];
        int[] types = new int[ids.Length];

        for (int i = 0; i < queryKeep; i++)
            ids[i] = queryIds[i];

        ids[queryKeep] = separator;

        for (int i = 0; i < documentKeep; i++)
        {
            ids[queryKeep + 1 + i] = documentIds[i];
            types[queryKeep + 1 + i] = 1;
        }

        return new OnnxTokenizedText(ids, types);
    }

    private static bool InferByteLevel(JsonElement root)
    {
        if (root.TryGetProperty("pre_tokenizer", out JsonElement pre))
        {
            string? type = pre.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;

            if (string.Equals(type, "ByteLevel", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(type, "Sequence", StringComparison.OrdinalIgnoreCase)
                && pre.TryGetProperty("pretokenizers", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    string? childType = child.TryGetProperty("type", out JsonElement childElement) ? childElement.GetString() : null;

                    if (string.Equals(childType, "ByteLevel", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        // No pre-tokenizer section: byte-level exports still carry the Ġ marker in their
        // vocabulary, so that is a safer signal than assuming either convention.
        if (root.TryGetProperty("model", out JsonElement model) && model.TryGetProperty("vocab", out JsonElement vocab))
            return vocab.EnumerateObject().Any(entry => entry.Name.Contains('Ġ', StringComparison.Ordinal));

        return false;
    }

    private OnnxTokenizedText Apply(
        IReadOnlyList<int> first,
        IReadOnlyList<int>? second,
        TemplateStep[] template,
        int maxSequenceLength,
        int maxFirst)
    {
        int specials = 0;

        foreach (TemplateStep step in template)
        {
            if (step.IsSpecial)
                specials++;
        }

        if (maxSequenceLength < specials)
            throw new ArgumentOutOfRangeException(
                nameof(maxSequenceLength),
                maxSequenceLength,
                $"The post-processor needs at least {specials} tokens for its special tokens.");

        int capacity = maxSequenceLength - specials;
        int firstKeep = Math.Min(first.Count, second is null ? capacity : Math.Min(Math.Max(1, maxFirst), capacity));
        int secondKeep = second is null ? 0 : Math.Min(second.Count, capacity - firstKeep);

        int total = specials + firstKeep + secondKeep;
        int[] ids = new int[total];
        int[] types = new int[total];
        int at = 0;

        foreach (TemplateStep step in template)
        {
            if (step.IsSpecial)
            {
                ids[at] = step.Id;
                types[at] = step.TypeId;
                at++;
            }
            else if (step.IsSecondSequence)
            {
                for (int i = 0; i < secondKeep; i++)
                {
                    ids[at] = second![i];
                    types[at] = step.TypeId;
                    at++;
                }

            }
            else
            {
                for (int i = 0; i < firstKeep; i++)
                {
                    ids[at] = first[i];
                    types[at] = step.TypeId;
                    at++;
                }

            }
        }

        return new OnnxTokenizedText(ids, types);
    }

    private static (TemplateStep[]? Single, TemplateStep[]? Pair) ParsePostProcessor(
        JsonElement root,
        Dictionary<string, int> fallbackSpecial)
    {
        if (!root.TryGetProperty("post_processor", out JsonElement post) || post.ValueKind == JsonValueKind.Null)
            return (null, null);

        string? type = post.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;

        if (string.Equals(type, "ByteLevel", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        if (string.Equals(type, "TemplateProcessing", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, int> special = new(fallbackSpecial, StringComparer.Ordinal);

            if (post.TryGetProperty("special_tokens", out JsonElement specialTokens))
            {
                foreach (JsonProperty entry in specialTokens.EnumerateObject())
                {
                    if (entry.Value.TryGetProperty("ids", out JsonElement ids) && ids.GetArrayLength() > 0)
                        special[entry.Name] = ids[0].GetInt32();
                }
            }

            return (
                ParseTemplate(post, "single", special),
                ParseTemplate(post, "pair", special));
        }

        if (string.Equals(type, "RobertaProcessing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "BertProcessing", StringComparison.OrdinalIgnoreCase))
        {
            int cls = post.GetProperty("cls")[1].GetInt32();
            int sep = post.GetProperty("sep")[1].GetInt32();
            bool roberta = string.Equals(type, "RobertaProcessing", StringComparison.OrdinalIgnoreCase);

            TemplateStep[] single =
            [
                new(true, false, cls, 0),
                new(false, false, 0, 0),
                new(true, false, sep, 0),
            ];

            TemplateStep[] pair = roberta
                ?
                [
                    new(true, false, cls, 0),
                    new(false, false, 0, 0),
                    new(true, false, sep, 0),
                    new(true, false, sep, 0),
                    new(false, true, 0, 0),
                    new(true, false, sep, 0),
                ]
                :
                [
                    new(true, false, cls, 0),
                    new(false, false, 0, 0),
                    new(true, false, sep, 0),
                    new(false, true, 0, 1),
                    new(true, false, sep, 0),
                ];

            return (single, pair);
        }

        throw new NotSupportedException(
            $"The tokenizer.json post-processor '{type}' is not supported. It can add special tokens " +
            "the package would otherwise drop; implement IOnnxTokenizer or remove the post-processor.");
    }

    private static TemplateStep[]? ParseTemplate(
        JsonElement post,
        string name,
        Dictionary<string, int> special)
    {
        if (!post.TryGetProperty(name, out JsonElement steps) || steps.ValueKind != JsonValueKind.Array)
            return null;

        List<TemplateStep> result = [];

        foreach (JsonElement step in steps.EnumerateArray())
        {
            if (step.TryGetProperty("SpecialToken", out JsonElement token))
            {
                string id = token.GetProperty("id").GetString()!;
                int typeId = token.TryGetProperty("type_id", out JsonElement typeElement) ? typeElement.GetInt32() : 0;

                if (!special.TryGetValue(id, out int value))
                    throw new InvalidOperationException($"The post-processor references the special token '{id}', which is not in added_tokens.");

                result.Add(new TemplateStep(true, false, value, typeId));
            }
            else if (step.TryGetProperty("Sequence", out JsonElement sequence))
            {
                string id = sequence.GetProperty("id").GetString()!;
                int typeId = sequence.TryGetProperty("type_id", out JsonElement typeElement) ? typeElement.GetInt32() : 0;
                result.Add(new TemplateStep(false, id == "B", 0, typeId));
            }
            else
            {
                throw new NotSupportedException("The post-processor contains a step this package does not understand.");
            }
        }

        return result.ToArray();
    }
}
