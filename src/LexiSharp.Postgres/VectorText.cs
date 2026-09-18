using System.Globalization;
using System.Text;

namespace LexiSharp.Postgres;

/// <summary>Serialization helpers for pgvector literals (culture-invariant).</summary>
internal static class VectorText
{
    /// <summary>Renders a float vector as a pgvector literal, e.g. <c>[1.5,0,-2.25]</c>.</summary>
    public static string Format(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
            throw new ArgumentException("Cannot serialize an empty vector.", nameof(vector));

        var builder = new StringBuilder(vector.Length * 8 + 2);
        builder.Append('[');

        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>Parses a pgvector literal, used by tests to assert round-tripping.</summary>
    public static float[] Parse(string literal)
    {
        var inner = literal.Trim().Trim('[', ']');
        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var values = new float[parts.Length];

        for (int i = 0; i < parts.Length; i++)
            values[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);

        return values;
    }
}