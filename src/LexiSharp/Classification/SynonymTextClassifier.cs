using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// <see cref="ITextClassifier"/> decorator that canonicalizes terms in both training and query
/// text using a <see cref="SynonymTokenizer"/>, so a wrapped classifier sees one form per
/// synonym group without being retrained for each alias.
/// </summary>
public sealed class SynonymTextClassifier : ITextClassifier
{
    private readonly ITextClassifier _inner;
    private readonly SynonymTokenizer _tokenizer;

    /// <summary>Wraps <paramref name="inner"/> and canonicalizes text with <paramref name="tokenizer"/>.</summary>
    /// <param name="inner">The classifier to delegate to.</param>
    /// <param name="tokenizer">The synonym-aware tokenizer used to rewrite text.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SynonymTextClassifier(ITextClassifier inner, SynonymTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(tokenizer);

        _inner = inner;
        _tokenizer = tokenizer;
    }

    /// <summary>Wraps <paramref name="inner"/> and canonicalizes text with a synonym tokenizer built from <paramref name="synonyms"/> and <paramref name="tokenizer"/>.</summary>
    public SynonymTextClassifier(ITextClassifier inner, ITokenizer tokenizer, SynonymMap? synonyms)
        : this(inner, new SynonymTokenizer(tokenizer, synonyms))
    {
    }

    /// <summary>Wraps <paramref name="inner"/> and canonicalizes text with a synonym tokenizer built from <paramref name="synonyms"/> using <paramref name="tokenizer"/>.</summary>
    public SynonymTextClassifier(ITextClassifier inner, SynonymMap? synonyms, ITokenizer tokenizer)
        : this(inner, new SynonymTokenizer(tokenizer, synonyms))
    {
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var rewritten = new List<SearchDocument>();

        foreach (SearchDocument document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);

            rewritten.Add(new SearchDocument(
                document.Id,
                Canonicalize(document.Text),
                document.Fields,
                document.Category,
                document.TextFields));
        }

        _inner.Train(rewritten);
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        _inner.Predict(Canonicalize(text), limit, excludedCategories);

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
        _inner.PredictBest(Canonicalize(text), excludedCategories);

    private string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        IReadOnlyList<string> tokens = _tokenizer.Tokenize(text);
        return string.Join(' ', tokens);
    }
}
