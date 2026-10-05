using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The identifier check every PostgreSQL-backed engine runs on its options, exercised without a
/// server.
/// </summary>
/// <remarks>
/// <para>
/// Schema, table and text-search configuration are interpolated into DDL, so this guard is the only
/// thing between a caller's string and a statement. It is also the one piece of these engines that
/// has nothing to do with a connection, which is why it is tested here rather than in the
/// integration suites that skip without <c>POSTGRES_TEST_CONNECTION</c>: a guard that only runs where
/// a database happens to be listening is a guard whose regression nothing notices.
/// </para>
/// <para>
/// The accepted cases pass <c>autoCreateSchema: false</c>, because the default is to create the
/// schema from the constructor and that opens a connection. The rejected cases do not need it —
/// <c>ArgumentException</c> for a bad identifier is thrown before any of that — which is the order
/// the guard is supposed to run in, so the fact that they need no opt-out is itself part of what is
/// being pinned.
/// </para>
/// </remarks>
public class PostgresIdentifierValidationTests
{
    /// <summary>Unreachable on purpose: nothing here may open a connection.</summary>
    private const string Unused = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

    // The two engines that take an embedding producer null-check it before they reach the guard, so
    // a non-null instance has to be supplied to get as far as the guard at all. It is never called.
    private sealed class NeverCalled : IEmbeddingProvider, ISparseEmbeddingProvider
    {
        public int Dimension => 1;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The identifier guard runs before any embedding.");

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The identifier guard runs before any embedding.");
    }

    /// <summary>
    /// Strings that must be accepted. The shape of a SQL identifier, including the awkward ends: a
    /// leading digit is legal in an unquoted PostgreSQL identifier, and an all-digit name is a name.
    /// </summary>
    public static TheoryData<string> AcceptedNames => new()
    {
        "public",
        "lexisharp_documents",
        "PUBLIC",
        "_leading_underscore",
        "9lives",
        "123",
        "a",
        "with_digits_123",
    };

    /// <summary>
    /// Strings that must be rejected. Each is a way past a naive check: a quote ends a literal, a
    /// semicolon ends a statement, a comment swallows the rest of it, whitespace and a dot introduce
    /// a different grammar, and an empty string is not a name.
    /// </summary>
    public static TheoryData<string> RejectedNames => new()
    {
        "has space",
        "has-dash",
        "has.dot",
        "has;semicolon",
        "has'quote",
        "has\"dquote",
        "has--comment",
        "has/*comment*/",
        "has\nnewline",
        "has\ttab",
        "trailing ",
        " leading",
        "",
    };

    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public void AnIdentifierOfTheAllowedShape_IsAcceptedByEveryEngine(string name)
    {
        // The guard each engine consults is asserted on the options themselves, so a constructor
        // that stopped consulting it fails here rather than passing on the absence of a throw.
        var lexical = new PostgresIndexOptions { Schema = name, Table = name };
        var parade = new ParadeDB.ParadeDBOptions { Schema = name, Table = name };
        var fuzzy = new PostgresFuzzyOptions { Schema = name, Table = name };
        var sparse = new PostgresSparseOptions { Schema = name, Table = name, Vocabulary = new Dictionary<string, int> { ["term"] = 0 } };
        var vector = new PostgresVectorOptions { Schema = name, Table = name };

        Assert.True(lexical.IsValid);
        Assert.True(parade.IsValid);
        Assert.True(fuzzy.IsValid);
        Assert.True(sparse.IsValid);
        Assert.True(vector.IsValid);

        // Schema and Table together, because each engine reads both through the same check and a
        // guard covering only one of them would still pass this.
        using (new PostgresTextSearchEngine(Unused, lexical, autoCreateSchema: false))
        {
        }

        using (new ParadeDB.ParadeDBTextSearchEngine(Unused, parade, autoCreateSchema: false))
        {
        }

        using (new PostgresFuzzySearchEngine(Unused, fuzzy, autoCreateSchema: false))
        {
        }

        using (new PostgresSparseSearchEngine(Unused, new NeverCalled(), sparse, autoCreateSchema: false))
        {
        }

        using (new PostgresVectorSearchEngine(Unused, new NeverCalled(), vector, autoCreateSchema: false))
        {
        }
    }

    [Theory]
    [MemberData(nameof(RejectedNames))]
    public void AnIdentifierOutsideTheAllowedShape_IsRejectedByEveryEngine(string name)
    {
        // Schema and Table each, on all five engines: a check that covered only one field, or only
        // one engine, would still satisfy the accepted case above.
        AssertRejected(() => new PostgresTextSearchEngine(Unused, new PostgresIndexOptions { Schema = name }));
        AssertRejected(() => new PostgresTextSearchEngine(Unused, new PostgresIndexOptions { Table = name }));
        AssertRejected(() => new ParadeDB.ParadeDBTextSearchEngine(Unused, new ParadeDB.ParadeDBOptions { Schema = name }));
        AssertRejected(() => new ParadeDB.ParadeDBTextSearchEngine(Unused, new ParadeDB.ParadeDBOptions { Table = name }));
        AssertRejected(() => new PostgresFuzzySearchEngine(Unused, new PostgresFuzzyOptions { Schema = name }));
        AssertRejected(() => new PostgresFuzzySearchEngine(Unused, new PostgresFuzzyOptions { Table = name }));
        AssertRejected(() => new PostgresSparseSearchEngine(Unused, new NeverCalled(), new PostgresSparseOptions { Schema = name }));
        AssertRejected(() => new PostgresSparseSearchEngine(Unused, new NeverCalled(), new PostgresSparseOptions { Table = name }));
        AssertRejected(() => new PostgresVectorSearchEngine(Unused, new NeverCalled(), new PostgresVectorOptions { Schema = name }));
        AssertRejected(() => new PostgresVectorSearchEngine(Unused, new NeverCalled(), new PostgresVectorOptions { Table = name }));
    }

    [Fact]
    public void TheTextSearchConfiguration_IsCheckedLikeAnIdentifier()
    {
        // PostgresIndexOptions reads a third field through the same pattern, and it lands in a SET
        // clause rather than a table name — a guard covering two of the three would leave it.
        using (new PostgresTextSearchEngine(
            Unused,
            new PostgresIndexOptions { TextSearchConfig = "simple" },
            autoCreateSchema: false))
        {
        }

        AssertRejected(() => new PostgresTextSearchEngine(
            Unused,
            new PostgresIndexOptions { TextSearchConfig = "simple; DROP TABLE x" }));

        AssertRejected(() => new PostgresTextSearchEngine(
            Unused,
            new PostgresIndexOptions { TextSearchConfig = "un accent" }));
    }

    [Fact]
    public void TheVectorEngineChecksItsEmbeddingColumnNames()
    {
        // EmbeddingColumns keys become column names, so this is the widest of these surfaces and the
        // one most worth pinning. An empty value is rejected alongside a bad key: the pair is the
        // shape of "one label, one target", and half of it is not usable.
        using (new PostgresVectorSearchEngine(
            Unused,
            new NeverCalled(),
            new PostgresVectorOptions
            {
                EmbeddingColumns = new Dictionary<string, string> { ["content_vec"] = "content" },
            },
            autoCreateSchema: false))
        {
        }

        AssertRejected(() => new PostgresVectorSearchEngine(
            Unused,
            new NeverCalled(),
            new PostgresVectorOptions
            {
                EmbeddingColumns = new Dictionary<string, string> { ["content vec; DROP TABLE x"] = "content" },
            }));

        AssertRejected(() => new PostgresVectorSearchEngine(
            Unused,
            new NeverCalled(),
            new PostgresVectorOptions
            {
                EmbeddingColumns = new Dictionary<string, string> { ["ok"] = "" },
            }));
    }

    private static void AssertRejected(Func<IDisposable> construct) =>
        Assert.Throws<ArgumentException>(construct);

    // ---- the predicate itself, over generated names ----------------------------------------------

    /// <summary>
    /// The characters worth generating: the two the guard accepts, and every kind it exists to
    /// refuse. A newline is in the alphabet on purpose — see <see cref="TheGuardRejectsANameEndingInANewline"/>.
    /// </summary>
    private static readonly char[] GuardAlphabet =
        ['a', 'Z', '7', '_', ' ', '"', '\'', ';', '-', '.', '/', '\n', '\r', '\t', '\\', 'é'];

    private static Gen<string> GuardNames() =>
        from length in Gen.Choose(0, 8)
        from chars in Gen.Elements(GuardAlphabet).ArrayOf(length)
        select new string(chars);

    private static readonly Arbitrary<string> Names = Arb.From(GuardNames());

    /// <summary>The guard's promise, written out independently of the pattern that keeps it.</summary>
    private static bool Expected(string name) =>
        name.Length > 0 && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    /// <summary>
    /// The check accepts exactly <c>[A-Za-z0-9]+</c> and nothing else — the claim its comment, its
    /// five callers' error messages and this repository's docs all make about it.
    /// </summary>
    [Property(MaxTest = 2_000)]
    public Property TheGuardAcceptsExactlyTheCharactersItPromises() =>
        Prop.ForAll(Names, name => SafeIdentifier.IsValid(name) == Expected(name));

    /// <summary>
    /// Every engine's options answer with the one check. Stated over generated names rather than
    /// over the two tables above, because agreement on thirteen hand-picked strings is not the same
    /// claim as agreement on the predicate.
    /// </summary>
    [Property(MaxTest = 2_000)]
    public Property EveryEngineConsultsTheSameGuard() =>
        Prop.ForAll(Names, name =>
        {
            bool expected = SafeIdentifier.IsValid(name);

            return new PostgresIndexOptions { Schema = name }.IsValid == expected
                && new ParadeDB.ParadeDBOptions { Schema = name }.IsValid == expected
                && new PostgresFuzzyOptions { Schema = name }.IsValid == expected
                && new PostgresSparseOptions
                {
                    Schema = name,
                    Vocabulary = new Dictionary<string, int> { ["term"] = 0 },
                }.IsValid == expected
                && new PostgresVectorOptions { Schema = name }.IsValid == expected;
        });

    /// <summary>
    /// A name ending in a newline is refused. <c>$</c> in a .NET pattern matches at the end of the
    /// string <i>or before a trailing newline</i>, so the pattern this guard was written with —
    /// <c>^[A-Za-z0-9_]+$</c> — accepted <c>"lexisharp\n"</c>. It was not an injection, because the
    /// name is quoted before it reaches a statement and the quoting holds; it was a name the guard
    /// said it refused and did not, which is the failure that makes the next one possible.
    /// </summary>
    [Fact]
    public void TheGuardRejectsANameEndingInANewline()
    {
        AssertRejected(() => new PostgresTextSearchEngine(
            Unused,
            new PostgresIndexOptions { Schema = "lexisharp\n" }));

        AssertRejected(() => new PostgresTextSearchEngine(
            Unused,
            new PostgresIndexOptions { Table = "lexisharp\n" }));

        AssertRejected(() => new PostgresTextSearchEngine(
            Unused,
            new PostgresIndexOptions { TextSearchConfig = "simple\n" }));
    }
}
