namespace LexiSharp.Indexing;

/// <summary>
/// A whole vector index: its entries and the identity of the embedding pipeline that produced
/// them. <see cref="InMemoryVectorSearchEngine.ExportSnapshot"/> writes one and
/// <see cref="InMemoryVectorSearchEngine.ImportSnapshot"/> reads it, refusing vectors whose
/// signature differs from the engine's provider.
/// </summary>
/// <param name="Entries">Documents with their stored vectors, as produced by <see cref="InMemoryVectorSearchEngine.Export"/>.</param>
/// <param name="EmbeddingSignature">
/// The provider's <see cref="Core.IEmbeddingSignatureProvider.EmbeddingSignature"/>, or
/// <see langword="null"/> when the provider does not implement that interface.
/// </param>
public sealed record VectorIndexSnapshot(
    IReadOnlyList<VectorIndexEntry> Entries,
    string? EmbeddingSignature);
