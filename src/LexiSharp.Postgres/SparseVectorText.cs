using System.Globalization;
using System.Text;

namespace LexiSharp.Postgres;

/// <summary>Serialization helpers for pgvector <c>sparsevec</c> literals (culture-invariant).</summary>
internal static class SparseVectorText
{
    /// <summary>
    /// Renders coordinate/weight pairs as a sparsevec literal, e.g. <c>{2:1.5,5:-2.25}/8</c>
    /// (coordinates 1-based, as pgvector expects). Zero, NaN and infinite weights are skipped.
    /// </summary>
    /// <param name="coordinates1Based">Coordinate (1-based) → weight, already validated in range.</param>
    /// <param name="dimension">The literal's declared dimension.</param>
    public static string Format(IEnumerable<(int Coordinate, float Weight)> coordinates1Based, int dimension)
    {
        if (dimension <= 0)
            throw new ArgumentException("A sparsevec dimension must be positive.", nameof(dimension));

        var builder = new StringBuilder();
        builder.Append('{');

        bool first = true;

        foreach (var (index, weight) in coordinates1Based)
        {
            if (index < 1 || index > dimension)
                throw new ArgumentOutOfRangeException(nameof(coordinates1Based), index,
                    $"Coordinate must be within 1..{dimension}.");

            if (weight == 0f || !float.IsFinite(weight))
                continue;

            if (!first)
                builder.Append(',');
            first = false;

            builder.Append(index.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(weight.ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append("}/");
        builder.Append(dimension.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>Parses a sparsevec literal into 1-based coordinates plus its declared dimension.</summary>
    public static (int Dimension, Dictionary<int, float> Coordinates) Parse(string literal)
    {
        string trimmed = literal.Trim();

        int slash = trimmed.LastIndexOf('/');
        int closeBrace = trimmed.LastIndexOf('}');

        if (slash < 0 || closeBrace <= 0 || slash <= closeBrace
            || !int.TryParse(trimmed[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int dimension))
        {
            throw new FormatException($"Invalid sparsevec literal '{literal}': missing closing brace or dimension.");
        }

        string body = trimmed[1..closeBrace].Trim();
        var coordinates = new Dictionary<int, float>();

        if (body.Length > 0)
        {
            foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0)
                    throw new FormatException($"Invalid sparsevec entry '{part}'.");

                int index = int.Parse(part[..colon], CultureInfo.InvariantCulture);
                float weight = float.Parse(part[(colon + 1)..], CultureInfo.InvariantCulture);
                coordinates[index] = weight;
            }
        }

        return (dimension, coordinates);
    }
}