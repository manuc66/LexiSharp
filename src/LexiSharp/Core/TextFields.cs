namespace LexiSharp.Core;

/// <summary>
/// Field naming rules shared by the index and the field-aware scorers.
/// </summary>
public static class TextFields
{
    /// <summary>
    /// Name of the field holding a document's main <see cref="SearchDocument.Text"/>.
    /// </summary>
    /// <remarks>
    /// The empty string is the conventional default-field name in IR toolkits, and it cannot
    /// collide with a named <see cref="SearchDocument.TextFields"/> key: those must be non-empty,
    /// which is what <see cref="IsValidFieldName"/> checks.
    /// </remarks>
    public const string Default = "";

    /// <summary>
    /// Whether a name can be used as a field. <see cref="Default"/> is the reserved default field
    /// and is valid; every other name must be non-empty, and null is never valid.
    /// </summary>
    /// <remarks>
    /// A quoted phrase matches inside a single field and never bridges two of them, so a field
    /// name has to be distinguishable from the default one to keep that guarantee.
    /// </remarks>
    public static bool IsValidFieldName(string? field) =>
        field is not null && (field.Length == 0 || field.Trim().Length > 0);

    /// <summary>
    /// Validates a field name, throwing the exception callers of a <c>field</c> argument expect.
    /// </summary>
    /// <param name="field">The name to check.</param>
    /// <param name="parameterName">The parameter to name in the thrown exception.</param>
    /// <returns>The validated name, unchanged.</returns>
    /// <exception cref="ArgumentException">The name is null or blank.</exception>
    public static string Validate(string? field, string parameterName)
    {
        // IsValidFieldName, not IsNullOrWhiteSpace: the default field IS the empty string, so a
        // blank check would reject the very name the interface hands out.
        if (!IsValidFieldName(field))
        {
            throw new ArgumentException(
                $"Field name must be non-blank. Use {nameof(TextFields)}.{nameof(Default)} for the " +
                "document's main text.",
                parameterName);
        }

        return field!;
    }
}
