namespace LexiSharp.Core;

/// <summary>
/// The shape of a document field, used by <see cref="MetadataFilter"/> to interpret the stored
/// string value when no explicit parsing is requested.
/// </summary>
public enum FieldType
{
    /// <summary>A single string value (the default).</summary>
    String,

    /// <summary>
    /// A delimited list of strings stored in one field (e.g. <c>"ml|deep-learning|nlp"</c>).
    /// Filtering tests membership of the list rather than substring overlap.
    /// </summary>
    StringArray,
}