using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>What the user did with a suggestion.</summary>
public enum FeedbackOutcome
{
    /// <summary>The user accepted the offered category.</summary>
    Accepted,

    /// <summary>The user chose another category.</summary>
    Corrected,

    /// <summary>The user rejected the offer without choosing a replacement.</summary>
    Rejected,

    /// <summary>The system offered nothing; the user may or may not have chosen a category.</summary>
    Abstained,

    /// <summary>The user saw the offer and did nothing. It is not a rejection.</summary>
    Ignored,
}

/// <summary>One logged interaction with a suggestion, at the instant it happened.</summary>
/// <param name="Text">The text the suggestion was about. It is not stored in a snapshot.</param>
/// <param name="OfferedCategory">The category the system proposed, or null for an abstention.</param>
/// <param name="AcceptedCategory">The category the user chose, or null when there was none.</param>
/// <param name="Context">The caller's context key: a sender, a repository, a folder, a request.</param>
/// <param name="At">The instant the interaction happened; it must not run backwards in one memory.</param>
/// <param name="Outcome">What the user did.</param>
/// <param name="Propensity">The probability the logging policy assigned to the offer, for IPS. Optional.</param>
/// <param name="Offered">The distribution that was shown, for calibration. Optional.</param>
public sealed record FeedbackEvent<TContext>(
    string Text,
    string? OfferedCategory,
    string? AcceptedCategory,
    TContext Context,
    DateTimeOffset At,
    FeedbackOutcome Outcome,
    double? Propensity = null,
    IReadOnlyList<ClassificationResult>? Offered = null)
    where TContext : notnull;

/// <summary>Encodes a context into the stable, opaque key a memory persists.</summary>
/// <typeparam name="TContext">The caller's context type.</typeparam>
public interface IContextCodec<TContext> where TContext : notnull
{
    /// <summary>A stable, versioned identity for the codec; a snapshot with another id is refused.</summary>
    string Id { get; }

    /// <summary>Whether the encoded key hides the context; the snapshot records this flag.</summary>
    bool IsOpaque { get; }

    /// <summary>Encodes one context. The same context must always produce the same key.</summary>
    /// <param name="context">The context to encode.</param>
    string Encode(TContext context);
}

/// <summary>The built-in context codecs: a hashed key by default, a raw key for debugging.</summary>
public static class ContextCodecs
{
    /// <summary>A SHA-256 key truncated to 128 bits, rendered as lowercase hexadecimal.</summary>
    /// <typeparam name="TContext">The caller's context type.</typeparam>
    public static IContextCodec<TContext> Hashed<TContext>() where TContext : notnull =>
        new HashedContextCodec<TContext>();

    /// <summary>
    /// The context's string form, in clear. It is an explicit opt-in: a snapshot then contains the
    /// context and must be protected like the log it came from.
    /// </summary>
    /// <typeparam name="TContext">The caller's context type.</typeparam>
    public static IContextCodec<TContext> Raw<TContext>() where TContext : notnull =>
        new RawContextCodec<TContext>();

    private sealed class HashedContextCodec<TContext> : IContextCodec<TContext> where TContext : notnull
    {
        public string Id => $"sha256-128:v1:{typeof(TContext).FullName}";

        public bool IsOpaque => true;

        public string Encode(TContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            string text = context as string
                ?? Convert.ToString(context, CultureInfo.InvariantCulture)
                ?? string.Empty;

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));

            return Convert.ToHexStringLower(hash.AsSpan(0, 16));
        }
    }

    private sealed class RawContextCodec<TContext> : IContextCodec<TContext> where TContext : notnull
    {
        public string Id => $"raw:v1:{typeof(TContext).FullName}";

        public bool IsOpaque => false;

        public string Encode(TContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return context as string
                ?? Convert.ToString(context, CultureInfo.InvariantCulture)
                ?? string.Empty;
        }
    }
}

/// <summary>Bounds and smoothing for a <see cref="FeedbackMemory{TContext}"/>.</summary>
public sealed record FeedbackMemoryOptions
{
    /// <summary>The default options: veto after 3 refusals, 100 000 contexts, 64 categories each.</summary>
    public static FeedbackMemoryOptions Default { get; } = new();

    /// <summary>Refusal mass at which a category is no longer offered in a context. Default: <c>3</c>.</summary>
    public int VetoAfter { get; init; } = 3;

    /// <summary>Maximum number of contexts kept; the least recently used is evicted. Default: <c>100_000</c>.</summary>
    public int MaxContexts { get; init; } = 100_000;

    /// <summary>Maximum number of category entries in one context. Default: <c>64</c>.</summary>
    public int MaxCategoriesPerContext { get; init; } = 64;

    /// <summary>Maximum number of category entries across all contexts. Default: <c>1_000_000</c>.</summary>
    public long MaxPairs { get; init; } = 1_000_000;

    /// <summary>Masses below this are dropped. Default: <c>1e-4</c>.</summary>
    public double MinMass { get; init; } = 1e-4;

    /// <summary>Shrinkage of a context's acceptance share towards the global share. Default: <c>10</c>.</summary>
    public double Shrinkage { get; init; } = 10;

    /// <summary>Dirichlet smoothing of the global acceptance distribution. Default: <c>0.5</c>.</summary>
    public double GlobalSmoothing { get; init; } = 0.5;
}

/// <summary>One category's evidence in a context, projected to an instant.</summary>
/// <param name="Category">The category label.</param>
/// <param name="AcceptanceShare">The shrunk acceptance share <c>m</c>, in <c>[0, 1]</c>.</param>
/// <param name="GlobalShare">The global acceptance share the context was shrunk towards.</param>
/// <param name="AcceptanceMass">The decayed acceptance mass.</param>
/// <param name="RefusalMass">The decayed refusal mass.</param>
public sealed record CategoryPrior(
    string Category,
    double AcceptanceShare,
    double GlobalShare,
    double AcceptanceMass,
    double RefusalMass)
{
    /// <summary>
    /// Whether the acceptance share comes only from the global prior: the context has no acceptance
    /// mass of its own. A UI that labels the multiplier as context evidence must check this flag.
    /// </summary>
    public bool IsInheritedFromGlobal => AcceptanceMass == 0 && GlobalShare > 0;
}

/// <summary>The evidence a memory holds for one context, projected to an instant.</summary>
/// <param name="Categories">Categories with evidence, by decreasing acceptance share then ordinal.</param>
/// <param name="Evidence">The context's evidence mass, <c>T / (T + κ)</c>, in <c>[0, 1)</c>.</param>
/// <param name="ResponseRate">Responses divided by exposures, in <c>[0, 1]</c>; ignored offers count as exposures.</param>
/// <param name="Observations">How many interactions the context has seen.</param>
/// <param name="LastUpdated">The instant of the last interaction.</param>
public sealed record ContextPrior(
    IReadOnlyList<CategoryPrior> Categories,
    double Evidence,
    double ResponseRate,
    long Observations,
    DateTimeOffset LastUpdated);
