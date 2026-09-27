# Reference corpus

A small, committed corpus for exercising and diffing LexiSharp's ranking behaviour. It exists for
three jobs, all of which need the corpus to be **in the repository**:

1. a **golden master** — a committed baseline of what each stock configuration returns, so a
   change to the scoring or merging path shows up as a reviewable diff instead of a surprise;
2. **per-query analysis** — which queries a strategy rescued, and which it hurt, rather than an
   aggregate that hides both;
3. a **demonstration of the hard cases** — where lexical matching and any semantic signal
   disagree, and the disagreement is the interesting part.

It is deliberately **not** a slice of BEIR. See *Why not BEIR* below.

## Using it

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- benchmark bench/reference-corpus/corpus \
    --queries bench/reference-corpus/queries.json \
    --qrels   bench/reference-corpus/qrels.tsv \
    --configs bm25,bm25-semantic,hybrid --top-k 5
```

## Layout

```
manifest.json   families, what each is meant to expose, and the harness caveat below
corpus/         the documents, as markdown with YAML front matter (LexiSharp.Sources reads these)
  ordinary/     plain, well-matched documents
  hard/<family> the eight deliberately awkward families
queries.json    query id -> query text
qrels.tsv       qid <TAB> docid <TAB> grade, graded 1..3
```

Document ids are the path relative to `corpus/`, forward slashes, because that is what
`MarkdownLoader.LoadDirectory` produces. The golden master stores those ids verbatim, so **renaming
a file invalidates the baseline**.

Query ids are `<family>-<nn>`, and the prefix is load-bearing: the per-query analysis and the
manifest join on it. A query whose id does not carry its family silently disappears from the
breakdown it exists to appear in, so `ReferenceCorpusTests` enforces the convention.

## The eight hard families

| Family | What it is meant to expose |
|--------|---------------------------|
| `identifier` | an opaque literal (`MZ-77E2T0`) with near-identical neighbours; only exact matching can separate them |
| `synonym` | query and document share **no** vocabulary; a synonym edge or a corpus-derived association is the only bridge |
| `paraphrase` | the user asks about a symptom, the document is written about the mechanism |
| `rare-term` | a term in a single document, so its IDF is maximal — and the guard on the opposite failure, a wordy document outranking a precise hit |
| `multilingual` | the same fact in English, French and German |
| `long-document` | the answer sits in several hundred words of context, next to two near neighbours |
| `ambiguous` | one symptom, three documents, only one of which is the cause — **this is why the qrels are graded** |
| `metadata-conflict` | the body text and the front-matter fields disagree about the document's subject |

`ambiguous` is the reason the benchmark CLI had to learn graded qrels. Its three documents are all
topically relevant; a binary file would score them identically and could not tell a diagnosis from
a workaround.

## Read the harness caveat before trusting any "expected" result

`manifest.json` states, per family, which stock configuration *should* separate the case. Those
statements are about **`HashingEmbeddingProvider`**, which is feature hashing over tokens, not a
semantic model — it captures lexical overlap and nothing else.

So a note like *"dense misses the identifier"* is a claim about hashing, and is a **lower bound**
proxy for a real embedding model, not a stand-in for one. A real dense model may well separate
`MZ-77E2T0` from `MZ-77E2T1`, because it saw those tokens during training. Reading these
expectations as claims about semantic retrieval would be wrong, and
`ReferenceCorpusTests.TheManifestStatesThatTheDenseCapabilityIsNotSemantic` guards the wording.

Nothing here runs a model. Swapping a real provider in behind `IEmbeddingProvider` is a
configuration change; the corpus stays valid.

## Grades

`3` answers the question completely, `2` answers part of it, `1` mentions the subject without
answering. The scale is deliberately coarse — NFCorpus's three graded levels are the reference, not
a finer granularity. `nDCG` uses these with exponential gains; recall, MAP, MRR, precision and F1
use the binary projection (every judged document is relevant), so the two are **not** comparable
across a corpus change.

## Balance, and why it is not a target yet

`ordinary/` holds 26 of 42 documents. The principle is that ordinary documents must be the
**majority**, or IDF and average document length stop meaning anything and every metric becomes
noise — `ReferenceCorpusTests` enforces that floor. The 70% figure in the manifest is a
*starting point*, not a result: it should be reviewed after the first `verify` run against the
measured spread, not defended as a target.

The corpus is currently a **skeleton**: 42 documents, 22 queries, one or two probes per family. It
is enough to run, to validate, and to review the wiring. It is not yet a benchmark, and its metrics
should not be quoted anywhere until the golden master exists and the numbers have been looked at.

## Why not BEIR

`LexiSharp.Eval` already covers comparable published numbers on three real corpora
([its README](../LexiSharp.Eval/README.md)). That job is done, and it is done with the BEIR mirror
and its published md5s.

A downloaded corpus cannot serve the other two jobs:

- `verify` has to run **offline, forever**, on any machine, in CI. A corpus behind a URL stops
  asserting anything the day the URL moves or the network is unavailable.
- A **redistributable** artifact is a licence question. BEIR explicitly declines to grant one, and
  the terms are not uniform: SciFact's claims are CC BY 4.0 while its documents are S2ORC abstracts
  under ODC-By 1.0. Committing corpus text would make those terms this project's problem.

Hence: hand-built, small, and owned by this project.

## The golden master

`golden/rankings.txt` records what six configurations return for every query, at top-k 5, over the
corpus as committed. It is the artifact that makes a change to the scoring or merging path visible as
a reviewable diff instead of a surprise in production.

The config set is load-bearing, not decorative:

```
bm25                    the reference ranking
bm25-semantic           the « semantic lexical » expansion variant
bm25f                   field-weighted BM25F, at its default parameters
bm25+                   BM25+, at its default delta
bm25l                   BM25L, at its default delta
bm25-proximity-full     proximity damping, at its default strength and floor
```

Four of the six are there for one reason: an audit of the test suite found that the **default
parameters** of `Bm25FScorer`, `Bm25PlusScorer`, `Bm25LScorer` and `ProximityReranker` were pinned by
nothing at all. Structural bugs in those classes were caught immediately — nine out of nine mutations
failed the suite, including both BM25 transcription errors — but changing a *default* was silent,
because no unit test should pin a default (that would be a specification, and it would change every
published table at once). This file is the net for that: each config pushes a real default through a
real ranking, so a change shows up as a diff.

**What the net still does not catch, measured rather than assumed.** Re-running the audit against
this extended baseline: it catches `Bm25FScorer`'s default `k1` and `ProximityReranker`'s default
floor. It does **not** catch BM25L's or BM25+'s default `delta`, nor the proximity default strength —
changing each of those leaves this corpus's ranking identical. That is a limit of 42 documents and 22
queries, not of the tooling: a default that cannot change the ranking on the corpus available is
invisible to it. More mutation testing would not help, since a mutant with no observable effect is
correct to survive. Pinning those needs a corpus where they matter, not a better tool.

**The baseline is versioned against how the corpus is loaded.** The benchmark harness promotes the
front-matter `title` to a text field (`MarkdownLoadOptions.TextFieldNames = ["title"]`), so the
index holds the titles as searchable text — which plain BM25 benefits from substantially, nDCG@5
0.8359 → 0.8751. The committed baseline was re-recorded when that landed. If you change the loading
options, the right sequence is: run `verify` (it will fail, loudly, with a per-query diff), read the
diff to confirm the change is the one you meant, then re-record and read *that* diff before
committing it.

```bash
# check (exit 1 on drift, so it can gate a build)
dotnet run --project bench/LexiSharp.Cli -c Release -- verify corpus \
    --queries queries.json --qrels qrels.tsv \
    --configs bm25,bm25-semantic,bm25f,bm25+,bm25l,bm25-proximity-full --top-k 5 \
    --against golden/rankings.txt

# re-record - a separate command on purpose
dotnet run --project bench/LexiSharp.Cli -c Release -- baseline corpus \
    --queries queries.json --qrels qrels.tsv \
    --configs bm25,bm25-semantic,bm25f,bm25+,bm25l,bm25-proximity-full --top-k 5 \
    --out golden/rankings.txt
```

The config list must match on both sides: the baseline is keyed by each configuration's display
name, so verifying a subset silently checks less than you think.

`baseline` and `verify` are **separate subcommands**, not flags on one command, on purpose. A
`--update` flag would make re-recording a keystroke, and a baseline nobody re-reads stops catching
anything — it becomes a mirror of whatever the code currently does. Re-record deliberately, then
read the diff.

### What it stores, and what it deliberately does not

One line per query: the document ids in rank order and the six metrics to four decimals. No score
vectors, no per-term breakdowns, no timings. A 22-query corpus produces 132 lines across the six
configurations. That is
the point: a baseline nobody reads is not a baseline, and raw scores for every query would be
unreadable while still looking like evidence.

Metrics are stored because they are what a change moves *quietly*. Re-ordering two documents that
tie leaves every rank intact and still shifts nDCG.

Timings are not stored on purpose: they are machine-dependent, so pinning them would produce
constant false failures.

### It distinguishes a tie from a real change

Two documents with equal scores are ordered by corpus order, so swapping them is not a behaviour
change. The verifier checks whether a moved document ties with a neighbour, and reports:

| | meaning | exit |
|---|---|---|
| `Match` | identical ranking and metrics | 0 |
| `TieReordered` | same documents and metrics, swapped among equal scores | 0, reported as a tie |
| `Changed` | different membership, different order without a tie, or moved metrics | 1 |
| desync | a query in the run but not the baseline, or the reverse | 1 |

An unknown score is **not** treated as evidence of a tie — the absence of data is not evidence, and
guessing "probably a tie" is precisely what hides a regression.

### Build path is pinned

The baseline is only meaningful for a corpus built the same way. `CorpusBenchmark` indexes
everything in one `Index()` call, and the header records the document count; `verify` refuses to
compare a run over a different number of documents or at a different `top-k`, because every metric
depends on the depth. Renaming a document changes the ids and will show up as a change, which is
correct — the ids are the join key between corpus, qrels and baseline.

## Reading a comparison, query by query

A mean hides which queries moved. To see them:

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- diff corpus \
    --queries queries.json --qrels qrels.tsv \
    --baseline bm25 --candidate bm25-semantic --top-k 5
```

On the skeleton as committed, that reports `1 improved, 2 degraded, 19 unchanged (net -1)` — a mean
of −0.015 that decomposes into one query that gained 0.29 and **one that lost its only judged
document entirely** (`long-document-02`, "the judged document at rank 3 is gone"). The mean alone
showed neither.

That degradation is the `long-document` family doing its job: a several-hundred-word document is
the adversarial input for a corpus-learned term expander, because the associations it contributes
are mostly noise. The manifest predicted it, which is the first evidence that the family is testing
what it claims to.

## Adding to it

- Documents: markdown with `title` / `category` front matter; any other key becomes a field.
  The `title` is additionally promoted to an indexed text field, so a query can match it.
- Keep the id stable. It is the join key between the corpus, the qrels and the baseline.
- Add the query with its family prefix, and at least one judgment, or it is excluded from the
  averages and nobody notices.
- A family with no query is documentation nobody can check; `ReferenceCorpusTests` fails on one.

The checks that keep this from rotting live in
[`ReferenceCorpusTests`](../../tests/LexiSharp.Tests/ReferenceCorpusTests.cs): dangling qrels,
unjudged queries, ids that do not name their family, grades outside the scale, duplicate
judgments, a manifest family with no query, and the ordinary-majority floor.
