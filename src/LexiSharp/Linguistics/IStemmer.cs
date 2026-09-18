namespace LexiSharp.Linguistics;

/// <summary>
/// Pluggable stemmer used by the <see cref="Tokenizer"/> to fold inflected forms
/// onto a common stem. The library does not ship an implementation: consumers are
/// expected to provide their own (Snowball, Lucene.NET analyzers, a French stemmer, ...).
/// </summary>
public interface IStemmer
{
    /// <summary>Returns the normalized stem of a single term.</summary>
    string Stem(string term);
}