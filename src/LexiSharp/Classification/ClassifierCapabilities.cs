using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Capability probes that see through the wrappers which implement an interface unconditionally.
/// </summary>
/// <remarks>
/// <see cref="SynchronizedTextClassifier"/>, <see cref="BlendTextClassifier"/> and
/// <see cref="FusedTextClassifier"/> implement <see cref="IIncrementalTextClassifier"/> so callers
/// can discover them, but the wrapper may wrap a model that cannot learn. A plain
/// <c>is IIncrementalTextClassifier</c> would then say yes and fail on the first call; these probes
/// ask the wrapper whether the capability is real.
/// </remarks>
internal static class ClassifierCapabilities
{
    public static bool IsIncremental(ITextClassifier model) => model switch
    {
        SynchronizedTextClassifier synchronized => synchronized.SupportsIncremental,
        FusedTextClassifier fused => fused.SupportsIncremental,
        BlendTextClassifier blend => blend.SupportsIncremental,
        _ => model is IIncrementalTextClassifier,
    };

    public static bool IsContextual(ITextClassifier model) => model switch
    {
        SynchronizedTextClassifier synchronized => synchronized.SupportsContext,
        _ => model is IContextualTextClassifier,
    };

    public static bool IsAsync(ITextClassifier model) => model switch
    {
        SynchronizedTextClassifier synchronized => synchronized.SupportsAsync,
        _ => model is IAsyncTextClassifier,
    };

    public static bool IsIncrementalAsync(ITextClassifier model) => model switch
    {
        SynchronizedTextClassifier synchronized => synchronized.SupportsIncrementalAsync,
        _ => model is IIncrementalAsyncTextClassifier,
    };
}
