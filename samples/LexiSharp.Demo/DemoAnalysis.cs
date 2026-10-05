using LexiSharp.Linguistics;

namespace LexiSharp.Demo;

/// <summary>
/// The word segmentation a run uses, named as the evaluation harness names it so the two are
/// recognisably the same setting.
/// </summary>
public static class DemoAnalysis
{
    /// <summary>Name of the default.</summary>
    public const string DefaultName = "uax29";

    /// <summary>Every name <c>--segmentation</c> accepts.</summary>
    public static readonly IReadOnlyList<string> Names = ["flat", "uax29"];

    /// <summary>Resolves an option value.</summary>
    /// <exception cref="DemoOptionException">The name is not one of <see cref="Names"/>.</exception>
    public static WordSegmentation Resolve(string name) => name switch
    {
        "flat" => WordSegmentation.Flat,
        "uax29" => WordSegmentation.UnicodeWordBoundaries,
        _ => throw new DemoOptionException(
            $"Unknown segmentation '{name}'. Available: {string.Join(", ", Names)}."),
    };

    /// <summary>Names the segmentation in <paramref name="segmentation"/>, for the UI.</summary>
    public static string Name(WordSegmentation segmentation) => segmentation switch
    {
        WordSegmentation.Flat => "flat",
        WordSegmentation.UnicodeWordBoundaries => "uax29",
        _ => segmentation.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// What the segmentation actually joins, in the words a reader would use to predict a token.
    /// The point of the option is that it is not a list of separators but a list of conditions, so
    /// naming the conditions is the only description that predicts a case.
    /// </summary>
    public static string Describe(WordSegmentation segmentation) => segmentation switch
    {
        // A lone digit is dropped, not emitted: the tokenizer discards terms shorter than two
        // characters unless KeepSingleCharTerms is set, and the demo does not set it. So `1,000`
        // indexes as `000`, and the `1` is not a term with a low weight -- it is absent.
        WordSegmentation.Flat =>
            "flat — every non-word character ends a word, so 1,000 indexes as 000 and don't as don",
        WordSegmentation.UnicodeWordBoundaries =>
            "uax29 — comma and semicolon between two digits, colon between two letters, full stop and " +
            "apostrophes within one class, underscore and soft hyphen anywhere",
        _ => segmentation.ToString(),
    };
}