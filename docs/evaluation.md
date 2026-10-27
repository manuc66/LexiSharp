---
title: Retrieval quality and IR metrics
nav_order: 11
description: >-
  Retrieval quality on the BEIR corpora, the metrics behind it (nDCG, MRR, MAP,
  recall), the benchmark CLI, per-query diffs and BM25 parameter tuning.
---

# Retrieval quality and IR metrics

A ranking you cannot score is a ranking you are guessing about. This page is the evidence
first, then the instruments: standard IR metrics, a parameter tuner, a benchmark CLI over
your own corpus, and a per-query diff that says *which* queries a change helped.

Everything quoted here is traceable to a command you can re-run. Where something is **not**
measured, it says so rather than being quietly left out.

## Measured against published baselines

`bench/LexiSharp.Eval` runs the engines over three public BEIR corpora — NFCorpus, SciFact
and ArguAna — md5-verified on download, and reports nDCG@10 / MAP@10 / MRR@10 / R@10 next
to the BM25 numbers published in the BEIR paper (Thakur et al. 2021, Table 2). Reproduce
it with `dotnet run --project bench/LexiSharp.Eval`; the full per-dataset tables are in
[its README](https://github.com/manuc66/LexiSharp/blob/main/bench/LexiSharp.Eval/README.md),
and it grants no licence over the datasets it downloads.

**Two configurations, and what each one answers.** Both are this library's BM25 over the same
three corpora, scored on the same runs. They differ in analysis and parameters, and the
published figures were produced with the second — which is why the first is not a like-for-like
comparison. nDCG@10:

| Corpus | Analysis and parameters | LexiSharp | published reference (2021) | Δ |
|---|---|---:|---:|---:|
| NFCorpus | defaults: `Tokenizer.Default`, k1=1.5, b=0.75 | 0.308 | 0.325 | −5.2 % |
| SciFact | defaults | 0.662 | 0.665 | −0.5 % |
| ArguAna | defaults | 0.320 | 0.315 | +1.6 % |
| NFCorpus | aligned: uax29 analysis, k1=0.9, b=0.4, reference conventions | 0.3218 | 0.3218 | 0.0000 |
| SciFact | aligned: same conventions | 0.6789 | 0.6789 | 0.0000 |
| ArguAna | aligned | 0.4005 | 0.3970 at k1=0.9, b=0.4 | −0.0035 |

The **defaults** rows answer *what the library does out of the box*: no stemming, stop words
kept. The **aligned** rows answer *is this comparable to the published figure* — and with the
reference's own analyzer, its parameters, its gain convention, its repeated-term rule, its index
statistics, its score rounding, its omitted saturation constant and its single precision, the two
agree to the fourth decimal on NFCorpus and SciFact. On ArguAna this library reads **0.4005** against
the published **0.3970** — 0.0035 short under the same conventions — and that gap is not reproduced by
any configuration tried. The gap between the two row groups is configuration, not ranking; the
ArguAna gap is not yet explained.

**What had to be matched, and how it was found.** The first version of these rows used the `english`
analyzer and read 0.3215 / 0.6788 against 0.3218 / 0.6789 — close enough to be called equal, and it
was not: `english` splits a contraction into a stem and a fragment (`don't` becomes two tokens) where
the reference keeps one, so its index held 2.8 % / 1.5 % more terms and its scores were not built on
the reference's document frequencies. The `uax29` analyzer — the published separator rules, an
apostrophe kept inside a word, possessives stripped before the stop list — brings the index to
the reference's published term count on NFCorpus and SciFact. Adding the four arithmetic options
(reference index statistics, four-decimal score rounding, omitted saturation constant, single
precision) brings both figures to the published ones at four decimals: this library reads
**0.32175501** and **0.67890943** unrounded, against a reference published as 0.3218 and 0.6789.
The published figures are rounded to four decimals, so the score comparison cannot see beyond that;
what can be seen, and is exact, is the index — see below. Bit-level score parity — the only claim
that would settle the ranking — exists on ArguAna, where 15,466 of 15,466 compared scores match, and
is not established on these two. Both fingerprints and both scores are pinned
(`nfcorpus/uax29`, `scifact/uax29`, `nfcorpus/uax29-aligned`, `scifact/uax29-aligned`), so
`--verify-reference` replays them.

**The last term gap was two tokenizer rules, and it is closed.** Running Lucene 10.5.2's
`EnglishAnalyzer` over the same corpus files reproduces the published counts exactly — 637 485,
838 128 and 969 528 — which identifies the reference's analyzer version and proves the target was
reachable. Comparing that run with this library's index term by term gave the whole difference:
Lucene 10 emits `©`, `®`, `™` and `↔` as terms where Lucene 4.8 dropped them, and joins across the
middle dot (`mg·dl` is one token) where this library split it. On NFCorpus that was 519 symbol
tokens minus 16 middle-dot splits (503); on SciFact 44 minus 1 (43); on ArguAna nothing. The `uax29`
analyzer now carries both rules — `TokenizerOptions.SymbolTerms` and `U+00B7` as a letter-to-letter
joiner — and its index holds **exactly** the published term count on all three corpora, pinned at
zero tolerance. Term for term, not only in total: diffing this library's token multiset against
Lucene 10.5.2's over these files leaves **zero** tokens on either side for NFCorpus and SciFact, and
ArguAna contains none of the characters the two rules touch. The indexes are the same index.

**ArguAna: the metric is reproduced, and so are the raw scores.** The two are verified by different
means and the distinction is the point.

The aggregate is verified without this repository's own metric code being involved. `trec_eval` 9.0.8,
run on a `trec_run` this harness writes at that configuration, reads nDCG@10 **0.3970** and recall@100
**0.9324** — the two published figures, to four decimals. This harness reads 0.3970 on the same run.
The metrics are computed differently and agree; where they do not, the difference is 0.0004, confined
to the 102 of 1,406 queries whose returned scores contain a tie. A tie is the plausible mechanism,
being the only property those 102 queries share, and it is not established: neither the order written
in the run file, nor document id, nor a reversal of the tied group reproduces `trec_eval`'s reading.
Which order produced the published figure is not established either.

One thing is worth knowing about reading a metric from this harness, because it changed a number
that looked like a ranking result. The harness measures at **rank 10**, not at the depth the run
retrieved, and the two being the same value is a coincidence of the default: a bit-for-bit
reproduction run retrieves one document deeper than it measures, so measuring at the retrieval depth
reported nDCG@11 under a heading reading nDCG@10. Retrieval depth and evaluation depth are different
things, and the cutoff is named (`MetricDepth`) and independent of `--top-k`. The table below
holds the full set of figures the published config produces, at the cutoff:

| | LexiSharp | reference | Δ |
|---|---:|---:|---:|
| nDCG@10 | 0.3970 | 0.3970 | 0.0000 |
| nDCG@5 | 0.3445 | 0.3445 | 0.0000 |
| recall@100 | 0.9324 | 0.9324 | 0.0000 |
| recall@1000 | 0.9872 | 0.9872 | 0.0000 |
| MAP@100 | 0.3280 | 0.3280 | 0.0000 |
| reciprocal rank | 0.3282 | 0.3282 | 0.0000 |

**What is reproduced is the scores, and on the raw bits.** A metric sees an order, and an order can
agree while every number behind it differs — which is exactly what was happening. Against a run produced
by the reference implementation's own searcher over its own index, for all 1,406 test queries:

| | paired scores identical on the raw bits |
|---|---:|
| index built over every document (as it was) | 0 / 14,168 |
| inverse document frequency over the documents carrying the field | 876 / 14,168 — 6.18 % |
| … and the reference's scale, precision and rounding | **14,168 / 14,168 — 100.00 %** |

At the depth the comparison runs, one document deeper than the metric is measured at, the count is
**15,466 / 15,466** over all 1,406 queries.

The three differences that got there, each behind an option whose default is unchanged:

| difference | option | default | what it is |
|---|---|---|---|
| idf over 8,674 documents instead of 8,673 | `ITextIndex.StatisticDocumentCount` | every document | the corpus holds one document with no term; worth 2.6e-05 on every score |
| no `k1 + 1` in the numerator | `Bm25Scorer(saturationConstant: false)` | `true` | the reference's BM25 has no saturation constant; same ranking, factor of 1.9 off |
| single precision, reciprocal table, total rounded once | `Bm25Scorer(arithmetic: SinglePrecision)` | `Double` | same formula, different arithmetic; worth about one part in 10⁷ |
| scores rounded to four decimals, ties walked down | `SearchOptions.ScoreRounding` | `None` | what the reference does to a score before writing it down; worth up to 5e-06 |

The last of the four is the one a reader is most likely to think is a fudge, so it is worth being plain
about: it is a post-processing step, not scoring, and it is lossy — it merges scores that differ by less
than half a ten-thousandth and then separates them again by rank, so the same document reports two
different scores at two different page sizes. It is here because a run file contains those numbers and
not the scorer's, and reproducing the arithmetic without reproducing what is written down reproduces
nothing anybody can check.

That measurement was nDCG@10 identical to the last digit on **all 1,406 queries, one by one**: paired
difference 0.00000000, standard deviation 0.00000000, 95 % interval [0.00000000, 0.00000000]. The two
indexes agree **term for term**: 23,895 distinct terms and 969,528 occurrences on each side, zero terms differing
in document frequency, zero in term frequency, and zero terms present in one index and not the other.

The comparison runs against the reference's own index, read out of it rather than re-derived from an
assumption about its analyzer, and against its own evaluation binary.

**What the 0.033 was.** Not a parameter choice and not the corpus — four rules, each measured by asking
the reference implementation rather than by reasoning about it, and each behind an option whose default
is unchanged:

| rule | option | default | measured effect |
|---|---|---|---|
| phrase syntax on prose | `SearchOptions.ParseQuerySyntax` | `true` | 138 of 1,406 queries returned 0–1 results at any depth |
| repeated query terms | `QueryTermWeighting.QueryFrequency` | `Distinct` | +0.074 |
| word boundaries | `TokenizerOptions.WordSegmentation` | `Flat` | 18 separators × digit/letter on both sides |
| possessive, before the stop list | `TokenizerOptions.StripPossessives` | `false` | `it` 3,501 → 3,134; `that` and `there` removed |
| diacritics kept | `TokenizerOptions.FoldDiacritics` | `true` | ±0.003 |
| U+0130 folded | `TokenizerOptions.FoldTurkishDottedI` | `false` | one term in 23,895 |
| one-byte lengths | `InMemoryTextIndex.DocumentLengthQuantization` | `Exact` | −0.006, kept as a reproduction device |
| idf over the documents carrying the field | `ITextIndex.StatisticDocumentCount` | every document | 2.6e-05 on every score, no effect on any ranking |

Three findings here were counter-intuitive enough to be worth stating, because each is the kind of
thing that gets "fixed" the wrong way:

- **The possessive rule has to run before the stop word list, not inside the stemmer.** Trimming inside
  the stemmer is too late: the list has been tested on `it's` rather than on `it`, so `it's` survives as
  a stop word and only then becomes `it`. On this corpus that was an `it` 3,501 times against the
  reference's 3,134, plus a `that` (83) and a `there` (36) the reference has not got at all — 486 of
  the 487 occurrences by which the two indexes differed. The reference's `it` comes from `its`, which
  is not a possessive and is not on the list.
- **An unconditional joiner belongs to the token even at its start.** `_630888` indexes whole and
  `__alpha` as `__alpha`, while `-alpha` is just `alpha` — so it is the underscore's rule, not a rule
  about any separator, and a run of underscores alone is not a token.
- **A combining mark ended the word it followed.** `celi\u0307l` split into `celi` and `l` into two
  tokens. A mark modifies the character it follows, so it extends a word and cannot open one — measured
  across all four positions: `a\u0307b` and `a\u0307` are each one token, `\u0307ab` is just `ab`, a lone
  mark is nothing. ArguAna holds no combining mark, so it moved no number here; NFCorpus holds four
  characters out of 5,779,318.
- **The stemmer refuses every term containing a non-ASCII character.** The algorithm is defined
  over characters, not scripts: everything outside `aeiou` is a consonant. Asking the reference's own
  stemmer settled it — `they’re` → `they’r`, `naïve` → `naïv`, `façade` → `façad` — and its answers are
  this library's. That same stemmer does **not** remove possessives: asked directly, it answers
  `adam'`, which makes the possessive removal a separate stage.

**No difference remains.** Of the 505 code points whose lowercase differs from themselves over the
ranges a European corpus contains, U+0130 is the *only* one where .NET's invariant casing and the
reference disagree — so it is a single character, not a difference of strategy. And the fold is the
Unicode **simple** mapping, U+0069, rather than the full one that appends U+0307: their index holds a
five-character `celil`, and handing their analyzer a six-character `celi\u0307l` returns the dot intact,
which means their pipeline never produced one. Folding to the full mapping, on the reading that "fold it
to i" wants the complete Unicode answer, is a different function and does not reproduce it.

Leaving the character alone is still defensible — it is the right answer for a Turkish index, where `İ`
and `i` are different letters and folding them merges two words — which is why it is an option and why the
default is unchanged.

Both constructions were measured here rather than reasoned about: the multi-field run scores the title
and the body as separate fields with their own document frequencies and their own length, then sums
the two scores, which is what a multi-field Lucene query does. It gives nDCG@10 0.370 and R@100
0.802 — better than flat on nDCG@10 and worse on recall, and short of 0.414 either way.

The figures read out of the leaderboard paper are its Table 1 row for ArguAna, whose header names the
two metric groups. The same paper contains a second table with a different column arrangement whose
ArguAna row does not reconcile with the first, which is why the citation is to the row and its header
rather than to a bare number.

**Why a small difference can be worth this much.** The gold document is outside the top 10 for
**39.9 %** of these queries, and the score gap between rank 10 and rank 11 has a tenth percentile of
**0.118 %** — a fifth of the queries are decided by less than 0.046 %. The 0.033 is therefore about 165
documents promoted from outside the top 10 into it, which is a difference an aggregate score does not
surface: the mean moves by the size of the gap, not by how many queries it decided.

**The withdrawn ArguAna figure.** **0.4061** was reported at k1=3.0/b=0.75, above the published
0.3970, and explained as a scoring rule: the library deduplicated query terms, the reference scores one
clause per query-token occurrence, and on a corpus whose every test query is a whole ~200-word
argument, counting them was said to be worth **+0.0705** (0.2197 to 0.2902 at k1=0.9/b=0.4). It is
withdrawn, and no published release ever carried it — see the
[changelog](../CHANGELOG.md#withdrawn) for the provenance.

Re-measured on 1,406 queries with the English analysis, at the reference's own k1=0.9/b=0.4:

| setting | nDCG@10 |
|---|---:|
| `QueryTermWeighting.Distinct` (the default) | 0.240 |
| `QueryTermWeighting.QueryFrequency` | 0.301 |
| **effect of the setting** | **+0.061** |

Three conventions were checked, and the third one is the defect. The linear gain convention returns
the same 0.301 as the exponential one. Excluding the query document is measured separately, at the
pin's own configuration (English analysis, k1=1.5/b=0.75, linear): **0.313 without, 0.4180 with, i.e.
+0.105** — the pin value is recorded with the flag on. An earlier reading of +0.0012 measured the
option doing nothing (the exclusion was not applied on the term-at-a-time path); the fix is what made
it reach its document. Neither 0.2902 nor 0.4061 is reproducible, and the 0.301 above is measured
without the exclusion.

The reason the withdrawn figures could not have come from this code is structural. The harness flag fed
exactly one thing — the `QueryTermWeighting` argument of `new Bm25Scorer(...)` — and that scorer went
into `RankedTextSearchEngine`, which deduplicated the query before the scorer could see a repetition. The
two settings could not have produced different rankings. The engine hands the scorer the raw term
list, the setting is observable through `Search`, and `QueryTermWeightingTests` fails if it ever stops
being.

**ArguAna is 0.0035 short, and that is the measured state.** Under the reference's analyzer,
parameters, gain convention, repeated-term rule, exclusion and arithmetic, this library reads
**0.4005** where the reference publishes **0.3970**. A parity row used to carry 0.3970 as this
library's own figure; it was the published number, not a measurement — the parity section is
displayed by `--verify-reference` but never replayed, so nothing caught the difference. That row is
withdrawn, the value is now a pinned regression (`arguana/uax29-aligned`), and the raw-score claim
that went with it is withdrawn too. The cause is not established: the index is term for term the
reference's, so what remains is the scorer's arithmetic, and no configuration tried here closes it.

The reference is also **newer than the table it is first compared against**: a current per-corpus
regression for this exact index puts ArguAna at 0.3970, not the 0.315 of the 2021 paper.

Reproduce the alignment, and check it against pinned values:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --analyzer uax29 --ndcg-gain linear --reference-bm25 0.9,0.4 --query-term-frequency \
  --reference-index-statistics --reference-score-rounding --omit-saturation-constant \
  --single-precision-bm25
dotnet run --project bench/LexiSharp.Eval -c Release -- --verify-reference
```

See [the harness README](../bench/LexiSharp.Eval/README.md) for what each flag changes and
`reference/pinned.json` for the measured values.

**NFCorpus, the one corpus where the whole stack is measured** (nDCG@10 / MAP@10 / MRR@10 /
R@10, 323 judged queries, default tokenizer, no stemming):

| Config | nDCG@10 | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|---:|
| BM25 (k1=1.5, b=0.75) | 0.308 | 0.222 | 0.516 | 0.146 |
| BM25 (tuned, in-sample) | 0.311 | — | — | — |
| TF-IDF | 0.248 | 0.171 | 0.399 | 0.128 |
| Query likelihood (λ=0.2) | 0.288 | 0.205 | 0.485 | 0.132 |
| BM25F (unweighted / title 2.0) | 0.296 | 0.211 | 0.484 | 0.141 |
| Dense (multilingual-e5-small) | 0.304 | 0.216 | 0.502 | 0.144 |
| Hybrid BM25+dense (weighted) | 0.323 | 0.230 | 0.534 | 0.156 |
| Hybrid BM25+dense (RRF) | 0.333 | 0.239 | 0.550 | 0.160 |
| BM25 → cross-encoder rerank | 0.337 | 0.246 | 0.558 | 0.153 |
| **Hybrid RRF → cross-encoder rerank** | **0.346** | **0.250** | **0.573** | 0.158 |
| BEIR's published BM25 | 0.325 | — | — | — |

What that table says:

- the **dense retriever alone** sits at BM25 level (0.304 vs 0.308) on a model it does not
  ship — the harness supplies `multilingual-e5-small` over ONNX;
- **fusing beats both parents** (0.323–0.333 against 0.308 and 0.304), and **on this corpus** the
  **cross-encoder improves every first stage** it is stacked on: **0.346** for BM25+dense RRF
  plus reranking, about +2.1 points over BEIR's published BM25. The other two corpora are the
  counter-examples: SciFact's reranker helps one of three first stages and costs the best fusion
  0.022, and on ArguAna it loses to all three;
- **BM25F sits below BM25 here and the title weight moves nothing** — a negative result, and
  the reason is default parameters rather than the technique: tuned against tuned, the two
  are within 0.002 of each other on this corpus (0.309 against 0.311) and the fitted title
  weight is 1.0. See
  [BM25F](ranking.md#bm25f-field-weighted-bm25) for that correction in full.

What it does **not** say:

- the dense and cross-encoder rows are the only ones that use a real model, and both belong
  to the harness, not to the library. The **dense and fused rows exist on all three corpora**
  (SciFact: 0.655 alone, 0.681/0.686 fused; ArguAna: 0.313 alone, 0.349/0.350 fused), so the
  hybrid payoff is measured three times rather than once. The **cross-encoder** is measured on all
  three, and it pays on one: NFCorpus improves every first stage, SciFact helps query likelihood
  (0.622 → 0.649) and costs BM25 and the best fusion, and on ArguAna it **loses to all three**
  (0.302, 0.301, 0.339 against 0.320, 0.302, 0.350). Short queries with a few judged documents are
  where it pays; 196-word argument queries are where it does not. Each reranked row re-scores the
  **top 100** candidates of its first stage with one fixed pretrained model, not a model fitted on
  these queries, so unlike a tuned row it is **not an in-sample oracle**. Reproduce the three with
  `--dataset arguana --no-tuned --dense --rerank --rerank-top 100 --jobs 4`; the pair cache under
  `data/rerank-cache/` (14 MB for ArguAna) lets the second and third rows reuse the first's
  candidate passes. Re-running from that cache computes only the pairs the cached run had not
  flushed, and the BM25 row reads nDCG@10 **0.303** and R@10 **0.616** against the 0.302 of the
  sentence above (R@10 0.615 in the harness table), because one query crosses the rank-10
  boundary; the ordering is unchanged;
- stemming is not settled either: `--stem porter` takes NFCorpus 0.308 → 0.322 and SciFact
  0.662 → 0.687, and is mixed on ArguAna 0.320 → 0.308 (BM25) / 0.302 → 0.312 (QL). That is why it stays opt-in — see
  [Tokenizer customization](indexing.md#tokenizer-customization). What that comparison leaves out is
  the stop word list, which is the larger half of the analysis: `--analyzer english` (Porter plus
  the conventional 33 words) takes SciFact to 0.692 and NFCorpus to 0.327;
- ArguAna's BM25F row has a tuned counterpart: **0.397** at k1=1.5, b=1, with the title weight left
  at **1.0** — `WeightingHelped = false`, so the margin is not the weighting. A tuned **BM25** reads
  0.363 there, so BM25F does beat one on this corpus; on the reference corpus it trails one by 0.011.
  Both are in-sample oracles, and the sign of the comparison is corpus-dependent.

## The SQL backends, on the same corpora

The PostgreSQL engines that rank on their own are measured by the same harness, over the same test
queries and qrels, through the same metric code at the same depth:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --backend paradedb --backend-connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp"
```

`--backend lexical` measures `PostgresTextSearchEngine` (`tsvector` + `ts_rank_cd`);
`--backend paradedb` measures `ParadeDBTextSearchEngine` (`pg_search` + `pdb.score`, Tantivy's BM25);
`--backend vector` measures `PostgresVectorSearchEngine` over the cached dense embeddings, with one
row per `ef_search`; `--backend fuzzy` measures `PostgresFuzzySearchEngine`, with one row per
`pg_trgm` similarity threshold; `--backend sparse` measures `PostgresSparseSearchEngine` over the
`--sparse` vectors, with one row for the engine's HNSW index and one for the same table after the
index is dropped. The lane builds a table named `lexisharp_eval_<dataset>` and drops it when the
run ends.

nDCG@10, harness defaults (exponential gains — the convention of the **defaults** rows above). The
first table is the comparison worth having: ParadeDB's k1 and b are index-time tokenizer arguments in
`pg_search`, so `--backend-bm25 0.9,0.4` builds the index at the operating point the published
baselines use, and the LexiSharp column is a run of this harness at the same parameters (Δ is
ParadeDB minus LexiSharp):

| Corpus | LexiSharp BM25 (0.9, 0.4) | ParadeDB `pdb.score` (0.9, 0.4) | Δ |
|---|---:|---:|---:|
| NFCorpus | 0.305 | 0.3055 | +0.0005 |
| SciFact | 0.655 | 0.6633 | +0.0083 |

Out of the box, each engine at its own defaults (`k1=1.5, b=0.75` here, `1.2, 0.75` for Tantivy):

| Corpus | LexiSharp BM25 | ParadeDB `pdb.score` | PostgreSQL `ts_rank_cd` (`simple`) | `ts_rank_cd` (`english`) |
|---|---:|---:|---:|---:|
| NFCorpus | 0.308 | 0.3084 | 0.1864 | 0.2043 |
| SciFact | 0.662 | 0.6619 | 0.0188 | not measured |
| ArguAna | 0.320 | not measured | not measured | not measured |

- **ParadeDB is a BM25 and reads where this library's BM25 reads.** At matched parameters the two
  differ by 0.0005 on NFCorpus and 0.0083 on SciFact, which is analysis rather than ranking: the
  index is built on `pdb.simple` with ASCII folding, the title is concatenated into the indexed
  content because the `pg_search` index covers `content` alone, and the matcher is a disjunction
  (`|||`) rather than this library's scored term loop. It is not a reproduction, and it is not
  bit-identical.
- **`ts_rank_cd` is not BM25** — it is coverage-based and takes no k1/b, so `--backend-bm25` is
  refused with it rather than silently ignored. Most of its distance from the BM25 column is the
  matching rule rather than the ranking: `websearch_to_tsquery` ANDs every term, so a match is a
  document containing all of them. NFCorpus's test queries average 3.4 words and read 0.1864;
  SciFact's average 13.1 and read 0.0188. PostgreSQL's own English analysis
  (`--backend-config english`) moves NFCorpus 0.1864 → 0.2043, not closer. Weighting the title with
  `--backend-field-weights title=A` leaves both rows essentially unchanged — NFCorpus 0.1869,
  SciFact 0.0188 — and [the backend guide](backends.md#postgresql-backend-lexisharpostgres) carries
  the statement, the rows at `A` and `B`, and what `ts_rank_cd` cannot express per field.
- **ArguAna has no row.** Both text engines refuse query text their matcher reads as a prefix
  operator, and 2 of the 1,406 test queries carry one; the vector engine refuses the 148 whose text
  it reads as a phrase, because an embedding has no phrase semantics to honour. The run prints
  that refusal and its count instead of a metric over a reduced query set, which would be a
  different query set from every row beside it.
- **These rows are not pinned.** `reference/pinned.json` keys on the analyzer and the BM25
  parameters, and the pinned job runs without a PostgreSQL service, so a pin there could only ever
  skip — a gate that cannot fail. They are reproducible from the command above, and nothing catches
  a drift in them.
- **Scores are PostgreSQL-native and not numerically comparable** with `Bm25Scorer`; the metric
  columns are. The analysis is PostgreSQL's own, not this library's tokenizer, which is why these
  rows are not comparable with the published figures or with the aligned rows either — the
  parameter-matched table above compares two analyses, not two implementations of one.

The vector engine is measured the same way, over the harness's cached embeddings, with one row per
`ef_search` — `--backend vector --backend-ef 10,40,160`, which needs `--dense`:

| corpus, ef_search | nDCG@10 | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|---:|
| NFCorpus 10 | 0.2882 | 0.2077 | 0.4589 | 0.1301 |
| NFCorpus 40 | 0.2960 | 0.2127 | 0.4811 | 0.1388 |
| NFCorpus 160 | 0.3013 | 0.2151 | 0.4928 | 0.1426 |
| NFCorpus exact, in process | 0.304 | 0.216 | 0.502 | 0.144 |
| SciFact 10 | 0.5998 | 0.5552 | 0.5670 | 0.7244 |
| SciFact 40 | 0.6528 | 0.6053 | 0.6179 | 0.7861 |
| SciFact 160 | 0.6550 | 0.6062 | 0.6188 | 0.7928 |
| SciFact exact, in process | 0.655 | 0.606 | 0.619 | 0.793 |

The in-memory dense row ranks the same vectors exactly, so the distance between the two is what the
index costs in metric points: **NFCorpus 0.0158 / 0.008 / 0.0027** and **SciFact 0.055 / 0.002 /
0.0000** at `ef_search` 10 / 40 / 160 — recall translated into nDCG rather than left as a ratio. The
residual at NFCorpus's 160 is not attributed: it is the same cosine computed by pgvector in single
precision and in process, and on that corpus the planner may already answer with an exact scan at
that value ([backends](backends.md)). ArguAna has no curve: the vector engine refuses 148 of its
1,406 test queries as phrases, and a metric over the rest would be a different query set.

The fuzzy engine is measured the same way, over `pg_trgm` trigram similarity in `Similarity` mode:
the threshold is the `%` operator's match rule (`set_limit`), and every document that clears it is
ranked by `similarity(content, query)`. `--backend fuzzy --backend-threshold
0.3,0.1,0.05,0.01,0.005,0.001,0.0001` gives one row per threshold over one index:

```bash
POSTGRES_TEST_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp"
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --backend fuzzy --backend-connection "$POSTGRES_TEST_CONNECTION" \
  --backend-threshold 0.3,0.1,0.05,0.01,0.005,0.001,0.0001
```

Measured on one host in one session against a fresh `pgvector/pgvector:pg16` container (the engine
installs `pg_trgm` itself), harness defaults (exponential gains), 10 documents retrieved and measured
at 10, both trigram indexes built, and the BEIR title concatenated into the content column the engine
compares. nDCG@10:

| Corpus (queries) | 0.3 | 0.1 | 0.05 | 0.01 | 0.005 | 0.001 | 0.0001 |
|---|---:|---:|---:|---:|---:|---:|---:|
| NFCorpus (323) | 0.0000 | 0.0034 | 0.0251 | 0.0961 | 0.0981 | 0.0981 | 0.0981 |
| SciFact (300) | 0.0033 | 0.1908 | 0.2186 | 0.2186 | 0.2186 | 0.2186 | 0.2186 |

MAP@10, MRR@10 and R@10 at the endpoints of the sweep, so a reader can see the three metrics move
together:

| Corpus, threshold | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|
| NFCorpus 0.3 | 0.0000 | 0.0000 | 0.0000 |
| NFCorpus 0.005 (saturated) | 0.0545 | 0.1809 | 0.0594 |
| SciFact 0.3 | 0.0033 | 0.0033 | 0.0033 |
| SciFact 0.05 (saturated) | 0.1885 | 0.1925 | 0.3104 |

- **A threshold on trigrams is not a relevance ranking, and the rows say how far off it is.** At the
  engine's default 0.3 the lane scores 0.0000 on NFCorpus and 0.0033 on SciFact. Similarity divides
  the shared character trigrams by the sum of both trigram sets, and a short query against a whole
  document puts thousands of trigrams in the denominator, so a relevant document scores near zero
  and is filtered out before ranking. Trigram similarity is not a ranking of prose, and the rows say
  how far off it is; the threshold is the parameter that decides whether a row has anything to rank
  at all.
- **Lowering the threshold to where the rows stop moving raises the ceiling to 0.0981 and 0.2186.**
  From 0.005 down on NFCorpus, and from 0.05 down on SciFact, the metrics are identical to four
  decimals: below that point the threshold no longer changes the top 10 the metric reads, so the row
  measures the trigram ranking rather than the gate. Those are still far below BM25 on the same
  corpus (NFCorpus 0.308, SciFact 0.662) — the ranking itself, not just the default threshold, is the
  limit.
- **The sweep is in-sample and its best row is an upper bound.** Every threshold was scored on the
  qrels it is reported against, so the best row is not a tuned configuration and is not comparable
  with a tuned row. The grid is seven points and all of them are printed, which is what keeps the
  table from being the best of an unstated sweep; a corpus where the low end had not saturated would
  need a wider grid to say where its ceiling is.
- **Not measured:** `Nearest` mode (GiST kNN ordering without a threshold), `UseLevenshteinRefinement`,
  `IncludePhonetic`, and `%>`/`word_similarity`, which matches a query against the best-matching part
  of a document rather than the whole of it. The rows above are thresholded whole-content similarity
  only, which is the mode the engine's default threshold gates. ArguAna has no row: the lane is
  measured on NFCorpus and SciFact only.

The sparse engine is measured the same way, over the vectors the in-memory sparse row reads.
`--backend sparse` is refused without `--sparse <dir>`, and with both flags one command prints the
in-memory row and two SQL rows over one table:

```bash
docker run -d --name lexisharp-pg-sparsevec -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=lexisharp pgvector/pgvector:pg16

POSTGRES_TEST_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp"
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned \
  --backend sparse --backend-connection "$POSTGRES_TEST_CONNECTION" \
  --sparse bench/LexiSharp.Eval/data/sparse-vectors --exclude-query-doc
```

The lane indexes the corpus into `PostgresSparseSearchEngine` — a `sparsevec` column of 23 099
coordinates, one per term the vector set carries, with an HNSW index at `m = 16` and
`ef_construction = 64` (pgvector's defaults) — in a table named `lexisharp_eval_arguana`, which is
dropped when the run ends. It then scores the corpus twice: with that index in place, and after
`DROP INDEX`, which leaves the same column answered by an exact scan. Measured on 2026-10-26 on one
host against a fresh container, 1 406 test queries, harness defaults (exponential gains),
`--exclude-query-doc`:

| ArguAna, `--exclude-query-doc` | nDCG@10 | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|---:|
| SPLADE-v3, in memory (`--sparse`, same run) | **0.5216** | 0.4300 | 0.4300 | 0.8179 |
| `sparsevec` + HNSW, `hnsw.ef_search` 40 | 0.5200 | 0.4272 | 0.4272 | 0.8201 |
| `sparsevec`, exact scan (index dropped) | 0.5202 | 0.4280 | 0.4280 | 0.8179 |

- **The index costs 0.0002 nDCG@10 here.** 0.5200 with the graph against 0.5202 without it is the
  whole of what the HNSW candidate list of 40 costs on this corpus; the 0.0014 between the exact
  row and the in-memory row is the same vectors summed by a different code path, and this run does
  not apportion it between pgvector's arithmetic and the order of a page.
- **The plan says which path answered, and the lane prints it.** `EXPLAIN` over the engine's own
  search shape returns `Index Scan using lexisharp_eval_arguana_sparse_hnsw on
  lexisharp_eval_arguana`, and the row's label is read from that plan, so a table cannot say "hnsw"
  about a sequential scan. The second row is the same table after `DROP INDEX`: "exact" is a fact
  about the table, not an inference from the first row.
- **`hnsw.ef_search` is one point, at the server's default.** The engine exposes no option that
  reaches it, so the row runs at pgvector's default 40 — and `--backend-ef` is refused with this
  backend rather than accepted and ignored. What a larger candidate list would buy is not measured;
  the vector lane's curve is not a substitute, because that index is dense and cosine.
- **The query set is the in-memory row's, all 1 406 queries.** The two text rows refuse 2 of
  ArguAna's queries as prefix operators and the vector row refuses 148 as phrases; this engine is
  called with query syntax off and hands the text to the encoder, which hashed it verbatim, so it
  scores every query. A query whose text had no vector is refused by the lane with its id rather
  than skipped.
- **R@10 moves the other way, and the reason is not measured.** The exact row reads 0.8179 and the
  indexed one 0.8201, so exactness is not an upper bound on R@10 here: the metric reads a page, the
  SQL `ORDER BY` has no tie-break where the in-memory engine breaks ties by document id, and which
  of several equally-scored documents fills rank 10 is then the executor's choice. That is a
  candidate explanation for the difference; this run does not separate ties from rounding, and no
  figure above depends on the explanation.
- **Not measured:** the query-document-kept convention (the SQL counterpart of the 0.3836 row),
  NFCorpus and SciFact (a sparse row needs a vector set per corpus, and ArguAna's is the one this
  page measures both in memory and in PostgreSQL), recall@k as a ratio against an exact top-k, the
  cost of loading the table (one `INSERT` per document, no latency reported — every search opens a
  connection, which is the lane's shape rather than a property of the engine), and any vocabulary
  that does not cover every term in the vectors: the lane builds one that does, which is what makes
  the SQL dot product the same sum the in-memory engine computes.

## Learned sparse (SPLADE, through ONNX)

The sparse engine ranks by the dot product of two learned sparse vectors, and LexiSharp computes
neither: a model runs outside the library, once, and the harness scores what it wrote. The
`--sparse <dir>` flag loads precomputed vectors into `SparseTextSearchEngine` and measures them on
the same test queries, the same qrels and through the same `RetrievalMetrics` as every row above:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --sparse bench/LexiSharp.Eval/data/sparse-vectors

# ArguAna's test queries are documents, so the reported row passes --exclude-query-doc, the
# convention the published reference figure uses. The second command is the control with the query
# document left in the corpus.
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned \
  --sparse bench/LexiSharp.Eval/data/sparse-vectors --exclude-query-doc
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned \
  --sparse bench/LexiSharp.Eval/data/sparse-vectors
```

The vectors are SPLADE-v3 through the full-precision ONNX export (`splade-v3.onnx`, 532 138 951
bytes, sha256 `9e472022f478fec8…`), truncated to 256 tokens and pruned to the 256 highest-weighted
terms per text. Documents are encoded as `title + "\n" + text`; queries as their text verbatim.
[The producer's README](../bench/LexiSharp.Eval/sparse/README.md) carries the install, the model
download, the tokenizer route and the file format. The vectors are **not** committed: they are
tens of megabytes derived from a CC BY-NC-SA model, and the README is how to regenerate them.

Measured on 2026-10-26, one host (12 cores, `--jobs` default 8), nDCG@10 at **exponential gains —
the harness default, and the convention every other row on this page uses**. The aligned column is
`--analyzer english --reference-bm25 0.9,0.4 --query-term-frequency` at these exponential gains, which
is **not** the alignment row of the table at the top of this page: that one uses the reference's
`uax29` analyzer and its arithmetic, and reads 0.3218 / 0.6789 under linear gains. The column here is
the same parameters and analysis this sparse lane was compared with when it was written, and the two
must not be mixed. All three
columns come from runs in this session on NFCorpus and SciFact, and the two ArguAna rows come from
this session's two runs over the same vectors. ArguAna has no aligned cell — the aligned column is a
BM25 analysis the sparse model does not use — and its rows carry the two task conventions, because
1 298 of its 1 406 test queries name a document and the published reference figure drops it:

| Corpus | BM25 defaults (1.5 / 0.75) | BM25 at 0.9 / 0.4, English analysis | SPLADE-v3 (256 tokens, top 256) |
|---|---:|---:|---:|
| NFCorpus | 0.308 | 0.322 | **0.3596** |
| SciFact | 0.662 | 0.676 | **0.7083** |
| ArguAna, `--exclude-query-doc` | 0.425 | — | **0.5216** |
| ArguAna, query document kept | 0.320 | — | 0.3836 |

The first ArguAna row is the convention the 0.3970 reference figure was produced under. Dropping the
query's own document lifts BM25 by **0.105** nDCG@10 (0.320 → 0.425) and SPLADE by **0.1380**
(0.3836 → 0.5216), so the exclusion is worth more than the sparse row's margin over BM25 under either
convention (**+0.097** excluded, **+0.064** kept).

The three other metrics for the sparse rows, against BM25 at the aligned operating point where the
run has one (NFCorpus, SciFact), and at the harness-default parameters on ArguAna under both task
conventions:

| Corpus, engine | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|
| NFCorpus, BM25 aligned | 0.234 | 0.523 | 0.152 |
| NFCorpus, SPLADE-v3 | **0.2646** | **0.5714** | **0.1709** |
| SciFact, BM25 aligned | 0.631 | 0.642 | 0.800 |
| SciFact, SPLADE-v3 | **0.6678** | **0.6814** | **0.8157** |
| ArguAna, BM25 defaults, excluded | 0.341 | 0.341 | 0.697 |
| ArguAna, SPLADE-v3, excluded | **0.4300** | **0.4300** | **0.8179** |
| ArguAna, BM25 defaults, query document kept | 0.207 | 0.207 | 0.679 |
| ArguAna, SPLADE-v3, query document kept | **0.2530** | **0.2530** | **0.7980** |

- **SPLADE is above BM25 on all three corpora at this configuration** — by **0.052** nDCG@10 against
  the harness-default BM25 row on NFCorpus, **0.046** on SciFact and **0.097** on ArguAna under the
  exclusion (0.064 with the query document kept), or **0.038** and **0.032** against the aligned
  operating point on the first two, and by every other metric on all three. On ArguAna the aligned
  reference is **0.3970** at the published English analysis and k1=0.9/b=0.4, so the sparse row is
  **0.1246 above it** — a comparison of one task convention, one query list and one qrels set, not a
  like-for-like comparison of rankings: the analysis and the retrieval model both differ. The row is
  one model and one pruning budget: the truncation comparison below covers the token budget, any
  top-k other than 256 is not measured, and it is not a statement about learned-sparse retrieval in
  general.
- **The column above carries the repeated-term rule**, which is the reference's: SciFact reads
  0.676 with `--query-term-frequency` against 0.679 without it (32 % of its test queries repeat a
  word), NFCorpus 0.322 against 0.3225 (3 %, one query's worth). The alignment rows at the top of the
  page now use the reference's `uax29` analyzer and read 0.3218 / 0.6789; the numbers in this
  section were measured with `english` and must not be read as that row. The sparse row is unaffected
  either way: the flag reaches BM25's scorer only.
- **What the row is not.** The metrics contain no encode cost — the model ran offline, before the
  harness started, and its time is the producer's (48.1 min for NFCorpus's 3 633 documents and 3 237
  queries, 57.2 min for SciFact's 5 183 and 1 109, two encoders side by side at 6 threads each; 28.7
  min for ArguAna's 8 674 documents and 1 406 queries, one encoder at 6 threads, of which 25.4 min
  were documents and 3.3 min queries). The score run for the exclusion row took 9 m 58 s at
  `--jobs 8`. The row is not pinned: `pinned.json` keys on analyzer and BM25 parameters, and the
  pinned job has no vectors, so a pin could only ever skip. The model is not part of the library and
  no `LexiSharp` API runs it.
- **The title is in the document text, not weighted.** `SparseTextSearchEngine` has no field
  concept, so `title + "\n" + text` is one string on both sides. Unlike the BM25F rows, no title
  weight exists here, and the producer applies this rule for every corpus — otherwise two rows
  would differ in what they indexed without either of them saying so.
- **Top-256 is a pruning budget, and it binds.** The export emits every non-zero term; on a
  30-document NFCorpus sample it emitted a median of 254 and a maximum of 339, and 13 of the 30 were
  cut. The dropped terms are the lowest-weighted ones, and the row measures the pruned vectors, not
  the full ones.
- **The tokenizer is assembled, not loaded.** The checkpoint's tokenizer files are behind a gated
  repository, so the encoder builds a WordPiece tokenizer from the published vocabulary; it agrees
  token-for-token with a public copy of the checkpoint's `tokenizer.json` on all 13 162 texts of
  NFCorpus and SciFact. That check and its limitation are in the producer's README, and it does not
  cover ArguAna, which was encoded in this session.
- **ArguAna is measured under both conventions, and the exclusion is worth 0.105-0.138 nDCG@10.**
  1 298 of the 1 406 test queries name a document. Dropping it lifts BM25 from 0.320 to 0.425 and
  SPLADE from 0.3836 to 0.5216 in the same two runs; both pairs are in the tables above. The
  exclusion row is the one whose task convention matches the 0.3970 reference figure.
- **The tuned ArguAna rows are not at that convention.** BM25 tuned (0.363) and BM25F tuned (0.397)
  are in-sample oracles at the harness-default analysis, and both sit below the untuned BM25's 0.425
  under the exclusion. The tuner's 25-point grid contains (1.5, 0.75), so an in-sample best over that
  grid on the excluded queries cannot be lower; those two rows are scored with the query document
  kept, and neither is a fair baseline beside the sparse row.
- **What the exclusion does not align is the analysis.** SPLADE scores the model's WordPiece
  vocabulary and ranks by a learned sparse dot product; the aligned rows use the English analyzer
  with `k1=0.9, b=0.4`. The 0.5216 and the 0.3970 are two retrieval systems on one corpus, one query
  list and one qrels set, not two analyses of one index, and no task convention makes them the same
  thing.

**Doubling the truncation moves the row by about a hundredth, in both directions.** The checkpoint
accepts 512 tokens per text; the rows above encode 256. The same three corpora encoded at
`--truncate 512` — same model file (sha256 `9e472022…`), same top-256 pruning, same host, 6 ONNX
Runtime threads per encoder, two encoders side by side for NFCorpus and SciFact — and scored with the
command above at the harness-default gains give:

| Corpus | truncation | nDCG@10 | MAP@10 | MRR@10 | R@10 | document terms (mean) | encode wall clock |
|---|---:|---:|---:|---:|---:|---:|---:|
| NFCorpus | 256 | 0.3596 | 0.2646 | 0.5714 | 0.1709 | 232.3 | 48.1 min |
| NFCorpus | 512 | **0.3665** | 0.2686 | 0.5756 | 0.1741 | 242.4 | 48.6 min |
| SciFact | 256 | 0.7083 | 0.6678 | 0.6814 | 0.8157 | 241.2 | 57.2 min |
| SciFact | 512 | **0.7174** | 0.6757 | 0.6914 | 0.8256 | 246.7 | 54.4 min |
| ArguAna, `--exclude-query-doc` | 256 | 0.5216 | 0.4300 | 0.4300 | 0.8179 | 247.2 | 28.7 min |
| ArguAna, `--exclude-query-doc` | 512 | 0.5074 | 0.4164 | 0.4164 | 0.8023 | 247.4 | 31.1 min |
| ArguAna, query document kept | 256 | 0.3836 | 0.2530 | 0.2530 | 0.7980 | 247.2 | 28.7 min |
| ArguAna, query document kept | 512 | 0.3737 | 0.2459 | 0.2459 | 0.7795 | 247.4 | 31.1 min |

The 256 rows are the rows tabulated above, from vectors encoded at 256 tokens with the same model
and the same pruning; the only encoder argument that differs is `--truncate`, and both sets are
scored with the same command, the same queries, the same qrels and the same gains. Doubling the
truncation raises nDCG@10 by **0.0069** on NFCorpus and **0.0091** on SciFact, and lowers it by
**0.0142** on ArguAna under the exclusion convention (**0.0099** with the query document kept) — a
hundredth or less either way, and not a direction the three corpora agree on. A ranking over a fixed
vector set is deterministic (a repeat of each run reads the same four decimals), so none of that
difference is rerun noise: it is a different vector set entering the same scorer. For scale, a
refitted BM25 is worth −0.001 to +0.004 nDCG@10 on NFCorpus and SciFact in the held-out table below,
where these moves are 0.007 to 0.014.

What the direction depends on is not established. It is consistent with where the truncation
actually bites: every NFCorpus and SciFact query is shorter than 256 tokens (longest 38 and 79), so
those two rows change on the document side only, while 513 of ArguAna's 1,406 queries exceed 256
tokens (longest 1,087), so its query vectors change too — and ArguAna is the one corpus of the three
where the row falls. That is a correlation across three corpora, not a mechanism measured here.

The top-256 budget still binds at 512: documents average 242.4, 246.7 and 247.4 terms against 232.3,
241.2 and 247.2 at 256, so a 512-token vector is a different selection of 256 terms, not the
256-token vector with more terms on the end. **The encode clocks are two sets of timings, not a
speedup ratio.** The 512 encodes ran while another measurement shared the host (load average 12-22
on 12 cores for the side-by-side pair, around 8 for ArguAna alone), and the host load behind the 256
clocks above was not recorded. What the 512 numbers establish is that an encode at 512 is about an
hour per corpus at 6 threads — 48.6, 54.4 and 31.1 minutes — while the encoder fed 1.44×, 1.36× and
1.12× more tokens per document than at 256.

**The pruning budget is nearly flat above 256.** The encoder keeps the `--top-k` highest-weighted
terms per text; every row above uses 256. NFCorpus re-encoded at 128 and at 512, same model file,
same 256-token truncation, scored with the command above:

| top-k | nDCG@10 | MAP@10 | MRR@10 | R@10 | document terms (mean) | encode (documents + queries) |
|---:|---:|---:|---:|---:|---:|---:|
| 128 | 0.3567 | 0.2615 | 0.5717 | 0.1679 | 128.0 | 62.3 + 3.6 min |
| 256 (published) | 0.3596 | 0.2646 | 0.5714 | 0.1709 | 232.3 | 48.1 min |
| 512 | 0.3598 | 0.2648 | 0.5711 | 0.1710 | 246.4 | 62.0 + 3.7 min |

Halving the budget costs **0.0029** nDCG@10; doubling it buys **0.0002**, which is inside this
corpus's own run-to-run spread. At 512 the budget stops binding altogether — documents average 246.4
non-zero terms, so nothing is cut — and the only difference from 256 is which terms the smaller
budget had dropped. Pruning is not additive: a larger budget keeps a different set, it does not keep
the smaller set plus more. The two 65-minute clocks were taken with four encoders sharing the host,
so they are the cost of an encode under contention, not a speed ratio against the 256 row's 48.1
minutes.

**The 8-bit export is a different model, not a cheaper copy.** `splade-v3-8bit.onnx` (133 941 688
bytes, dynamically quantised weights) re-encodes both corpora at the published settings:

| Corpus | export | nDCG@10 | MAP@10 | MRR@10 | R@10 | document terms (mean) | encode (documents + queries) |
|---|---|---:|---:|---:|---:|---:|---:|
| NFCorpus | full precision | 0.3596 | 0.2646 | 0.5714 | 0.1709 | 232.3 | — |
| NFCorpus | int8 weights | 0.3490 | 0.2525 | 0.5616 | 0.1645 | 164.1 | 52.2 + 3.1 min |
| SciFact | full precision | 0.7083 | 0.6678 | 0.6814 | 0.8157 | 241.2 | — |
| SciFact | int8 weights | **0.7236** | 0.6845 | 0.6996 | 0.8252 | 164.2 | 66.8 + 0.3 min |

It loses **0.0106** on NFCorpus and gains **0.0153** on SciFact — no direction, which is what a
different model looks like rather than a cheaper approximation of the same one. The quantised export
also emits fewer non-zero terms per document (164 against 232 and 241), so the top-256 budget does
not bind there either. The vector-level comparison that was already on this page (mean cosine 0.904
on SciFact queries) said the same thing before any metric existed; the row is the metric.

The manifest records `quantised: true` for these vectors and the row's model line reads `SPLADE-v3
(ONNX, int8 weights, 256-token truncation, top-256 terms, vectors precomputed offline)`, so a reader
can see which export produced a row. **What differs between the two rows is the retrieval model, not
the index**: the same `SparseTextSearchEngine` scores the same documents, queries and qrels, and a
repeat of each run reads the same four decimals, so none of the 0.0106 or the 0.0153 is rerun noise.
Set against the truncation move above (+0.0069 / +0.0091), quantisation is **1.5× and 1.7× larger and
points the other way on SciFact** — a difference of about a hundredth in whichever direction the
corpus takes it, which is what separates a second model from a lossy build of the first.

- **Not measured:** the truncation × pruning interaction, any top-k but 128 / 256 / 512 on
  NFCorpus, any other corpus at another budget, and the encode as a performance claim — the clocks
  above were taken with four encoders sharing the host. The PostgreSQL `sparsevec` engine's row is
  in [the SQL backend section](#the-sql-backends-on-the-same-corpora): two rows over the same
  ArguAna vectors, and what separates them is the index.


## Metrics

`RetrievalMetrics` covers the standard set, so a number means here what it means in the
literature:

| Metric | The question it answers |
|---|---|
| `Precision@k`, `Recall@k`, `F1@k` | how much of the page was right, how much of the truth was found |
| `nDCG@k`, binary and **graded**, both gain conventions | is the right document near the top, weighted by its grade |
| `ReciprocalRank@k` (→ MRR) | how high the first relevant document appears |
| `AveragePrecision@k` (→ MAP) | precision at every rank where a relevant document appears |

Relevance grades come from the qrels file. A query carrying no judgement is loaded and
counted in the run, but excluded from the averages rather than scored as a zero — silently
averaging in a zero is how a harness flatters itself.

**nDCG has two conventions and only one of them is the published one.** `NdcgAtK` defaults to
exponential gains (`2^rel − 1`); the standard evaluation tool, and therefore the published BM25
figures, set the gain to the relevance level itself. They agree on binary relevance and
disagree on a graded corpus: NFCorpus scores 0.3080 exponential against 0.3071 linear for the same
run. Pass `--ndcg-gain linear` before quoting a number next to a published one.

## Tuning BM25, BM25F, BM25+ and BM25L

Let the corpus pick its own parameters. `Bm25ParameterTuner` grid-searches `k1` and `b`
against your own validation queries, judged by `Precision@k`, `Recall@k`, `F1@k` or
`nDCG@k`; `Bm25FParameterTuner` does the same for BM25F's `k1`/`b` and its per-field
weights, and adds one question of its own. `Bm25PlusParameterTuner` and
`Bm25LParameterTuner` search the single-field variants' third parameter, the `delta` lower
bound, over the full `k1 × b × delta` product.

| Tuner | Searches | Default points | Reports |
|---|---|---|---|
| `Bm25ParameterTuner` | `k1 × b` | 25 | — |
| `Bm25FParameterTuner` | `(k1, b)`, then field weights | 25 + Π weights | `WeightingHelped` |
| `Bm25PlusParameterTuner` | `k1 × b × delta` | 125 | `DeltaHelped` |
| `Bm25LParameterTuner` | `k1 × b × delta` | 125 | `DeltaHelped` |

**A tuner scores candidates under the caller's conventions.** Each of the four constructors takes an
optional `SearchOptions`; it is the template for every candidate search, with `Limit` and the
validation query's `ExcludedDocumentIds` replaced per query and nothing else. `null` — the default —
is the library defaults, so a tuner built without it performs the same search it always did. The
setting that matters is `ParseQuerySyntax`: a quoted phrase is positional adjacency to a tuner left on
the default and literal text to a run that reports with syntax off. On ArguAna, whose queries are
whole arguments and 138 of 1,406 carry a straight quote, the two readings disagree about which queries
can match at all; on NFCorpus and SciFact no query carries syntax, so their tuned rows do not depend
on the option. The BEIR harness passes the options it reports with only under `--tune-search-options`,
off by default, so an existing run keeps the convention its tuned rows were fitted under.

**A tuned score is an oracle, not a fair baseline.** The winner was fitted on the same
queries it is scored on, and the more configurations were tried, the more of the gain is
fitting noise. Score it on a held-out set — `--heldout` does that for all four tuners, below —
or treat it as an upper bound. Every comparison
in this documentation that claims a scorer "beats" another was run tuned-against-tuned for
exactly this reason — a default-vs-default comparison mostly measures which default fits
the corpus.

**State the objective, and check it is not flat.** Every tuner maximizes the metric you name, and
the default is `F1@k`. On the reference corpus `F1@5` is **0.3588 for all six configurations** — a
flat objective gives a tuner no signal, so its "winner" is whichever grid point the tie-break
reached first, and a tuned row built on it is a number that means nothing. Fitting two scorers on
that flat objective is how BM25F-tuned and BM25-tuned both came out at 0.8812; re-fitted on the
nDCG they are reported in, they are 0.8867 and 0.8978. The `lexisharp benchmark` CLI takes
`--metric ndcg` for exactly this, and prints the objective it used.

**`DeltaHelped` is the answer to "does the lower bound help on my corpus".** It is `false` on the
reference corpus and on NFCorpus — where all three tuners settled on identical parameters
(`k1=2, b=0.5, δ=0`) and an identical 0.311 nDCG@10, the δ search choosing no bound and handing
back BM25. On the reference corpus `false` is the weaker of the two reasons: 46 of the 125 points tie
at the maximum and every δ from 0 to 1 is among them, so δ = 0 is what the tie-break reached rather
than a preference — that corpus cannot separate the formulas at all. It is `true` on SciFact, by
0.002 and 0.004 nDCG@10 at `δ=0.25`, fitted in-sample on the 300 queries that chose it. The harness
prints each variant's own unfloored score beside it, and on SciFact that baseline reads **0.664 —
identical to `BM25 tuned`**, so the two 125-point searches agree with `Bm25ParameterTuner` on the
no-bound slice and the margin is δ's rather than the price of a wider grid. It is still a 0.004 lead
fitted in-sample over 125 candidates, so an upper bound rather than a result. `0.0` belongs in
`deltaValues` for the other reason: without a no-bound candidate the search cannot conclude that
nothing helps, and `UnflooredMetricScore` is `NaN` rather than a fabricated comparison.

**Both variants degenerate to BM25 at `δ = 0`, which is what makes the tables readable** — obvious for
BM25+, whose `δ` is additive, and for BM25L a cancellation in the term weight
([worked through in BM25 variants](ranking.md#tuning-delta-and-what-it-does-to-the-comparison)). A
`δ = 0` grid therefore returns `Bm25ParameterTuner`'s own winner and score, which
`AZeroDeltaGridReproducesTheBm25ParameterTunerExactly` asserts across all three tuners.

```csharp
var plus = new Bm25PlusParameterTuner(index, validationQueries).Tune(topK: 10, metric: TuningMetric.Ndcg);

if (plus.DeltaHelped)
    Console.WriteLine($"delta {plus.Delta} beat no delta; k1={plus.K1} b={plus.B}");
else
    Console.WriteLine($"no delta beat no delta; best was k1={plus.K1} b={plus.B}");

var plusEngine = new RankedTextSearchEngine(index, new Bm25PlusScorer(plus.K1, plus.B, plus.Delta));
var lEngine = new RankedTextSearchEngine(
    index, new Bm25LScorer(l.K1, l.B, l.Delta));
```

At 125 points the δ tuners cost five times a BM25 search, so both are capped at 512 configurations
and refuse above it with the count rather than trimming the grid.

`Bm25FParameterTuner` grid-searches `(k1, b)` and the weight of each named field, in two stages:
`(k1, b)` first with fields neutral, then the weights at that winner. That is coordinate descent,
not an exhaustive product, and the class says so — a configuration that is only good jointly can be
missed. The δ tuners have no such caveat: their product *is* searched in full. The run is refused
above a configuration cap, with the count, rather than silently trimmed.

```csharp
var result = new Bm25FParameterTuner(index, validationQueries).Tune(
    weightedFields: ["title"],
    weightValues: [1.0, 1.5, 2.0, 3.0],
    topK: 10,
    metric: TuningMetric.Ndcg);

if (result.WeightingHelped)
    Console.WriteLine($"title weighs {result.Parameters.FieldWeights["title"]}");
else
    Console.WriteLine($"no weighting beat neutral; best was k1={result.Parameters.K1} b={result.Parameters.B}");

var engine = new RankedTextSearchEngine(index, new Bm25FScorer(result.Parameters));
```

Two things it deliberately reports rather than hides:

- **`WeightingHelped` is the answer to "does weighting help on my corpus".** It is `false` on the
  reference corpus and on all three BEIR corpora, measured by this harness with `--tune-bm25f`
  (title weight 1.0 in every case). `1.0` belongs in `weightValues` for exactly this reason:
  without a neutral candidate the search cannot conclude that nothing helps.
- **The best score is an oracle, not a fair baseline.** The winner was fitted on the same queries it
  is scored on, and the more configurations were tried the more of the gain is fitting noise. Score
  the result on a held-out set or treat it as an upper bound. `Bm25Tuned` has carried this caveat
  all along; this one does too.

Reuse the same `Bm25ValidationQuery` input as `Bm25ParameterTuner` — a single validation type for
both, deliberately.

**Without a field-aware index it refuses, by name.** Scoring against an index that is not an
`IFieldStatisticsIndex` throws `NotSupportedException` naming the index rather than ranking
on zeros:

```csharp
// Throws: "BM25F needs per-field statistics, and FlatIndex has none…"
new Bm25FScorer().Score("1", ["ranking"], someIndexWithoutFields);
```

## Held-out measurement (`--heldout`)

`--heldout <share>` withholds a share of the run's validation queries from every tuner and prints
each tuned row's fit-set figure beside its score-set figure. The point is to replace "upper bound" with
a number, and to show whether the parameters the fit set chose are worth anything on queries that did
not choose them.

**The split rule.** The bucket is FNV-1a (64-bit) over the query id's UTF-16 code units, modulo
1,000,000; a bucket below `share × 1,000,000` is the **score set**, and the rest is the **fit set**.
The id is the only input: no RNG, no seed, no dependence on file order, corpus order or the query
text. The split is taken over the queries the run would score, after `--limit`. `--heldout 0.5` on
NFCorpus therefore always reads the same 156 fit and 167 score queries, on any machine, from the
command alone. The rule lives in `bench/LexiSharp.Eval/HeldOutSplit.cs` and the buckets are pinned by
unit tests, so a refactor that changes the partition fails rather than quietly measuring a different
one.

**What the flag changes.** Every tuner (`Bm25ParameterTuner`, `Bm25FParameterTuner`,
`Bm25PlusParameterTuner`, `Bm25LParameterTuner`) fits on the fit set only. The tuned rows in the main
table are then *scored on the fit set*, so that table's `Queries` column reads 156 rather than 323 on
NFCorpus. The held-out block prints, for each of those rows, the fit-set nDCG@10 (in-sample) beside
the score-set nDCG@10 (held out), with MAP@10 and recall@10 for each side, and then the fixed
`BM25 (k1=1.5, b=0.75)` row scored on the same withheld queries — a fitted number has nothing to be
compared with otherwise. Without the flag nothing changes: the published rows keep the numbers they
have, because they were fitted and scored on the whole set.

**Commands.** These are the exact runs behind the table; the runs without the flag are the control
that the published in-sample figures are unchanged.

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --heldout 0.5 --jobs 4
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset scifact --heldout 0.5 --jobs 4

# The BM25F column and ArguAna.
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --heldout 0.5 --jobs 8 --tune-bm25f
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset scifact --heldout 0.5 --jobs 8 --tune-bm25f
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --heldout 0.5 --jobs 8
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --heldout 0.5 --jobs 8 --tune-bm25f

# The ArguAna fraction sweep: single-field tuners only, no BM25F.
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --heldout 0.25 --jobs 8
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --heldout 0.75 --jobs 8

# The same corpora without the flag, for the published in-sample rows.
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --jobs 4
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset scifact --jobs 4
```

**Measured on 2026-10-25 (the three single-field tuner rows on NFCorpus and SciFact) and 2026-10-26
(the BM25F rows, the ArguAna rows at 0.5 and the ArguAna fraction sweep), default analysis.** The
NFCorpus and SciFact single-field rows ran at `--jobs 4` and the rest at `--jobs 8`; the flag changes
wall clock, not a metric, and the later re-runs reproduced the three single-field rows on both
corpora to the printed precision. nDCG@10 is the metric the tuners maximize; Δ fit→held is held-out
minus fit-set; Δ vs untuned is held-out minus the fixed `BM25 (k1=1.5, b=0.75)` row on the same
withheld queries. Wall clock, which is the cost of the run and not a benchmark: 23 s NFCorpus, 106 s
SciFact, 14 m 20 s ArguAna at 0.5, 69 m 46 s ArguAna at 0.5 with `--tune-bm25f`, 21 m 35 s ArguAna at
0.25 and 8 m 47 s ArguAna at 0.75. The three shorter NFCorpus/SciFact runs overlapped the 70-minute
one on this host, so their times are upper bounds on a solo run; the two fraction-sweep runs did not
overlap another evaluation.

| Corpus | Split (fit / score) | Row | Fit nDCG@10 | Held-out nDCG@10 | Δ fit→held | Held-out MAP@10 | Held-out R@10 | Δ vs untuned |
|---|---|---|---|---:|---:|---:|---:|---:|---:|
| NFCorpus | 156 / 167 | BM25 tuned (k1=2, b=0.5) | 0.331 | 0.292 | −0.039 | 0.215 | 0.139 | +0.001 |
| | | BM25+ tuned (k1=2, b=0.5, δ=0) | 0.331 | 0.292 | −0.039 | 0.215 | 0.139 | +0.001 |
| | | BM25L tuned (k1=2, b=0.5, δ=0) | 0.331 | 0.292 | −0.039 | 0.215 | 0.139 | +0.001 |
| | | BM25F tuned (k1=2, b=0.25, title=1.5) | 0.328 | 0.290 | −0.038 | 0.213 | 0.138 | −0.001 |
| | | BM25 (k1=1.5, b=0.75), untuned reference | — | 0.291 | — | 0.214 | 0.140 | — |
| SciFact | 153 / 147 | BM25 tuned (k1=1.5, b=1) | 0.650 | 0.679 | +0.029 | 0.629 | 0.822 | −0.001 |
| | | BM25+ tuned (k1=2, b=1, δ=0.25) | 0.652 | 0.680 | +0.028 | 0.630 | 0.822 | 0.000 |
| | | BM25L tuned (k1=2, b=1, δ=0.25) | 0.652 | 0.684 | +0.032 | 0.635 | 0.822 | +0.004 |
| | | BM25F tuned (k1=1.2, b=0.5, title=1.5) | 0.655 | 0.679 | +0.024 | 0.631 | 0.814 | −0.001 |
| | | BM25 (k1=1.5, b=0.75), untuned reference | — | 0.680 | — | 0.632 | 0.814 | — |
| ArguAna | 1076 / 330 (0.25) | BM25 tuned (k1=2, b=1) | 0.366 | 0.352 | −0.015 | 0.231 | 0.733 | +0.043 |
| | | BM25+ tuned (k1=2, b=1, δ=0) | 0.366 | 0.352 | −0.015 | 0.231 | 0.733 | +0.043 |
| | | BM25L tuned (k1=2, b=1, δ=0) | 0.366 | 0.352 | −0.015 | 0.231 | 0.733 | +0.043 |
| | | BM25 (k1=1.5, b=0.75), untuned reference | — | 0.309 | — | 0.201 | 0.658 | — |
| ArguAna | 718 / 688 | BM25 tuned (k1=2, b=1) | 0.354 | 0.372 | +0.019 | 0.248 | 0.765 | +0.044 |
| | | BM25+ tuned (k1=2, b=1, δ=0) | 0.354 | 0.372 | +0.019 | 0.248 | 0.765 | +0.044 |
| | | BM25L tuned (k1=2, b=1, δ=0) | 0.354 | 0.372 | +0.019 | 0.248 | 0.765 | +0.044 |
| | | BM25F tuned (k1=2, b=1, title=3) | 0.395 | 0.405 | +0.010 | 0.277 | 0.807 | +0.077 |
| | | BM25 (k1=1.5, b=0.75), untuned reference | — | 0.328 | — | 0.214 | 0.688 | — |
| ArguAna | 344 / 1062 (0.75) | BM25 tuned (k1=2, b=1) | 0.358 | 0.365 | +0.007 | 0.241 | 0.756 | +0.044 |
| | | BM25+ tuned (k1=2, b=1, δ=0) | 0.358 | 0.365 | +0.007 | 0.241 | 0.756 | +0.044 |
| | | BM25L tuned (k1=2, b=1, δ=0) | 0.358 | 0.365 | +0.007 | 0.241 | 0.756 | +0.044 |
| | | BM25 (k1=1.5, b=0.75), untuned reference | — | 0.321 | — | 0.208 | 0.684 | — |

The two halves differ in difficulty, so the fit/held-out gap is not an overfitting measure on its
own. The untuned default row reads 0.308 on all 323 NFCorpus queries and 0.291 on the withheld 167;
on SciFact it reads 0.662 on all 300 and 0.680 on the withheld 147; on ArguAna it reads 0.320 on all
1,406 and 0.328 on the withheld 688. What a tuned row is worth is therefore the held-out column
against the untuned row on the same withheld queries, not against its own fit-set figure.

The three single-field tuners choose the same parameters the published full-set tune chooses on all
three corpora (NFCorpus `k1=2, b=0.5`; SciFact `k1=1.5, b=1` and `δ=0.25` for both variants; ArguAna
`k1=2, b=1` and `δ=0` for both variants), so for them the split is not reporting a different winner.
It is reporting what that winner is worth on the other half. BM25F's half fits differ from its
full-set fits; see below.

- **NFCorpus.** The three single-field tuners read 0.292 held out against the untuned row's 0.291:
  **+0.001**. The 0.039 between their fit-set and held-out figures is a difficulty difference, not a
  transfer. BM25F's fit half picks `k1=2, b=0.25, title=1.5` and reads 0.290 held out: **−0.001**
  against 0.291.
- **SciFact.** The fit-set figure (0.650) is *below* the held-out one (0.679), so the two halves differ
  in difficulty and the fit/held-out gap is not itself an overfitting measure — the untuned row on the
  withheld queries is. It reads 0.680: BM25 tuned is **−0.001**, BM25+ tuned **0.000**, BM25L tuned
  **+0.004**. That +0.004 is the same margin the full-set in-sample row shows (0.668 against 0.664),
  so it survives at the same size — four thousandths of nDCG@10 on 147 queries, not a result. BM25F's
  half fit is `k1=1.2, b=0.5, title=1.5` and reads 0.679: **−0.001**. That is a different parameter
  point from its full-set fit (`k1=1, b=0.5, title=1`, 0.668), so on this corpus the half does not
  re-run the full-set winner.
- **ArguAna.** The split is 718 / 688 — FNV does not divide 1,406 exactly in half. All four tuners pick
  `k1=2, b=1`; the two variants pick `δ=0` and collapse onto BM25, and BM25F adds `title=3`. Held out,
  BM25 at those parameters reads **0.372** against the untuned 0.328: **+0.044**. BM25F reads
  **0.405**: **+0.077**, the largest held-out margin in this table, and above its own fit-set figure
  (0.395) because the withheld half is the easier one. The fit is not free of the convention it was
  made under: the tuner scores candidates with query syntax on and the table reports with it off,
  which is why the tuner's own score for the winning BM25F configuration (0.358) is below the fit
  column (0.395), and its unfloored BM25 baseline (0.32) below that fit column's 0.354. The held-out
  and untuned columns are both syntax off, so their difference compares like with like.
- **ArguAna at other fractions.** The three single-field tuners run at 0.25 and 0.75 as well, and the
  transfer is the same size at all three. At 0.25 (1076 fit / 330 score) the fitted `k1=2, b=1` reads
  **0.352** held out against the untuned 0.309: **+0.043**; its fit figure is 0.366, so the fit→held
  gap is −0.015. At 0.75 (344 fit / 1062 score) it reads **0.365** against 0.321: **+0.044**, with a
  fit figure of 0.358 and a +0.007 gap. Both variants pick `δ=0` and collapse onto BM25 at both
  fractions, so the margin is the `(k1, b)` fit, not a variant's delta. The held-out margin holds at
  three fractions on this corpus — 1076/330, 718/688 and 344/1062 — and no fraction in the sweep
  breaks it. It is a shape on one corpus, not a law: NFCorpus and SciFact are measured at 0.5 only,
  and the sweep has no BM25F row, because a BM25F run costs about an hour per split.

**What the halves say about BM25F's field weight.** On the full set every fitted title weight is 1.0
and `WeightingHelped` is false. On the fit halves the tuner chooses 1.5 (NFCorpus, SciFact) and 3.0
(ArguAna), and reports `WeightingHelped = true` on all three: 0.327 against 0.327, 0.655 against
0.655, 0.358 against 0.356. The first two margins are below the precision the harness prints, and
they buy nothing on the withheld queries (−0.001 on both corpora). ArguAna's 0.002 margin is the one
above that precision, and it is the fit-half margin attributable to the weight — not the +0.077
against untuned BM25, which also contains the `(k1, b)` fit and BM25F's single-field length term.
The held-out runs score the fitted configuration only, so none of the three isolates the weight on
the withheld half; what is measured there is the whole fitted configuration.

**What remains an upper bound.** Every tuned row not in the table: every fraction other than 0.5 on
NFCorpus and SciFact, and every fraction other than 0.25, 0.5 and 0.75 on ArguAna; BM25F at any
fraction other than 0.5; every run under another analysis (`--analyzer english`, `--stem porter`,
another `--ndcg-gain`); every depth other than 10; every lane with no tuned row (the SQL backends and
the dense, sparse and rerank lanes have no held-out measurement); and the non-BEIR reference-corpus
rows, which are outside this matrix. BM25F is measured with `title` as the only weighted field;
another field set is unmeasured. The published full-set rows are intact and still in-sample figures
over all queries; the pairs above cover three corpora, three fractions on ArguAna and one on the
other two, and the one analysis the commands name.

`--heldout` is refused with `--analyze`, `--verify-reference` and `--fingerprint`, whose output
shapes assume every configuration scores every query or stop before scoring starts. With
`--no-tuned` the held-out block is still printed and reads `Not run: no tuned row was fitted…`, so a
run without a fitted row has a stated cause rather than looking like a split that found no gap.

## Benchmark CLI (`LexiSharp.Cli`)

Compare ranking strategies over your own corpus without writing code. The runner
(`LexiSharp.Benchmarking` in the core) builds one shared in-memory index, evaluates every
selected configuration against the same labeled queries and reports nDCG/MAP/MRR/Recall/
Precision/F1 at the retrieval depth plus the per-query latency:

```
dotnet run --project bench/LexiSharp.Cli -c Release -- benchmark ./notes \
    --queries queries.json --qrels qrels.tsv --top-k 10
```

- `--queries` accepts a JSON object `{ "id": "query text", ... }` or a TSV `id⇥text`.
- `--qrels` is a TSV `qid⇥docid[⇥grade]` (grades are read as binary relevance); a query
  without any judgment is loaded but excluded from the metric averages.
- `--configs` selects the comparison: `bm25`, `bm25-tuned` (fits `(k1, b)` on the labeled
  queries), `tfidf`, `ql` (query likelihood), `hybrid` (RRF over BM25 + TF-IDF),
  `bm25+`/`bm25l` (the variants at a fixed `delta`) and `bm25+-tuned`/`bm25l-tuned` (fitted
  on `(k1, b, delta)`), `bm25f`, `bm25f-title`, `bm25f-tuned`, the three `bm25-proximity*`
  shapes, and `bm25-semantic`.
- `--metric <precision|recall|f1|ndcg>` chooses what every `*-tuned` configuration maximizes.
  The default is `f1`, and **a flat objective makes a tuned row meaningless** — see the tuning
  section above. Pass the metric you intend to report. The objective actually used is echoed in the
  run's output.
- `--json <path>` writes the results as a machine-readable report.

The same comparison is available in-process through `CorpusBenchmark.Run` over any
`IReadOnlyCollection<SearchDocument>` and `BenchmarkQuery` set, with custom engines reachable
through the public `BenchmarkConfig` constructor.

### A persistent store from the CLI

`segment` builds a corpus into a `SegmentedTextIndex` on disk, changes it, and searches it — each
subcommand in its own process, so the store is what carries state between them:

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- segment build ./notes --store ./store
dotnet run --project bench/LexiSharp.Cli -c Release -- segment search ./store --query "pagination window"
dotnet run --project bench/LexiSharp.Cli -c Release -- segment remove ./store --id notes/paging.md
dotnet run --project bench/LexiSharp.Cli -c Release -- segment info ./store
```

- `build <corpus-dir> --store <dir>` indexes the corpus and commits it. `--analyzer default|english`
  selects the analysis; the default keeps stop words and does not stem.
- `add <store-dir> --corpus <dir>` adds documents to the store's live overlay and commits.
- `remove <store-dir> --id <doc-id>` tombstones a document and commits. The commit recompacts, so
  **every surviving score can move** — the statistics stop being frozen.
- `commit <store-dir>` compacts the store: it merges the tombstones and rewrites one segment.
- `search <store-dir> --query <text> [--top-k <n>] [--json <path>]` ranks the committed corpus.
- `info <store-dir>` reports the counts without searching.

The analysis is recorded in `store.json` beside the segments, and every subcommand reads the recorded
one — a store's dictionary holds normalized terms, so searching it with a different tokenizer would
read a different index without saying so. `build` is the only subcommand that chooses the analyzer.

## Comparing two configurations, query by query

A pair of means cannot tell you what a change actually did. Two configurations can land
0.028 apart while one of them rescued a query and lost two others — and the mean is silent
about both.

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- diff bench/reference-corpus/corpus \
    --queries bench/reference-corpus/queries.json --qrels bench/reference-corpus/qrels.tsv \
    --baseline bm25 --candidate bm25-semantic --top-k 5
```

Recorded output of that exact command, corpus-derived expansion against plain BM25 — a
negative result, and the reason the lane is not a default:

```
mean nDCG@5: 0,8751 -> 0,8469 (-0,0282 per query)
queries: 0 improved, 2 degraded, 20 unchanged (net -2)

DEGRADED by BM25 + semantic
  long-document-02         0,500 -> 0,000 (-0,500)  lost: the judged document at rank 3 is gone
    "how often should a token be exchanged"
  paraphrase-02            0,579 -> 0,459 (-0,120)  demoted: rank 2 -> 3
    "how long is an access token valid"

Read: the candidate loses more queries than it gains (2 vs 0). A mean can improve while this is true.
```

The `Reading` on each line names the cause rather than restating the numbers, because *lost*,
*rescued*, *demoted* and *same rank* are four different bugs. A net of zero is not "no change": it
is movements that cancelled out, which is precisely what a mean hides.

`CorpusBenchmark.Run` returns the same breakdown in `BenchmarkConfigResult.PerQuery` (metrics per
query, the retrieved ids, and the 1-based rank of the first judged document), and
`BenchmarkComparer.Compare(baseline, candidate, epsilon)` builds the comparison. It refuses runs over
different query sets or in different orders rather than reporting a comparison against nothing.
`--epsilon` (default `1e-9`) is the score difference below which a query counts as unchanged; the
reported mean delta stays raw arithmetic.

The [reference corpus](https://github.com/manuc66/LexiSharp/blob/main/bench/reference-corpus/README.md) is the corpus the command above was run
against.

## Evaluating a live engine, on demand

`CorpusBenchmark` evaluates *configurations* over a corpus snapshot it indexes itself. For the
engine an application is actually serving — with whatever it currently holds, incremented by
real writes — `RetrievalEvaluator` runs a fixed panel of labeled queries against that live
engine and reports the metrics at a depth:

```csharp
using LexiSharp.Benchmarking;

var panel = new[]
{
    new BenchmarkQuery("q1", "how do I renew an expired token", ["doc-17", "doc-92"]),
    new BenchmarkQuery("q2", "refresh policy",            ["doc-17"]),
};

var evaluator = new RetrievalEvaluator(panel, topK: 10);
PanelEvaluationResult result = evaluator.Evaluate(engine);
```

Call it periodically against the same panel and compare successive results: a panel metric
moving is the relevance half of data-drift observability. `PanelEvaluationResult` carries the
means (`Metrics`, the same `BenchmarkMetrics` shape a CLI run reports) and the per-query rows
(`ByQuery`, each with the retrieved ids, scores and the 1-based rank of the first judged
document), so a movement can be attributed to one query rather than to the mean.

The panel is the app's own: relevance comes from the judgments it pinned, and every query is
searched with the caller's options except `Limit`, which is pinned to the metric depth — the
retrieval depth and the evaluation depth stay one number. A query without judgments is loaded
but excluded from the averages; a panel with none reports zeros, not a measurement. Graded
judgments keep the library's exponential nDCG convention by default, with
`NdcgGain.Linear` available for comparing against a linear-convention figure.

## Performance

Latency and allocation numbers live in [Benchmarks](benchmarks.md): BenchmarkDotNet runs on
one named machine, with its configuration and the reproduce command. Read them as
indicative and re-run the suite on your own hardware. Of the two kinds of number there, the
one that travels is **allocation** (counted in bytes, host-independent); wall-clock means
from a shared or contended machine are not publishable, and none are quoted from CI.
What *does* gate a build is deterministic and clock-free: `lexisharp verify`, which replays
the reference corpus against a committed baseline.
