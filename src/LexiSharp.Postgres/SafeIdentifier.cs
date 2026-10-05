using System.Text.RegularExpressions;

namespace LexiSharp.Postgres;

/// <summary>
/// The check every interpolated SQL identifier passes, and the quoting that follows it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, deliberately.</b> This is the only thing standing between a caller's string and a
/// statement: <c>Schema</c>, <c>Table</c>, <c>TextSearchConfig</c>, <c>ContentField</c> and the
/// embedding column names all reach DDL by interpolation, and every option record reads this rather
/// than carrying a pattern of its own. Five copies of a security check is four chances to have one of
/// them differ from the other four, and nothing in a build would notice when one did.
/// </para>
/// <para>
/// One instance, one parse, for a check that runs on every configuration.
/// </para>
/// <para>
/// NonBacktracking selects the runtime's linear-time engine, which cannot backtrack and so cannot be
/// walked into the exponential blowup a nested quantifier would allow. That is the whole reason this
/// check exists.
/// </para>
/// <para>
/// Written out rather than declared with [GeneratedRegex] (SYSLIB1045): the source generator cannot
/// emit a specialized matcher for NonBacktracking and falls back to precisely this constructor call,
/// which is the SYSLIB1044 the build reports. The comment it replaced said "compiled once at startup
/// by the source generator"; the generated file said "a custom Regex-derived type could not be
/// generated because RegexOptions.NonBacktracking isn't supported". The second sentence was true
/// whichever way it went.
/// </para>
/// </remarks>
internal static class SafeIdentifier
{
    private static readonly Regex SafeNameRegex =
        new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Whether <paramref name="name"/> is a name this library is willing to interpolate into SQL.
    /// </summary>
    /// <remarks>
    /// The shape of an unquoted PostgreSQL identifier, which is why a leading digit is accepted: a
    /// name like <c>9lives</c> or an all-digit <c>123</c> is legal once quoted, and the quoter here
    /// quotes it.
    /// </remarks>
    internal static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return SafeNameRegex.IsMatch(name);
    }

    /// <summary>
    /// The name as a quoted identifier, doubling any embedded quote. This is what makes an accepted
    /// name safe to interpolate: the pattern is what keeps a name to characters nobody has to reason
    /// about, and the quoting is what keeps a statement whole whatever else arrives.
    /// </summary>
    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}