using System.Security.Cryptography;
using System.Text;
using LexiSharp.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LexiSharp.Onnx;

/// <summary>
/// A dense text encoder backed by an ONNX Runtime session: the shipped implementation of
/// <see cref="IEmbeddingProvider"/> when the model runs in-process rather than behind an HTTP
/// service.
/// </summary>
/// <remarks>
/// <para>
/// The package ships <b>no model and no vocabulary</b>. The caller points it at an export and a
/// tokenizer (<see cref="BertWordPieceTokenizer"/>, <see cref="SentencePieceOnnxTokenizer"/>, or
/// their own <see cref="IOnnxTokenizer"/>), and states the graph's input and output names, the
/// pooling rule, the per-role prefixes and the truncation length. That is the whole contract: a
/// model whose export deviates is configured here rather than supported by a new release.
/// </para>
/// <para>
/// Supported output shapes are <c>[batch, sequence, hidden]</c> (pooled with
/// <see cref="OnnxTextEncoderOptions.Pooling"/>) and <c>[batch, hidden]</c> (already pooled, the
/// pooling option is ignored). Supported element types are float32 and float16 for the output,
/// int64 and int32 for the token inputs. Anything else is rejected at construction with a message
/// naming the offending tensor, rather than failing inside the runtime later.
/// </para>
/// <para>
/// Inference is synchronous — ONNX Runtime's <c>Run</c> is — so the asynchronous interface method
/// completes on the calling thread. <see cref="GetTextEmbedding"/> and
/// <see cref="GetTextEmbeddings"/> expose the same work without the wrapper, and the plural
/// overload runs one padded batch instead of one session call per text.
/// </para>
/// <para>
/// This package is analyzer-clean under <c>IsAotCompatible</c>; a Native AOT console referencing
/// it was published and run. The repository's own AOT smoke test does not include it yet.
/// </para>
/// </remarks>
public sealed class OnnxTextEncoder : IEmbeddingProvider, IBatchEmbeddingProvider, IEmbeddingSignatureProvider, IDisposable
{
    private readonly OnnxTextEncoderOptions _options;
    private readonly InferenceSession _session;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly TensorElementType _inputType;
    private readonly TensorElementType _outputType;
    private readonly bool _pooledOutput;
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly Lazy<string> _signature;
    private bool _disposed;

    /// <summary>Loads the model, validates its tensors and resolves its output dimension.</summary>
    /// <param name="options">The model path, tokenizer and graph names; see <see cref="OnnxTextEncoderOptions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required option is empty or out of range.</exception>
    /// <exception cref="NotSupportedException">The graph's tensor types or output rank are not supported.</exception>
    public OnnxTextEncoder(OnnxTextEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new ArgumentException("ModelPath is required.", nameof(options));

        ArgumentNullException.ThrowIfNull(options.Tokenizer, nameof(options));

        if (options.MaxSequenceLength < 2)
            throw new ArgumentException("MaxSequenceLength must be at least 2.", nameof(options));

        if (options.MaxInputCharacters < 0)
            throw new ArgumentException("MaxInputCharacters cannot be negative.", nameof(options));

        if (options.MaxBatchSize < 1)
            throw new ArgumentException("MaxBatchSize must be at least 1.", nameof(options));

        _options = options;
        _signature = new Lazy<string>(ComputeSignature);
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

            if (outputMetadata.Dimensions.Length is not (2 or 3))
                throw new NotSupportedException(
                    $"Output '{options.OutputName}' has rank {outputMetadata.Dimensions.Length}; expected [batch, sequence, hidden] or [batch, hidden].");

            _pooledOutput = outputMetadata.Dimensions.Length == 2;
            _outputNames = [options.OutputName];
            _inputNames = options.TokenTypeIdsName is not null && _session.InputMetadata.ContainsKey(options.TokenTypeIdsName)
                ? [options.InputIdsName, options.AttentionMaskName, options.TokenTypeIdsName]
                : [options.InputIdsName, options.AttentionMaskName];

            Dimension = ResolveDimension();
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public int Dimension { get; }

    /// <summary>
    /// SHA-256 over the model file, the tokenizer fingerprint and every option that changes a
    /// vector (pooling, normalization, prefix and truncation). Computed on first access, because
    /// hashing a large model is not free.
    /// </summary>
    public string? EmbeddingSignature => _signature.Value;

    private string ComputeSignature()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("lexisharp-onnx-v1\n"));

        using (FileStream stream = File.OpenRead(_options.ModelPath))
        {
            byte[] buffer = new byte[81920];
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }

        string configuration =
            $"{_options.Pooling}|{_options.Normalize}|{_options.MaxSequenceLength}|" +
            $"{_options.QueryPrefix}|{_options.PassagePrefix}|{_options.Tokenizer.Fingerprint ?? "none"}";

        hash.AppendData(Encoding.UTF8.GetBytes(configuration));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Embeds one text synchronously, applying the prefix selected by <paramref name="use"/>.</summary>
    /// <param name="text">Text to embed.</param>
    /// <param name="use">Whether this is a query or an indexed passage.</param>
    /// <param name="cancellationToken">Cancels the session call through ONNX Runtime's terminate flag.</param>
    /// <returns>One vector of length <see cref="Dimension"/>, L2-normalized when the option asks for it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public ReadOnlyMemory<float> GetTextEmbedding(
        string text,
        EmbeddingUse use = EmbeddingUse.Passage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        _gate.EnterReadLock();

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            OnnxTokenizedText tokenized = Encode(text, use);
            float[] output = RunBatch([tokenized], cancellationToken, out int sequenceLength);

            return _pooledOutput
                ? ReadPooled(output, 0)
                : PoolTokenLevel(output, 0, sequenceLength, tokenized.Length);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Embeds several texts in one padded session call. The result is in input order; each vector
    /// is identical to what <see cref="GetTextEmbedding"/> would return for that text.
    /// </summary>
    /// <param name="texts">Texts to embed; the list is not mutated.</param>
    /// <param name="use">Whether these are queries or indexed passages.</param>
    /// <param name="cancellationToken">Cancels between chunks and during the session call.</param>
    /// <returns>One vector per input, in the same order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texts"/> is <see langword="null"/> or contains <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public IReadOnlyList<ReadOnlyMemory<float>> GetTextEmbeddings(
        IReadOnlyList<string> texts,
        EmbeddingUse use = EmbeddingUse.Passage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        _gate.EnterReadLock();

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (texts.Count == 0)
                return Array.Empty<ReadOnlyMemory<float>>();

            ReadOnlyMemory<float>[] vectors = new ReadOnlyMemory<float>[texts.Count];

        for (int start = 0; start < texts.Count; start += _options.MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = Math.Min(_options.MaxBatchSize, texts.Count - start);
            var items = new (int Index, OnnxTokenizedText Tokenized)[count];

            for (int i = 0; i < count; i++)
            {
                string text = texts[start + i] ?? throw new ArgumentException("The text list contains null.", nameof(texts));
                items[i] = (start + i, Encode(text, use));
            }

            // Shortest first: padding to the longest member is what a heterogeneous batch pays
            // for, and the sort is local to the chunk so the caller's order is restored below.
            Array.Sort(items, (left, right) => left.Tokenized.Length.CompareTo(right.Tokenized.Length));

            OnnxTokenizedText[] batch = new OnnxTokenizedText[count];

            for (int i = 0; i < count; i++)
                batch[i] = items[i].Tokenized;

            float[] output = RunBatch(batch, cancellationToken, out int sequenceLength);

            if (_pooledOutput)
            {
                if (output.Length != count * Dimension)
                    throw new InvalidOperationException(
                        $"The model returned {output.Length} values, not {count} pooled vectors of {Dimension} dimensions.");

                for (int i = 0; i < count; i++)
                    vectors[items[i].Index] = ReadPooled(output, i);
            }
            else
            {
                if (output.Length != count * sequenceLength * Dimension)
                    throw new InvalidOperationException(
                        $"The model returned {output.Length} values, not {count} sequences of {sequenceLength} positions of {Dimension} dimensions.");

                for (int i = 0; i < count; i++)
                    vectors[items[i].Index] = PoolTokenLevel(output, i, sequenceLength, batch[i].Length);
            }
            }

            return vectors;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private ReadOnlyMemory<float> ReadPooled(float[] output, int index)
    {
        float[] vector = new float[Dimension];
        Array.Copy(output, index * Dimension, vector, 0, Dimension);

        if (_options.Normalize)
            Normalize(vector);

        return vector;
    }

    private ReadOnlyMemory<float> PoolTokenLevel(float[] output, int index, int sequenceLength, int tokenCount)
    {
        int positions = Math.Max(1, tokenCount);
        int stride = sequenceLength * Dimension;
        float[] vector = new float[Dimension];

        if (_options.Pooling == OnnxPooling.Cls)
        {
            Array.Copy(output, index * stride, vector, 0, Dimension);
        }
        else
        {
            int offset = index * stride;

            for (int position = 0; position < positions; position++)
            {
                int at = offset + (position * Dimension);

                for (int d = 0; d < Dimension; d++)
                    vector[d] += output[at + d];
            }

            for (int d = 0; d < Dimension; d++)
                vector[d] /= positions;
        }

        if (_options.Normalize)
            Normalize(vector);

        return vector;
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text,
        EmbeddingUse use,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetTextEmbedding(text, use, cancellationToken));
    }

    /// <summary>Embeds several texts asynchronously; the work still runs on the calling thread.</summary>
    /// <param name="texts">Texts to embed.</param>
    /// <param name="use">Whether these are queries or indexed passages.</param>
    /// <param name="cancellationToken">Checked before the session call, not during it.</param>
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
        IReadOnlyList<string> texts,
        EmbeddingUse use,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetTextEmbeddings(texts, use, cancellationToken));
    }

    /// <summary>
    /// Runs one short sequence through the session so the graph optimization and memory arena are
    /// paid before the first real request. Results are unchanged; the call is optional.
    /// </summary>
    public void Warmup() => GetTextEmbedding("warmup");

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

    private OnnxTokenizedText Encode(string text, EmbeddingUse use)
    {
        if (_options.MaxInputCharacters > 0 && text.Length > _options.MaxInputCharacters)
            throw new ArgumentException(
                $"The text is {text.Length} characters, over MaxInputCharacters ({_options.MaxInputCharacters}).",
                nameof(text));

        string prefixed = OnnxPrefixes.For(use, _options.QueryPrefix, _options.PassagePrefix) + text;
        return _options.Tokenizer.Encode(prefixed, _options.MaxSequenceLength);
    }

    private float[] RunBatch(OnnxTokenizedText[] batch, CancellationToken cancellationToken, out int sequenceLength)
    {
        int count = batch.Length;
        sequenceLength = 1;

        foreach (OnnxTokenizedText tokenized in batch)
            sequenceLength = Math.Max(sequenceLength, tokenized.Length);

        int elements = count * sequenceLength;
        long[] ids = new long[elements];
        long[] mask = new long[elements];
        long[] types = new long[elements];

        for (int i = 0; i < count; i++)
        {
            OnnxTokenizedText tokenized = batch[i];
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

        return _inputType == TensorElementType.Int32
            ? RunInt32(ids, mask, types, count, sequenceLength, cancellationToken)
            : RunInt64(ids, mask, types, count, sequenceLength, cancellationToken);
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

    private int ResolveDimension()
    {
        NodeMetadata metadata = _session.OutputMetadata[_options.OutputName];
        int[] dimensions = metadata.Dimensions;

        if (dimensions.Length > 0 && dimensions[^1] > 0)
            return dimensions[^1];

        OnnxTokenizedText probe = _options.Tokenizer.Encode("probe", _options.MaxSequenceLength);
        float[] output = RunBatch([probe], CancellationToken.None, out int sequenceLength);

        if (_pooledOutput)
            return output.Length;

        if (output.Length % Math.Max(1, sequenceLength) != 0)
            throw new ArgumentException("The model output has no token dimension the encoder can pool.", nameof(_options));

        return output.Length / Math.Max(1, sequenceLength);
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;

        foreach (float value in vector)
            sum += (double)value * value;

        if (sum == 0)
            return;

        float length = (float)Math.Sqrt(sum);

        for (int i = 0; i < vector.Length; i++)
            vector[i] /= length;
    }
}
