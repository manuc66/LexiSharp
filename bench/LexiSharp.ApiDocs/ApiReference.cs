using System.Globalization;
using System.Text;

namespace LexiSharp.ApiDocs;

/// <summary>
/// Renders the public surface as one Markdown page: the contracts first, then the types, each
/// group labelled with the package that has to be referenced to get it.
/// </summary>
/// <remarks>
/// The output is a pure function of the assemblies and their XML documentation. It carries no
/// timestamp, no machine path, no build hash and no locale-dependent formatting, and it ends
/// every line with <c>\n</c> whatever the platform, so regenerating it on another machine
/// produces the same bytes and <c>--check</c> can gate a build on the difference. That is the
/// whole point: a page nobody has to remember to update is a page that cannot go stale.
/// </remarks>
internal static class ApiReference
{
    /// <summary>
    /// How much of a summary survives into a table cell. Long enough for a sentence to make its
    /// point, short enough that a cell does not become a paragraph: a reader who wants the
    /// argument reads the guide, or the XML documentation in their IDE.
    /// </summary>
    private const int SummaryLimit = 160;

    private const string Ellipsis = "…";

    /// <summary>The package a namespace belongs to, for the heading that names it.</summary>
    private const string CorePackage = "LexiSharp";

    /// <summary>
    /// Renders the page. <paramref name="assemblies"/> is a package name paired with its public
    /// types; packages are listed in the order given, and types within one in the ordinal order
    /// <see cref="ApiSurface"/> read them.
    /// </summary>
    public static string Render(IReadOnlyList<(string Package, IReadOnlyList<ApiType> Types)> assemblies)
    {
        var page = new StringBuilder();

        FrontMatter(page);
        Intro(page);

        Contracts(page, assemblies);
        Types(page, assemblies);

        return page.ToString();
    }

    private static void FrontMatter(StringBuilder page)
    {
        // The page is published by GitHub Pages' own Jekyll build, so it needs the same front
        // matter as every other page: nav_order 15 sits after the two deepest pages (SPLADE is 14).
        page.Append(
            """
            ---
            title: API reference
            nav_order: 15
            description: >-
              Every public type in the four packages, generated from the compiled assemblies and
              the XML documentation they ship: the contracts you implement, and the classes that
              implement them for you.
            ---

            """.ReplaceLineEndings("\n") + "\n");
    }

    private static void Intro(StringBuilder page)
    {
        page.Append(
            """
            # API reference

            The public surface, generated from the assemblies and the XML documentation they ship.
            The **What it is** column is the type's own summary, not a sentence written for this
            page: it is what the author wrote above the declaration, trimmed to its first
            paragraph. Where it is not enough, the **Guide** column is the page that argues the
            case.

            This page indexes the surface; it does not teach it. The [guide](index.md) is where a
            pipeline is built, and the API reference answers the narrower question of what exists
            to build it with.

            """.ReplaceLineEndings("\n") + "\n");
    }

    private static void Contracts(StringBuilder page, IReadOnlyList<(string Package, IReadOnlyList<ApiType> Types)> assemblies)
    {
        var withContracts = assemblies
            .Select(entry => (entry.Package, Contracts: entry.Types.Where(type => type.Kind == ApiTypeKind.Interface).ToList()))
            .Where(entry => entry.Contracts.Count > 0)
            .ToList();

        if (withContracts.Count == 0)
            return;

        page.Append("## The contracts\n\n");
        page.Append(
            """
            Every interface the library asks you to satisfy, and the ones it asks you to satisfy
            *something* with. These are the seams: an engine, a scorer, a merger, a reranker, a
            model provider. Implementing one is how the library is extended, which is why they
            are listed first.

            """.ReplaceLineEndings("\n") + "\n");

        foreach (var (package, contracts) in withContracts)
        {
            PackageHeading(page, package, level: 3);

            page.Append("| Contract | Extends | What it is | Guide |\n");
            page.Append("| --- | --- | --- | --- |\n");

            foreach (var contract in contracts)
            {
                page.Append(CultureInfo.InvariantCulture, $"| `{contract.Name}` | ");
                page.Append(Cell(List(contract.Contracts)));
                page.Append(" | ");
                page.Append(Cell(contract.Summary));
                page.Append(" | ");
                page.Append(Guide(contract));
                page.Append(" |\n");
            }

            page.Append('\n');
        }
    }

    private static void Types(StringBuilder page, IReadOnlyList<(string Package, IReadOnlyList<ApiType> Types)> assemblies)
    {
        var withTypes = assemblies
            .Select(entry => (entry.Package, Types: entry.Types.Where(type => type.Kind != ApiTypeKind.Interface).ToList()))
            .Where(entry => entry.Types.Count > 0)
            .ToList();

        if (withTypes.Count == 0)
            return;

        page.Append("## The types\n\n");
        page.Append(
            """
            Everything else that is public, by namespace. `record`, `record struct` and `class` are
            told apart by the members the compiler adds to each — the difference is in the page
            because `SearchDocument` is constructed and `InMemoryTextIndex` is not, and because a
            `record struct` is neither: it is a value type with value equality and a `with`
            expression, and calling it a `struct` would be as wrong as calling a `record` a class.

            """.ReplaceLineEndings("\n") + "\n");

        foreach (var (package, types) in withTypes)
        {
            PackageHeading(page, package, level: 3);

            foreach (var group in types.GroupBy(type => type.Namespace, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                page.Append(CultureInfo.InvariantCulture, $"#### `{group.Key}`\n\n");
                page.Append("| Type | Kind | Contracts | What it is |\n");
                page.Append("| --- | --- | --- | --- |\n");

                foreach (var type in group)
                {
                    page.Append(CultureInfo.InvariantCulture, $"| `{type.Name}` | {Kind(type.Kind)} | ");
                    page.Append(Cell(List(type.Contracts)));
                    page.Append(" | ");
                    page.Append(Cell(type.Summary));
                    page.Append(" |\n");
                }

                page.Append('\n');
            }
        }
    }

    private static void PackageHeading(StringBuilder page, string package, int level)
    {
        string hashes = new('#', level);

        page.Append(CultureInfo.InvariantCulture, $"{hashes} {PackageLabel(package)}\n\n");
    }

    private static string PackageLabel(string package) => package == CorePackage
        ? "Core package"
        : $"`{package}` package";

    private static string Kind(ApiTypeKind kind) => kind switch
    {
        ApiTypeKind.Interface => "interface",
        ApiTypeKind.Record => "record",
        ApiTypeKind.Class => "class",
        ApiTypeKind.RecordStruct => "record struct",
        ApiTypeKind.Struct => "struct",
        ApiTypeKind.Enum => "enum",
        ApiTypeKind.Delegate => "delegate",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string List(IReadOnlyList<string> contracts) =>
        contracts.Count == 0 ? "—" : string.Join(", ", contracts.Select(name => $"`{name}`"));

    private static string Guide(ApiType type) => type.Guide.Length == 0 ? "—" : $"[guide]({type.Guide})";

    /// <summary>
    /// A summary, trimmed to one line and made safe for a table cell: a pipe would end the cell,
    /// angle brackets would start markup, and a line break would end the row.
    /// </summary>
    private static string Cell(string summary)
    {
        if (summary.Length == 0)
            return "—";

        string text = summary
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        return Truncate(text, SummaryLimit);
    }

    /// <summary>
    /// Cuts at the last space before the limit, so the text is never cut mid-word and never
    /// exceeds the limit plus the ellipsis.
    /// </summary>
    private static string Truncate(string text, int limit)
    {
        if (text.Length <= limit)
            return text;

        int cut = text.LastIndexOf(' ', limit);
        return (cut <= 0 ? text[..limit] : text[..cut]).TrimEnd() + Ellipsis;
    }
}
