namespace LexiSharp.Indexing;

/// <summary>
/// How <see cref="InMemoryVectorSearchEngine"/> stores the document vectors it scans.
/// </summary>
public enum VectorQuantization
{
    /// <summary>
    /// Float32, full precision: the default, and the representation the engine has always used.
    /// </summary>
    None = 0,

    /// <summary>
    /// Signed 8-bit components with one scale per vector, so a stored vector is
    /// <c>scale · round(vector / scale)</c>. A quarter of the float32 payload, at a loss in
    /// ranking fidelity that depends on the embedding model and the corpus — measure it before
    /// relying on it (see the benchmarks page for the measurement this repository publishes).
    /// </summary>
    Int8 = 1,
}

/// <summary>
/// Options for <see cref="InMemoryVectorSearchEngine"/>: how its vectors are stored, which is the
/// only choice the engine makes on its own storage.
/// </summary>
/// <remarks>
/// The defaults are the float32 block, so an engine constructed without options ranks exactly as
/// one constructed before this type existed. Quantisation is opt-in because it trades ranking
/// fidelity for memory, and how much fidelity it costs is a property of the embedding
/// distribution rather than of the engine.
/// </remarks>
public sealed record InMemoryVectorSearchOptions
{
    /// <summary>The defaults: float32 vectors, the same scan and the same scores as always.</summary>
    public static readonly InMemoryVectorSearchOptions Default = new();

    /// <summary>
    /// The stored representation. Default <see cref="VectorQuantization.None"/>.
    /// </summary>
    public VectorQuantization Quantization { get; init; } = VectorQuantization.None;
}
