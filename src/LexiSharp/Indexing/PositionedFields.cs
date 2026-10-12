using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// One token run of a document — its body or one text field — with the position its first token was
/// indexed at.
/// </summary>
/// <param name="Field">The field name, <see cref="TextFields.Default"/> for the body.</param>
/// <param name="Terms">The run's tokens, in order.</param>
/// <param name="Start">The position of <paramref name="Terms"/>'s first token.</param>
internal readonly record struct FieldRun(string Field, IReadOnlyList<string> Terms, int Start);

/// <summary>
/// The one place a document's fields are laid out in position space.
/// </summary>
/// <remarks>
/// <para>
/// The body's tokens come first, then each text field's after a <see cref="TextFields.PositionGap"/>
/// gap, so a phrase query can match inside one field and never bridge two of them. Every index that
/// records or recomputes a document's positions has to lay them out this way — the in-memory index
/// when it builds its posting lists, the overlay when it folds a live write, and a segment when it
/// recomputes positions from the text it stores — or the same document scores differently depending
/// on which index read it. They call this, so there is one layout rather than three that agree today.
/// </para>
/// <para>
/// The gap is applied to the runs' positions, not to <see cref="FieldRun.Terms"/>: the term list is
/// the union with no placeholder in it, which is what <c>GetTerms</c> returns.
/// </para>
/// </remarks>
internal static class PositionedFields
{
    /// <summary>
    /// A document's token runs: the body, then every text field in the order it is enumerated.
    /// </summary>
    public static List<FieldRun> Runs(ITokenizer tokenizer, SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(document);

        var body = tokenizer.Tokenize(document.Text);
        var runs = new List<FieldRun>(1 + (document.TextFields?.Count ?? 0))
        {
            new(TextFields.Default, body, 0),
        };

        if (document.TextFields is not { Count: > 0 } fields)
            return runs;

        int next = body.Count;

        foreach (var (field, text) in fields)
        {
            TextFields.Validate(field, nameof(SearchDocument.TextFields));

            var terms = tokenizer.Tokenize(text);

            next += TextFields.PositionGap;
            runs.Add(new FieldRun(field, terms, next));
            next += terms.Count;
        }

        return runs;
    }

    /// <summary>
    /// A document's tokens paired with the position each is indexed at, body first then every field.
    /// </summary>
    public static List<(string Term, int Position)> Positioned(ITokenizer tokenizer, SearchDocument document)
    {
        var runs = Runs(tokenizer, document);
        var all = new List<(string Term, int Position)>();

        foreach (var run in runs)
        {
            for (int i = 0; i < run.Terms.Count; i++)
                all.Add((run.Terms[i], run.Start + i));
        }

        return all;
    }
}
