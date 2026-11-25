namespace LexiSharp.Adaptive;

/// <summary>The complete persisted state of an adaptive ranker: options, memory and calibration.</summary>
/// <param name="SchemaVersion">The format version; only <see cref="CurrentVersion"/> is read.</param>
/// <param name="ModelSignature">The caller's signature of the wrapped text model.</param>
/// <param name="Options">The adaptive ranker options.</param>
/// <param name="Memory">The feedback memory snapshot.</param>
/// <param name="Calibration">The conformal calibrator snapshot, or null.</param>
/// <param name="Signature">SHA-256 of the serialized body, lowercase hexadecimal.</param>
public sealed record AdaptiveSnapshot(
    int SchemaVersion,
    string ModelSignature,
    AdaptiveOptions Options,
    FeedbackSnapshot Memory,
    WeightedConformalSnapshot? Calibration,
    string Signature)
{
    /// <summary>The only adaptive-snapshot version this build reads.</summary>
    public const int CurrentVersion = 1;
}
