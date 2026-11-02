using LexiSharp.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LexiSharp.Onnx;

/// <summary>
/// A pairwise relevance model backed by an ONNX Runtime session: the shipped implementation of
/// <see cref="ICrossEncoderScorer"/> for a local cross-encoder, ready for
/// <see cref="LexiSharp.Hybrid.CrossEncoderReranker"/> or
/// <see cref="BatchedCrossEncoderReranker"/>.
/// </summary>
/// <remarks>
/// <para>
/// The caller supplies the export, the tokenizer and the graph names; the package supplies the
/// pair encoding, the padding, the session and the score extraction. The score is the model's
/// first logit, returned raw — no sigmoid is applied, because the reranker only orders by it and a
/// caller that needs a probability can transform it outside.
/// </para>
/// <para>
/// <see cref="Score"/> runs one pair, as the interface requires;
/// <see cref="ScoreBatch"/> pads several pairs into one session call, which is what
/// <see cref="BatchedCrossEncoderReranker"/> uses. Supported output types are float32 and float16,
/// and supported shapes are <c>[batch, labels]</c> (the first label is taken) and rank-1
/// <c>[batch]</c>. Anything else is rejected at construction.
/// </para>
/// <para>
/// This package is analyzer-clean under <c>IsAotCompatible</c>; a Native AOT console referencing
/// it was published and run. The repository's own AOT smoke test does not include it yet.
/// </para>
/// </remarks>
public sealed class OnnxCrossEncoderScorer : ICrossEncoderScorer, IDisposable
{
    private readonly OnnxCrossEncoderOptions _options;
    private readonly InferenceSession _session;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly TensorElementType _inputType;
    private readonly TensorElementType _outputType;
    private readonly int _outputRank;
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private int _disposeStarted;
    private bool _disposed;

    /// <summary>Loads the model and validates its tensors.</summary>
    /// <param name="options">The model path, tokenizer and graph names; see <see cref="OnnxCrossEncoderOptions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required option is empty or out of range.</exception>
    /// <exception cref="NotSupportedException">The graph's tensor types or output rank are not supported.</exception>
    public OnnxCrossEncoderScorer(OnnxCrossEncoderOptions options)
    {
        ValidateOptions(options);

        _options = options;
        _session = OnnxSessionFactory.Create(
            options.ModelPath,
            options.IntraOpNumThreads,
            options.InterOpNumThreads,
            options.ConfigureSession);

        try
        {
            var input = OnnxGraphMetadata.ReadInputs(
                _session, options.InputIdsName, options.AttentionMaskName, options.TokenTypeIdsName);
            _inputType = input.InputType;
            _inputNames = input.InputNames;

            var output = ReadOutputMetadata(_session, options);
            _outputType = output.OutputType;
            _outputRank = output.Rank;
            _outputNames = [options.OutputName];
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    private static void ValidateOptions(OnnxCrossEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new ArgumentException("ModelPath is required.", nameof(options));

        ArgumentNullException.ThrowIfNull(options.Tokenizer, nameof(options));

        if (options.MaxSequenceLength < 3)
            throw new ArgumentException("MaxSequenceLength must be at least 3.", nameof(options));

        if (options.MaxQueryTokens < 1)
            throw new ArgumentException("MaxQueryTokens must be at least 1.", nameof(options));

        if (options.MaxInputCharacters < 0)
            throw new ArgumentException("MaxInputCharacters cannot be negative.", nameof(options));

        if (options.MaxBatchSize < 1)
            throw new ArgumentException("MaxBatchSize must be at least 1.", nameof(options));

        if (options.ScoreIndex < 0)
            throw new ArgumentException("ScoreIndex cannot be negative.", nameof(options));

        if (options.PadTokenId < 0)
            throw new ArgumentException("PadTokenId cannot be negative.", nameof(options));
    }

    private static (TensorElementType OutputType, int Rank) ReadOutputMetadata(
        InferenceSession session,
        OnnxCrossEncoderOptions options)
    {
        if (!session.OutputMetadata.TryGetValue(options.OutputName, out NodeMetadata? output))
            throw new ArgumentException($"The model declares no output named '{options.OutputName}'.", nameof(options));

        TensorElementType outputType = output.ElementDataType;

        if (outputType is not (TensorElementType.Float or TensorElementType.Float16))
            throw new NotSupportedException(
                $"Output '{options.OutputName}' is {outputType}; only Float and Float16 are supported.");

        int rank = output.Dimensions.Length;

        if (rank is not (1 or 2))
            throw new NotSupportedException(
                $"Output '{options.OutputName}' has rank {rank}; expected [batch, labels] or [batch].");

        return (outputType, rank);
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The scorer has been disposed.</exception>
    public double Score(string query, SearchDocument document) =>
        ScoreBatch(query, [document], _disposeCancellation.Token)[0];

    /// <summary>
    /// Scores several documents against one query in a single padded session call. The result is
    /// in input order, and each value equals what <see cref="Score"/> would return for that pair.
    /// </summary>
    /// <param name="query">The query side, shared by every pair.</param>
    /// <param name="documents">The documents to score; the list is not mutated.</param>
    /// <param name="cancellationToken">Cancels between chunks and during the session call.</param>
    /// <returns>One score per document, in the same order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The scorer has been disposed.</exception>
    public IReadOnlyList<double> ScoreBatch(
        string query,
        IReadOnlyList<SearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);

        _gate.EnterReadLock();

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckLength(query, nameof(query));

            if (documents.Count == 0)
                return Array.Empty<double>();

            double[] scores = new double[documents.Count];

            for (int start = 0; start < documents.Count; start += _options.MaxBatchSize)
            {
                int count = Math.Min(_options.MaxBatchSize, documents.Count - start);
                double[] chunk = ScoreChunk(query, documents, start, count, cancellationToken);

                for (int i = 0; i < count; i++)
                    scores[start + i] = chunk[i];
            }

            return scores;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private double[] ScoreChunk(
        string query,
        IReadOnlyList<SearchDocument> documents,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = new (int Index, OnnxTokenizedText Tokenized)[count];

        for (int i = 0; i < count; i++)
        {
            SearchDocument document = documents[start + i] ?? throw new ArgumentException("The document list contains null.", nameof(documents));
            CheckLength(document.Text, nameof(documents));
            items[i] = (start + i, _options.Tokenizer.EncodePair(
                query,
                document.Text,
                _options.MaxSequenceLength,
                _options.MaxQueryTokens));
        }

        // Shortest first: padding to the longest member is what a heterogeneous batch pays for,
        // and the indices restore the caller's order below.
        Array.Sort(items, (left, right) => left.Tokenized.Length.CompareTo(right.Tokenized.Length));

        OnnxTokenizedText[] batch = new OnnxTokenizedText[count];

        for (int i = 0; i < count; i++)
            batch[i] = items[i].Tokenized;

        float[] output = OnnxSessionRunner.Run(
            _session,
            _inputNames,
            _outputNames,
            _inputType,
            _options.PadTokenId,
            batch,
            cancellationToken,
            _disposeCancellation.Token,
            out _);

        double[] chunkScores = ExtractScores(output, count);
        double[] scores = new double[count];

        for (int i = 0; i < count; i++)
            scores[items[i].Index - start] = chunkScores[i];

        return scores;
    }

    /// <summary>Runs one short pair through the session to pay the first-call cost up front.</summary>
    public void Warmup() => Score("warmup", new SearchDocument("warmup", "warmup"));

    /// <summary>Releases the ONNX Runtime session.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
            return;

        // Cancel first: an in-flight Run holds the read lock, and waiting for the write lock
        // without terminating it would make shutdown wait for the whole inference.
        _disposeCancellation.Cancel();
        _gate.EnterWriteLock();

        try
        {
            _disposed = true;
            _session.Dispose();
            _disposeCancellation.Dispose();
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private void CheckLength(string text, string parameter)
    {
        if (_options.MaxInputCharacters > 0 && text.Length > _options.MaxInputCharacters)
            throw new ArgumentException(
                $"The text is {text.Length} characters, over MaxInputCharacters ({_options.MaxInputCharacters}).",
                parameter);
    }

    internal static int VerifyScoreShape(int outputLength, int count, int rank)
    {
        if (rank == 1)
        {
            if (outputLength != count)
                throw new InvalidOperationException(
                    $"The model returned {outputLength} scores for {count} pairs.");

            return 1;
        }

        int labels = outputLength / count;

        if (labels < 1 || outputLength != count * labels)
            throw new InvalidOperationException(
                $"The model returned {outputLength} values for {count} pairs, which is not a rectangular score matrix.");

        return labels;
    }

    private double[] ExtractScores(float[] output, int count)
    {
        double[] scores = new double[count];

        int labels = VerifyScoreShape(output.Length, count, _outputRank);

        if (_outputRank == 1)
        {
            if (_options.ScoreIndex != 0)
                throw new InvalidOperationException("ScoreIndex must be 0 when the model returns one score per pair.");

            if (_options.ScoreTransform == OnnxScoreTransform.Softmax)
                throw new InvalidOperationException("Softmax needs several labels; use Sigmoid for a single-logit model.");

            for (int i = 0; i < count; i++)
                scores[i] = Transform(output, i, 1, 0);

            return scores;
        }

        if (_options.ScoreIndex >= labels)
            throw new InvalidOperationException(
                $"ScoreIndex is {_options.ScoreIndex} but the model returned {labels} label(s).");

        for (int i = 0; i < count; i++)
            scores[i] = Transform(output, i * labels, labels, _options.ScoreIndex);

        return scores;
    }

    private double Transform(float[] output, int rowStart, int labels, int index)
    {
        double raw = output[rowStart + index];

        return _options.ScoreTransform switch
        {
            OnnxScoreTransform.Sigmoid => 1.0 / (1.0 + Math.Exp(-raw)),
            OnnxScoreTransform.Softmax => Softmax(output, rowStart, labels, index),
            _ => raw,
        };
    }

    private static double Softmax(float[] output, int rowStart, int labels, int index)
    {
        double max = double.NegativeInfinity;

        for (int i = 0; i < labels; i++)
            max = Math.Max(max, output[rowStart + i]);

        double sum = 0;

        for (int i = 0; i < labels; i++)
            sum += Math.Exp(output[rowStart + i] - max);

        return Math.Exp(output[rowStart + index] - max) / sum;
    }
}
