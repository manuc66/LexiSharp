namespace LexiSharp.Classification;

/// <summary>
/// A yes/no question asked of an item's features — the unit a question tree is built from.
/// </summary>
/// <remarks>
/// A question is a pure predicate over the feature set the caller's <see cref="IFeatureSource{TItem}"/>
/// produced. It knows nothing about text, time, transitions or any other domain: a feature is a
/// name, and how that name was derived belongs to the caller. The built-in builders ask
/// <see cref="FeaturePresenceQuestion"/> and nothing else; a caller that needs a numeric range or
/// a conjunction can derive a question of its own, because this type is the extension point.
/// </remarks>
public abstract record FeatureQuestion
{
    /// <summary>Answers the question for one item's feature set.</summary>
    /// <param name="features">The features of the item, as the model's source produced them.</param>
    /// <returns><c>true</c> when the question is answered yes.</returns>
    public abstract bool Matches(IFeatureSet features);

    /// <summary>Renders the question for an explanation, e.g. <c>contains "invoice"</c>.</summary>
    /// <returns>A short human-readable form, never null.</returns>
    public abstract string Describe();
}

/// <summary>
/// True when the item carries a given feature — the only question the built-in builders ask.
/// </summary>
/// <param name="Feature">The feature name to look for.</param>
public sealed record FeaturePresenceQuestion(string Feature) : FeatureQuestion
{
    /// <inheritdoc />
    public override bool Matches(IFeatureSet features)
    {
        ArgumentNullException.ThrowIfNull(features);

        return features.Contains(Feature);
    }

    /// <inheritdoc />
    public override string Describe() => $"contains \"{Feature}\"";
}
