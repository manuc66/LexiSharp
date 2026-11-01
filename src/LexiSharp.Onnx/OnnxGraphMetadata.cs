using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LexiSharp.Onnx;

/// <summary>Reads and validates the token-input side of an ONNX graph, shared by both adapters.</summary>
internal static class OnnxGraphMetadata
{
    public static (TensorElementType InputType, string[] InputNames) ReadInputs(
        InferenceSession session,
        string inputIdsName,
        string attentionMaskName,
        string? tokenTypeIdsName)
    {
        if (!session.InputMetadata.TryGetValue(inputIdsName, out NodeMetadata? ids))
            throw new ArgumentException($"The model declares no input named '{inputIdsName}'.", nameof(inputIdsName));

        if (!session.InputMetadata.TryGetValue(attentionMaskName, out NodeMetadata? mask))
            throw new ArgumentException($"The model declares no input named '{attentionMaskName}'.", nameof(attentionMaskName));

        TensorElementType inputType = ids.ElementDataType;

        if (inputType is not (TensorElementType.Int64 or TensorElementType.Int32))
            throw new NotSupportedException(
                $"Input '{inputIdsName}' is {inputType}; only Int64 and Int32 token ids are supported.");

        if (mask.ElementDataType != inputType)
            throw new NotSupportedException(
                $"Inputs '{inputIdsName}' and '{attentionMaskName}' use different element types.");

        string[] names = tokenTypeIdsName is not null && session.InputMetadata.ContainsKey(tokenTypeIdsName)
            ? [inputIdsName, attentionMaskName, tokenTypeIdsName]
            : [inputIdsName, attentionMaskName];

        return (inputType, names);
    }
}
