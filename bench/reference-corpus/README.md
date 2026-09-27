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

## Adding to it

- Documents: markdown with `title` / `category` front matter; any other key becomes a field.
- Keep the id stable. It is the join key between the corpus, the qrels and the baseline.
- Add the query with its family prefix, and at least one judgment, or it is excluded from the
  averages and nobody notices.
- A family with no query is documentation nobody can check; `ReferenceCorpusTests` fails on one.

The checks that keep this from rotting live in
[`ReferenceCorpusTests`](../../tests/LexiSharp.Tests/ReferenceCorpusTests.cs): dangling qrels,
unjudged queries, ids that do not name their family, grades outside the scale, duplicate
judgments, a manifest family with no query, and the ordinary-majority floor.
