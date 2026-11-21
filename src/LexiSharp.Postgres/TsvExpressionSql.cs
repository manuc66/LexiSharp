using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// A <c>tsv</c> expression and the values it binds.
/// </summary>
/// <remarks>
/// The expression is SQL built from configuration — the text search configuration, the weight
/// classes, and the field names. The configuration is validated, but a field name is free-form
/// caller text: it is a <em>value</em> of the expression (<c>jsonb -&gt;&gt; name</c>, <c>key not in
/// (...)</c>), never syntax. Binding it here is what removes the old literal's dependence on
/// <c>standard_conforming_strings</c>: a server with the setting off treated a backslash as an
/// escape, and doubling the quote was not enough.
/// </remarks>
/// <param name="Sql">The expression, with one placeholder per entry in <paramref name="Parameters"/>.</param>
/// <param name="Parameters">The field names the expression refers to, by parameter name.</param>
public sealed record TsvExpressionSql(
    string Sql,
    IReadOnlyList<KeyValuePair<string, string>> Parameters)
{
    /// <summary>Binds every parameter this expression needs to the command that carries its SQL.</summary>
    /// <param name="command">The command whose statement text includes <see cref="Sql"/>.</param>
    public void Bind(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var parameter in Parameters)
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
    }
}
