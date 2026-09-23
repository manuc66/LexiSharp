using LexiSharp.Core;

namespace LexiSharp.Demo;

/// <summary>
/// A small, hand-written corpus about authentication, information retrieval and databases,
/// padded with off-topic documents. It is dense in vocabulary overlaps so that corpus-derived
/// semantic expansion has something to learn, while the distractors prove it does not fire
/// everywhere.
/// </summary>
public static class DemoCorpus
{
    /// <summary>Ready-to-use queries for the demo's suggestion chips.</summary>
    public static readonly IReadOnlyList<string> SampleQueries =
    [
        "refresh token",
        "short lived credential",
        "ranking quality",
        "session expiry",
        "embeddings",
        "inverted index",
    ];

    /// <summary>Builds the indexed documents (title and category as structured fields).</summary>
    public static IReadOnlyList<SearchDocument> Build() =>
    [
        Doc("auth-oauth-intro", "OAuth 2.0 overview", "auth",
            "OAuth 2.0 lets a client obtain an access token from an authorization server. "
            + "The token grants scoped access to protected resources without ever sharing the user password."),
        Doc("auth-refresh", "Refreshing an access token", "auth",
            "An access token is short lived. When it expires the client exchanges its refresh token for a new one, "
            + "so the user session continues without re-authenticating."),
        Doc("auth-session", "Session expiry and renewal", "auth",
            "A server session expires after a timeout. Before expiry the client silently renews it using stored "
            + "credentials, avoiding a logout for the active user."),
        Doc("auth-jwt", "JSON Web Tokens", "auth",
            "A JWT is a signed credential carrying claims. The signature lets a resource server verify the token "
            + "without a database lookup on every request."),
        Doc("auth-login", "Login and credential handling", "auth",
            "The login endpoint validates credentials against the user store, then issues a token pair. Storing "
            + "passwords requires a slow hash, never plaintext."),
        Doc("auth-revoke", "Revoking tokens", "auth",
            "Revocation invalidates an issued token before its natural expiry. A denylist keyed by token id lets "
            + "the authorization server reject a compromised credential."),

        Doc("ir-bm25", "BM25 ranking", "search",
            "BM25 scores a document by term frequency saturated with k1 and normalized by length with b. "
            + "It is the workhorse of lexical retrieval."),
        Doc("ir-inverted", "Inverted index", "search",
            "An inverted index maps each term to the documents and positions where it occurs, enabling fast "
            + "boolean and phrase matching."),
        Doc("ir-tokenizer", "Tokenization and stemming", "search",
            "A tokenizer splits text into terms, applies NFKD normalization and optionally removes stop words. "
            + "A stemmer reduces inflected words to a common root."),
        Doc("ir-hybrid", "Hybrid retrieval", "search",
            "Hybrid retrieval fuses a lexical ranking with a semantic ranking. Reciprocal rank fusion combines "
            + "the two orderings without calibrating their scores."),
        Doc("ir-rerank", "Two stage reranking", "search",
            "A cheap retriever produces a shortlist; an expensive reranker reorders it. The cascade trades "
            + "latency for precision at the top of the list."),
        Doc("ir-facets", "Faceted search", "search",
            "Facets count the values of a metadata field across the whole match set, letting an interface filter "
            + "results by category or tag."),
        Doc("ir-splade", "Learned sparse retrieval", "search",
            "SPLADE expands a document into weighted lexical terms learned by a model, keeping an inverted index "
            + "while adding semantic reach."),
        Doc("ir-eval", "Retrieval metrics", "search",
            "Precision at k, recall at k and NDCG measure ranking quality against labelled relevance. Without "
            + "them, tuning is guesswork."),

        Doc("db-postgres", "PostgreSQL full text search", "database",
            "PostgreSQL stores a tsvector for full text search and supports GIN indexes. It can rank with ts_rank "
            + "or combine with BM25 extensions."),
        Doc("db-pgvector", "Vector search with pgvector", "database",
            "The pgvector extension stores embeddings in a column and supports approximate nearest neighbour "
            + "search with an HNSW index."),
        Doc("db-sql-index", "SQL indexes", "database",
            "A B-tree index speeds up equality and range filters. A query planner decides whether an index scan "
            + "beats a sequential scan."),
        Doc("db-replication", "Database replication", "database",
            "Replication copies writes from a primary to replicas. Asynchronous replicas may lag, so reads can "
            + "observe stale rows."),
        Doc("db-transaction", "Transactions and isolation", "database",
            "A transaction groups statements into an atomic unit. Isolation levels trade consistency against "
            + "concurrency."),

        Doc("misc-weather", "Weather forecast", "misc",
            "The forecast calls for scattered showers in the morning and a sunny afternoon, with a light breeze "
            + "from the west."),
        Doc("misc-cooking", "Slow cooked stew", "misc",
            "Brown the meat, add onions and stock, then simmer for three hours. Season with salt and pepper "
            + "before serving."),
        Doc("misc-garden", "Spring garden chores", "misc",
            "Prune the roses, spread compost on the beds and water the tulips. Seed the herbs once the frost "
            + "has passed."),
        Doc("misc-cycling", "Commuting by bicycle", "misc",
            "Bicycles share the path with pedestrians. Signal before turning and keep a safe distance at rush hour."),
        Doc("misc-astronomy", "Backyard astronomy", "misc",
            "A small telescope reveals the craters of the moon and the rings of Saturn. Dark skies matter more "
            + "than aperture."),
        Doc("misc-music", "Tuning a guitar", "misc",
            "Tune the lowest string to the reference pitch, then match each successive string by ear or with a "
            + "tuner before the concert."),
        Doc("misc-photography", "Exposure basics", "misc",
            "Aperture, shutter speed and ISO form the exposure triangle. Open the aperture for a shallow depth "
            + "of field."),
    ];

    private static SearchDocument Doc(string id, string title, string category, string text) =>
        new(id, text, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["category"] = category,
        }, category);
}
