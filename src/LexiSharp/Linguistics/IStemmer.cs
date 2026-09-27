namespace LexiSharp.Linguistics;

/// <summary>
/// Pluggable stemmer used by the <see cref="Tokenizer"/> to fold inflected forms
/// onto a common stem. The library ships one implementation,
/// <see cref="PorterStemmer"/> (English, no dependency); consumers needing another
/// language or another algorithm bring their own (Snowball, Lucene.NET analyzers, a
/// French stemmer, ...).
/// </summary>
public interface IStemmer
{
    /// <summary>Returns the normalized stem of a single term.</summary>
    string Stem(string term);
}