namespace LexiSharp.Core;

/// <summary>
/// One query term restricted to a single field: <c>title:ssh</c> is
/// <c>new FieldScopedTerm("title", "ssh")</c>.
/// </summary>
/// <remarks>
/// A scoped term is matched and scored from one field's statistics. Its contribution is zero when
/// the document does not carry the term in that field, which is what makes a query of only scoped
/// terms return only documents whose match is in the field.
/// <para>
/// A scoped term is an ordinary scoring term with a field attached: it is <i>not</i> a hard gate
/// unless it is written with a leading <c>+</c> (or, for a phrase, by quoting). See
/// <see cref="ParsedQuery.ScopedTerms"/> for the scoring set and
/// <see cref="ParsedQuery.RequiredScopedTerms"/> for the required ones.
/// </para>
/// </remarks>
/// <param name="Field">A field name from <see cref="IReadOnlyTextIndex.Fields"/>.</param>
/// <param name="Term">A token as produced by the index's tokenizer.</param>
public sealed record FieldScopedTerm(string Field, string Term);

/// <summary>
/// A double-quoted phrase restricted to a single field: <c>title:"ssh tunnel"</c> asks for the
/// phrase at consecutive positions <b>inside that field</b>.
/// </summary>
/// <remarks>
/// The terms of an unqualified or required scoped phrase also score, each as the corresponding
/// <see cref="FieldScopedTerm"/>; a prohibited one contributes nothing to the score.
/// </remarks>
/// <param name="Field">A field name from <see cref="IReadOnlyTextIndex.Fields"/>.</param>
/// <param name="Terms">The phrase's terms, in reading order, already tokenized.</param>
public sealed record FieldScopedPhrase(string Field, IReadOnlyList<string> Terms)
{
    /// <summary>
    /// The <c>+</c>/<c>-</c> operator that qualified the phrase, or
    /// <see cref="QueryTermOperator.None"/> when it carried none.
    /// </summary>
    public QueryTermOperator Operator { get; init; } = QueryTermOperator.None;
}
