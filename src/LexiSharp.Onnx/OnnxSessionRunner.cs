using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LexiSharp.Onnx;

/// <summary>
/// Binds a padded token batch to an ONNX graph, runs it and returns the flat tensor. Shared by
/// the dense encoder and the pairwise scorer so the int32/int64 and float32/float16 handling is
/// defined once.
/// </summary>
internal static class OnnxSessionRunner
{
    public static float[] Run(
        InferenceSession session,
        string[] inputNames,
        string[] outputNames,
        TensorElementType inputType,
        int padTokenId,
        OnnxTokenizedText[] batch,
        CancellationToken cancellationToken,
        CancellationToken disposeToken,
        out int sequenceLength)
    {
        sequenceLength = 1;

        foreach (OnnxTokenizedText tokenized in batch)
            sequenceLength = Math.Max(sequenceLength, tokenized.Length);

        int elements = batch.Length * sequenceLength;
        long[] ids = new long[elements];
        Array.Fill(ids, padTokenId);
        long[] mask = new long[elements];
        long[] types = new long[elements];

        for (int i = 0; i < batch.Length; i++)
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

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeToken);
        using var runOptions = new RunOptions();
        using CancellationTokenRegistration registration = linked.Token.Register(() => runOptions.Terminate = true);

        try
        {
            return inputType == TensorElementType.Int32
                ? RunInt32(session, inputNames, outputNames, runOptions, ids, mask, types, batch.Length, sequenceLength)
                : RunInt64(session, inputNames, outputNames, runOptions, ids, mask, types, batch.Length, sequenceLength);
        }
        catch (OnnxRuntimeException) when (linked.IsCancellationRequested)
        {
            throw new OperationCanceledException(linked.Token);
        }
    }

    private static float[] RunInt64(
        InferenceSession session,
        string[] inputNames,
        string[] outputNames,
        RunOptions runOptions,
        long[] ids,
        long[] mask,
        long[] types,
        int count,
        int sequenceLength)
    {
        using var idsValue = OrtValue.CreateTensorValueFromMemory<long>(ids, [count, sequenceLength]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory<long>(mask, [count, sequenceLength]);
        using OrtValue? typesValue = inputNames.Length < 3
            ? null
            : OrtValue.CreateTensorValueFromMemory<long>(types, [count, sequenceLength]);

        return Execute(session, inputNames, outputNames, runOptions, idsValue, maskValue, typesValue);
    }

    private static float[] RunInt32(
        InferenceSession session,
        string[] inputNames,
        string[] outputNames,
        RunOptions runOptions,
        long[] ids,
        long[] mask,
        long[] types,
        int count,
        int sequenceLength)
    {
        int[] ids32 = Array.ConvertAll(ids, value => (int)value);
        int[] mask32 = Array.ConvertAll(mask, value => (int)value);
        int[] types32 = Array.ConvertAll(types, value => (int)value);

        using var idsValue = OrtValue.CreateTensorValueFromMemory<int>(ids32, [count, sequenceLength]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory<int>(mask32, [count, sequenceLength]);
        using OrtValue? typesValue = inputNames.Length < 3
            ? null
            : OrtValue.CreateTensorValueFromMemory<int>(types32, [count, sequenceLength]);

        return Execute(session, inputNames, outputNames, runOptions, idsValue, maskValue, typesValue);
    }

    private static float[] Execute(
        InferenceSession session,
        string[] inputNames,
        string[] outputNames,
        RunOptions runOptions,
        OrtValue idsValue,
        OrtValue maskValue,
        OrtValue? typesValue)
    {
        OrtValue[] inputs = typesValue is null
            ? [idsValue, maskValue]
            : [idsValue, maskValue, typesValue];

        using var output = session.Run(runOptions, inputNames, inputs, outputNames);

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
