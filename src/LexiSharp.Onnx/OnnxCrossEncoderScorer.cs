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
    private bool _disposed;

    /// <summary>Loads the model and validates its tensors.</summary>
    /// <param name="options">The model path, tokenizer and graph names; see <see cref="OnnxCrossEncoderOptions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required option is empty or out of range.</exception>
    /// <exception cref="NotSupportedException">The graph's tensor types or output rank are not supported.</exception>
    public OnnxCrossEncoderScorer(OnnxCrossEncoderOptions options)
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

        _options = options;
        _session = OnnxSessionFactory.Create(
            options.ModelPath,
            options.IntraOpNumThreads,
            options.InterOpNumThreads,
            options.ConfigureSession);

        try
        {
            if (!_session.InputMetadata.TryGetValue(options.InputIdsName, out NodeMetadata? idsMetadata))
                throw new ArgumentException($"The model declares no input named '{options.InputIdsName}'.", nameof(options));

            if (!_session.InputMetadata.TryGetValue(options.AttentionMaskName, out NodeMetadata? maskMetadata))
                throw new ArgumentException($"The model declares no input named '{options.AttentionMaskName}'.", nameof(options));

            _inputType = idsMetadata.ElementDataType;

            if (_inputType is not (TensorElementType.Int64 or TensorElementType.Int32))
                throw new NotSupportedException(
                    $"Input '{options.InputIdsName}' is {_inputType}; only Int64 and Int32 token ids are supported.");

            if (maskMetadata.ElementDataType != _inputType)
                throw new NotSupportedException(
                    $"Inputs '{options.InputIdsName}' and '{options.AttentionMaskName}' use different element types.");

            if (!_session.OutputMetadata.TryGetValue(options.OutputName, out NodeMetadata? outputMetadata))
                throw new ArgumentException($"The model declares no output named '{options.OutputName}'.", nameof(options));

            _outputType = outputMetadata.ElementDataType;

            if (_outputType is not (TensorElementType.Float or TensorElementType.Float16))
                throw new NotSupportedException(
                    $"Output '{options.OutputName}' is {_outputType}; only Float and Float16 are supported.");

            _outputRank = outputMetadata.Dimensions.Length;

            if (_outputRank is not (1 or 2))
                throw new NotSupportedException(
                    $"Output '{options.OutputName}' has rank {_outputRank}; expected [batch, labels] or [batch].");

            _outputNames = [options.OutputName];
            _inputNames = options.TokenTypeIdsName is not null && _session.InputMetadata.ContainsKey(options.TokenTypeIdsName)
                ? [options.InputIdsName, options.AttentionMaskName, options.TokenTypeIdsName]
                : [options.InputIdsName, options.AttentionMaskName];
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The scorer has been disposed.</exception>
    public double Score(string query, SearchDocument document) =>
        ScoreBatch(query, [document])[0];

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

            if (documents.Count == 0)
                return Array.Empty<double>();

            CheckLength(query, nameof(query));

            double[] scores = new double[documents.Count];

        for (int start = 0; start < documents.Count; start += _options.MaxBatchSize)
        {
            int count = Math.Min(_options.MaxBatchSize, documents.Count - start);
            var items = new (int Index, OnnxTokenizedText Tokenized)[count];

            cancellationToken.ThrowIfCancellationRequested();

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

            Array.Sort(items, (left, right) => left.Tokenized.Length.CompareTo(right.Tokenized.Length));

            int sequenceLength = 1;

            foreach (var item in items)
                sequenceLength = Math.Max(sequenceLength, item.Tokenized.Length);

            int elements = count * sequenceLength;
            long[] ids = new long[elements];
            long[] mask = new long[elements];
            long[] types = new long[elements];

            for (int i = 0; i < count; i++)
            {
                OnnxTokenizedText tokenized = items[i].Tokenized;
                ReadOnlySpan<int> tokenIds = tokenized.Ids.Span;
                ReadOnlySpan<int> typeIds = tokenized.TypeIds.Span;
                int offset = i * sequenceLength;

                for (int position = 0; position < tokenized.Length; position++)
                {
                    ids[offset + position] = tokenIds[position];
                    mask[offset + position] = 1;

                    if (tokenized.HasTypeIds)
                        types[offset + position] = typeIds[position];
                }
            }

            float[] output = _inputType == TensorElementType.Int32
                ? RunInt32(ids, mask, types, count, sequenceLength, cancellationToken)
                : RunInt64(ids, mask, types, count, sequenceLength, cancellationToken);

            double[] chunkScores = ExtractScores(output, count);

            for (int i = 0; i < count; i++)
                scores[items[i].Index] = chunkScores[i];
            }

            return scores;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>Runs one short pair through the session to pay the first-call cost up front.</summary>
    public void Warmup() => Score("warmup", new SearchDocument("warmup", "warmup"));

    /// <summary>Releases the ONNX Runtime session.</summary>
    public void Dispose()
    {
        _gate.EnterWriteLock();

        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _session.Dispose();
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

    private double[] ExtractScores(float[] output, int count)
    {
        double[] scores = new double[count];

        if (_outputRank == 1)
        {
            if (output.Length != count)
                throw new InvalidOperationException(
                    $"The model returned {output.Length} scores for {count} pairs.");

            if (_options.ScoreIndex != 0)
                throw new InvalidOperationException("ScoreIndex must be 0 when the model returns one score per pair.");

            if (_options.ScoreTransform == OnnxScoreTransform.Softmax)
                throw new InvalidOperationException("Softmax needs several labels; use Sigmoid for a single-logit model.");

            for (int i = 0; i < count; i++)
                scores[i] = Transform(output, i, 1, 0);

            return scores;
        }

        int labels = output.Length / count;

        if (labels < 1 || output.Length != count * labels)
            throw new InvalidOperationException(
                $"The model returned {output.Length} values for {count} pairs, which is not a rectangular score matrix.");

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

    private float[] RunInt64(long[] ids, long[] mask, long[] types, int count, int sequenceLength, CancellationToken cancellationToken)
    {
        using var idsValue = OrtValue.CreateTensorValueFromMemory<long>(ids, [count, sequenceLength]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory<long>(mask, [count, sequenceLength]);
        using OrtValue? typesValue = _inputNames.Length < 3
            ? null
            : OrtValue.CreateTensorValueFromMemory<long>(types, [count, sequenceLength]);

        return Execute(idsValue, maskValue, typesValue, cancellationToken);
    }

    private float[] RunInt32(long[] ids, long[] mask, long[] types, int count, int sequenceLength, CancellationToken cancellationToken)
    {
        int[] ids32 = Array.ConvertAll(ids, value => (int)value);
        int[] mask32 = Array.ConvertAll(mask, value => (int)value);
        int[] types32 = Array.ConvertAll(types, value => (int)value);

        using var idsValue = OrtValue.CreateTensorValueFromMemory<int>(ids32, [count, sequenceLength]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory<int>(mask32, [count, sequenceLength]);
        using OrtValue? typesValue = _inputNames.Length < 3
            ? null
            : OrtValue.CreateTensorValueFromMemory<int>(types32, [count, sequenceLength]);

        return Execute(idsValue, maskValue, typesValue, cancellationToken);
    }

    private float[] Execute(OrtValue idsValue, OrtValue maskValue, OrtValue? typesValue, CancellationToken cancellationToken)
    {
        OrtValue[] inputs = typesValue is null
            ? [idsValue, maskValue]
            : [idsValue, maskValue, typesValue];

        using var runOptions = new RunOptions();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => runOptions.Terminate = true);
        using var output = _session.Run(runOptions, _inputNames, inputs, _outputNames);

        OrtTensorTypeAndShapeInfo info = output[0].GetTensorTypeAndShape();

        if (info.ElementDataType == TensorElementType.Float16)
        {
            ReadOnlySpan<Float16> half = output[0].GetTensorDataAsSpan<Float16>();
            float[] converted = new float[half.Length];

            for (int i = 0; i < half.Length; i++)
                converted[i] = (float)half[i];

            return converted;
        }

        return output[0].GetTensorDataAsSpan<float>().ToArray();
    }
}
