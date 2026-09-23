using System.Text.Json;

namespace LexiSharp.Sources;

/// <summary>Knobs for <see cref="JsonDocumentsLoader"/>: which object property holds what.</summary>
public sealed record JsonDocumentLoadOptions
{
    /// <summary>Name of the property carrying the document id. Default: <c>id</c>.</summary>
    public string IdProperty { get; init; } = "id";

    /// <summary>Name of the property carrying the indexed text. Default: <c>text</c>.</summary>
    public string TextProperty { get; init; } = "text";

    /// <summary>Name of the property carrying the category; <c>null</c> disables it. Default: <c>null</c>.</summary>
    public string? CategoryProperty { get; init; }

    /// <summary>
    /// Whether every other scalar property becomes a document field (arrays are flattened to a
    /// comma-separated string). Default: <c>true</c>.
    /// </summary>
    public bool AdditionalPropertiesAsFields { get; init; } = true;
}

/// <summary>
/// Loads documents from a JSON array of objects, e.g. <c>[{"id": "1", "text": "hello"}]</c>.
/// </summary>
/// <remarks>
/// The root element must be a JSON array; every entry must be an object carrying the id and
/// text properties (their values may be strings, numbers or booleans — anything scalar).
/// Nested objects are skipped, arrays of scalars are flattened to a comma-separated string,
/// and duplicate keys are resolved by last-wins like the rest of <c>System.Text.Json</c>.
/// </remarks>
public static class JsonDocumentsLoader
{
    /// <summary>Parses a JSON array of documents.</summary>
    /// <exception cref="ArgumentException">The root is not an array, or an entry is not an object or misses the id/text property.</exception>
    public static IReadOnlyList<LoadedDocument> Parse(string json, JsonDocumentLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        var loaderOptions = options ?? new JsonDocumentLoadOptions();

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The JSON document must contain an array of objects.", nameof(json));

        var documents = new List<LoadedDocument>();
        int index = 0;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"JSON array entry {index} is not an object.", nameof(json));

            string id = ReadScalar(element, loaderOptions.IdProperty, index, "id", json);
            string text = ReadScalar(element, loaderOptions.TextProperty, index, "text", json);
            string? category = null;

            if (loaderOptions.CategoryProperty is not null)
                TryReadScalar(element, loaderOptions.CategoryProperty, out category);

            var fields = CollectFields(element, loaderOptions, index, json);

            documents.Add(new LoadedDocument(id, text, fields, category));
            index++;
        }

        return documents;
    }

    /// <summary>Reads and parses a JSON file containing an array of documents.</summary>
    public static IReadOnlyList<LoadedDocument> LoadFile(string path, JsonDocumentLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Parse(File.ReadAllText(path), options);
    }

    private static Dictionary<string, string> CollectFields(
        JsonElement element,
        JsonDocumentLoadOptions options,
        int index,
        string json)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!options.AdditionalPropertiesAsFields)
            return fields;

        foreach (var property in element.EnumerateObject())
        {
            string propertyName = property.Name;

            if (propertyName == options.IdProperty || propertyName == options.TextProperty)
                continue;

            if (options.CategoryProperty is not null && propertyName == options.CategoryProperty)
                continue;

            if (!TryReadScalar(element, propertyName, out string value))
                continue;

            fields[propertyName] = value;
        }

        return fields;
    }

    private static string ReadScalar(JsonElement element, string propertyName, int index, string role, string json)
    {
        if (TryReadScalar(element, propertyName, out string value))
            return value;

        throw new ArgumentException(
            $"JSON array entry {index} is missing the '{propertyName}' {role} property.", nameof(json));
    }

    /// <summary>
    /// Reads a scalar (or scalar-array) property as a string; arrays are flattened with ", ".
    /// Returns <c>true</c> when the property exists and holds a string, number, boolean, or an
    /// array of those; nested objects, <c>null</c> and absent properties return <c>false</c>.
    /// </summary>
    private static bool TryReadScalar(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();

            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number
                    or JsonValueKind.True or JsonValueKind.False))
                    continue;

                parts.Add(ScalarToString(item));
            }

            value = string.Join(", ", parts);
            return true;
        }

        if (property.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False)
        {
            value = ScalarToString(property);
            return true;
        }

        return false;
    }

    private static string ScalarToString(JsonElement scalar) => scalar.ValueKind switch
    {
        JsonValueKind.String => scalar.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => scalar.GetRawText(),
    };
}