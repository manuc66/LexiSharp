using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LexiSharp.Onnx;

/// <summary>
/// A BERT WordPiece tokenizer over a <c>vocab.txt</c>: the tokenizer most BERT-derived encoders
/// and cross-encoders (including the MiniLM family) publish beside their ONNX export.
/// </summary>
/// <remarks>
/// <para>
/// It lowercases, folds diacritics, splits punctuation and applies greedy longest-match WordPiece
/// with <c>##</c> continuations — the same rules the reference exports were trained with. It does
/// not normalize beyond that: SentencePiece checkpoints (E5, multilingual models) have their own
/// rules and need their own <see cref="IOnnxTokenizer"/>, because the vocabulary belongs to the
/// checkpoint rather than to this package.
/// </para>
/// <para>
/// The special tokens <c>[PAD]</c>, <c>[UNK]</c>, <c>[CLS]</c> and <c>[SEP]</c> must be present in
/// the vocabulary; their ids are read from it, not assumed. The instance is immutable and safe for
/// concurrent use.
/// </para>
/// </remarks>
public sealed class BertWordPieceTokenizer : IOnnxTokenizer
{
    private readonly Dictionary<string, int> _vocabulary;
    private readonly BertWordPieceOptions _options;
    private readonly int _unknown;
    private readonly int _classification;
    private readonly int _separator;
    private readonly string _fingerprint;

    /// <summary>Loads a <c>vocab.txt</c> from disk. Line <c>n</c> is the token with id <c>n</c>.</summary>
    /// <param name="vocabularyPath">Path to the checkpoint's <c>vocab.txt</c>.</param>
    /// <exception cref="ArgumentException">The file has no non-empty line.</exception>
    /// <exception cref="InvalidOperationException">A required special token is missing.</exception>
    public BertWordPieceTokenizer(string vocabularyPath)
        : this(File.ReadAllLines(vocabularyPath), new BertWordPieceOptions())
    {
    }

    /// <summary>Loads a <c>vocab.txt</c> and applies the given normalization rules.</summary>
    /// <param name="vocabularyPath">Path to the checkpoint's <c>vocab.txt</c>.</param>
    /// <param name="options">Lowercasing and accent-folding rules; see <see cref="BertWordPieceOptions"/>.</param>
    public BertWordPieceTokenizer(string vocabularyPath, BertWordPieceOptions options)
        : this(File.ReadAllLines(vocabularyPath), options)
    {
    }

    /// <summary>Builds a tokenizer from an in-memory vocabulary; the item at index <c>n</c> has id <c>n</c>.</summary>
    /// <param name="vocabulary">The checkpoint's vocabulary, in id order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vocabulary"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The vocabulary has no non-empty entry.</exception>
    /// <exception cref="InvalidOperationException">A required special token is missing.</exception>
    public BertWordPieceTokenizer(IEnumerable<string> vocabulary)
        : this(vocabulary, new BertWordPieceOptions())
    {
    }

    /// <summary>Builds a tokenizer from an in-memory vocabulary and applies the given normalization rules.</summary>
    /// <param name="vocabulary">The checkpoint's vocabulary, in id order.</param>
    /// <param name="options">Lowercasing and accent-folding rules; see <see cref="BertWordPieceOptions"/>.</param>
    public BertWordPieceTokenizer(IEnumerable<string> vocabulary, BertWordPieceOptions options)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        int id = 0;

        foreach (string token in vocabulary)
        {
            if (token.Length > 0)
                _vocabulary.TryAdd(token, id);

            id++;
        }

        if (_vocabulary.Count == 0)
            throw new ArgumentException("The vocabulary is empty.", nameof(vocabulary));

        _unknown = Lookup("[UNK]");
        _classification = Lookup("[CLS]");
        _separator = Lookup("[SEP]");

        string canonical = string.Join('\n', _vocabulary.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}:{entry.Value}"));
        _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <inheritdoc />
    public string? Fingerprint => _fingerprint;

    private int Lookup(string token) =>
        _vocabulary.TryGetValue(token, out int id)
            ? id
            : throw new InvalidOperationException($"Special token {token} is missing from the vocabulary.");

    /// <summary>Tokenizes one text the way the encoder expects: <c>[CLS] tokens [SEP]</c>.</summary>
    /// <param name="text">Text to tokenize.</param>
    /// <param name="maxSequenceLength">Hard cap, special tokens included; must be at least 2.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSequenceLength"/> is below 2.</exception>
    public OnnxTokenizedText Encode(string text, int maxSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxSequenceLength < 2)
            throw new ArgumentOutOfRangeException(nameof(maxSequenceLength), maxSequenceLength, "A sequence needs room for [CLS] and [SEP].");

        List<int> wordPieces = WordPieceIdList(text);
        int keep = Math.Min(wordPieces.Count, maxSequenceLength - 2);

        int[] ids = new int[keep + 2];
        ids[0] = _classification;
        wordPieces.CopyTo(0, ids, 1, keep);
        ids[^1] = _separator;

        return new OnnxTokenizedText(ids);
    }

    /// <summary>
    /// Merges a query and a document into <c>[CLS] query [SEP] document [SEP]</c>, truncating the
    /// query first at <paramref name="maxQueryTokens"/> and then the document from its start so its
    /// tail survives.
    /// </summary>
    /// <param name="query">The query side.</param>
    /// <param name="document">The document side.</param>
    /// <param name="maxSequenceLength">Hard cap on the merged sequence; must be at least 3.</param>
    /// <param name="maxQueryTokens">Cap on the query side; must be at least 1.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A cap is too small for the special tokens.</exception>
    public OnnxTokenizedText EncodePair(string query, string document, int maxSequenceLength, int maxQueryTokens)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);

        if (maxSequenceLength < 3)
            throw new ArgumentOutOfRangeException(nameof(maxSequenceLength), maxSequenceLength, "A pair needs room for [CLS] and two [SEP].");

        if (maxQueryTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxQueryTokens), maxQueryTokens, "The query needs at least one token.");

        List<int> queryIds = WordPieceIdList(query);
        List<int> documentIds = WordPieceIdList(document);

        int queryAllowance = Math.Min(maxQueryTokens, maxSequenceLength - 3);

        if (queryIds.Count > queryAllowance)
            queryIds.RemoveRange(queryAllowance, queryIds.Count - queryAllowance);

        int documentAllowance = maxSequenceLength - queryIds.Count - 3;

        if (documentIds.Count > documentAllowance)
        {
            if (documentAllowance <= 0)
                documentIds.Clear();
            else
                documentIds.RemoveRange(0, documentIds.Count - documentAllowance);
        }

        int total = queryIds.Count + documentIds.Count + 3;
        int[] ids = new int[total];
        int[] types = new int[total];

        ids[0] = _classification;

        for (int i = 0; i < queryIds.Count; i++)
            ids[i + 1] = queryIds[i];

        int separator = queryIds.Count + 1;
        ids[separator] = _separator;

        for (int i = 0; i < documentIds.Count; i++)
            ids[separator + 1 + i] = documentIds[i];

        ids[^1] = _separator;

        for (int i = separator + 1; i < total; i++)
            types[i] = 1;

        return new OnnxTokenizedText(ids, types);
    }

    /// <summary>WordPiece ids for one text, without special tokens.</summary>
    /// <param name="text">Text to tokenize.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<int> WordPieceIds(string text) => WordPieceIdList(text);

    private List<int> WordPieceIdList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<int> ids = [];

        foreach (string word in BasicTokenize(text))
        {
            int start = 0;
            bool first = true;

            while (start < word.Length)
            {
                int end = word.Length;
                int? found = null;

                while (start < end)
                {
                    string candidate = (start == 0 ? string.Empty : "##") + word[start..end];

                    if (_vocabulary.TryGetValue(candidate, out int id))
                    {
                        found = id;
                        break;
                    }

                    end--;
                }

                if (found is null)
                {
                    ids.Add(_unknown);

                    if (first)
                        break;

                    start++;
                    continue;
                }

                ids.Add(found.Value);
                start = end;
                first = false;
            }
        }

        return ids;
    }

    private IEnumerable<string> BasicTokenize(string text)
    {
        List<string> tokens = [];

        foreach (string raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string folded = _options.LowerCase ? raw.ToLowerInvariant() : raw;

            if (_options.FoldAccents)
                folded = FoldAccents(folded);

            if (folded.Length == 0)
                continue;

            var buffer = new StringBuilder();

            void Flush()
            {
                if (buffer.Length > 0)
                {
                    tokens.Add(buffer.ToString());
                    buffer.Clear();
                }
            }

            foreach (char c in folded)
            {
                if (char.IsWhiteSpace(c))
                    Flush();
                else if (char.IsLetterOrDigit(c) || c == '#' || c == '\'')
                    buffer.Append(c);
                else
                {
                    Flush();
                    tokens.Add(c.ToString());
                }
            }

            Flush();
        }

        return tokens;
    }

    private static string FoldAccents(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(normalized.Length);

        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(c);
        }

        return folded.ToString();
    }
}
