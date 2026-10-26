# SPLADE vectors for the evaluation harness

The library never runs a model. This directory is the **producer** side of
`ISparseEmbeddingProvider` for the on-disk SQL-free sparse row: `encode_splade.py` runs a SPLADE
ONNX export over a corpus once and writes its vectors to disk, and
`dotnet run --project bench/LexiSharp.Eval -- --dataset <corpus> --sparse <dir>` scores them with
`SparseTextSearchEngine`, the same test queries, the same qrels and the same metric code as every
other row.

Nothing here is referenced by `LexiSharp.dll`, and no model, weight file or vector file is committed:
the ONNX export is 532 MB and licensed **CC BY-NC-SA 4.0**, and the vectors are megabytes derived
from it.

## What it runs

| Piece | What was used | Where it comes from |
|---|---|---|
| Model | `splade-v3.onnx`, 532 138 951 bytes, sha256 `9e472022f478fec8c4afaa397e68f9c90bb5ff98f0f5c0c6bb195cf2b388ab30` | `castorini/splade-v3-onnx`, the full-precision export of `naver/splade-v3` |
| Graph output | `output_idx` `int64`, `output_weights` `float` | the export's own SPLADE head: `max over tokens of log1p(relu(logits))`, masked by the attention mask, non-zero terms only |
| Tokenizer | `BertWordPieceTokenizer` built from `splade-v3-vocab.txt` (30 522 entries), lowercase, accent stripping | assembled from the published vocabulary; see [Tokenizer](#tokenizer) |
| Runtime | `onnxruntime` 1.31.0, CPU execution provider | Python 3.14.7 virtualenv |
| Pruning | top 256 terms per text | the encoder's `--top-k` |
| Truncation | 256 tokens per text | the encoder's `--truncate` |

The model's licence is non-commercial and share-alike: **do not commit the weights or the vectors**.
They live under `bench/LexiSharp.Eval/data/`, which is gitignored, and this README is how to
regenerate them.

**Write them to the main checkout's `bench/LexiSharp.Eval/data/`, not to a worktree's.** A worktree
is disposable and its `data/` is invisible to `git status`, so a cleanup deletes the vectors without
a warning — an ArguAna encode is 23 minutes and 50 MB. The same goes for the model file: point
`--model` at a path outside any worktree, or expect to fetch 532 MB again.

## Install

A virtualenv on disk, not `/tmp` (the export alone is 532 MB and a tmpfs would hold it in RAM):

```bash
python3 -m venv bench/LexiSharp.Eval/data/sparse-venv
bench/LexiSharp.Eval/data/sparse-venv/bin/pip install \
  onnxruntime==1.31.0 numpy==2.5.4 tokenizers==0.23.3
```

`transformers` is **not** installed: `AutoTokenizer.from_pretrained("naver/splade-v3")` needs the
checkpoint's tokenizer files, and that repository is gated (`gated: auto` on the Hub — the files
return "Access to model naver/splade-v3 is restricted"). Pulling `transformers` in would not make
the tokenizer available; see the next section for what replaced it.

## Fetch the model

```bash
MODEL=bench/LexiSharp.Eval/data/models/splade-v3-onnx
mkdir -p "$MODEL"
for file in config.json splade-v3.onnx splade-v3-vocab.txt; do
  curl -sL -o "$MODEL/$file" "https://huggingface.co/castorini/splade-v3-onnx/resolve/main/$file"
done
```

`splade-v3-8bit.onnx` is also published (133 941 688 bytes, dynamically quantised int8 weights).
The rows this harness measured use the full-precision file; the encoder takes `--model-file` if you
want the quantised one, and the manifest records which was used, so a row cannot claim the wrong
one. **The two exports do not produce the same weights**: dynamic int8 quantisation changes the
arithmetic of every linear layer. Measured on the 300 SciFact queries, the two exports' sparse
vectors have a mean cosine of 0.9038 (minimum 0.7620), a mean maximum per-query weight difference of
1.03, and the quantised export activates 13.5 more terms per query on average. No metric was
measured on an 8-bit row; a row on that file would be a different retrieval model, and it prints
`quantised: true` so a reader can see which one it is.

## Tokenizer

The checkpoint's own `tokenizer.json` / `vocab.txt` are behind the gated repository, so the
tokenizer is **assembled** from the exported model's `splade-v3-vocab.txt` with `tokenizers`'
`BertWordPieceTokenizer(vocab, lowercase=True, strip_accents=True, wordpieces_prefix="##")` — the
same normalizer and pre-tokenizer chain as the uncased BERT tokenizer `naver/splade-v3` was trained
with (lowercase, accent stripping, punctuation splitting, WordPiece).

That is a reconstruction, and it carries the risk a reconstruction always carries: a checkpoint whose
tokenizer differed in a normalization rule would be fed tokens it was not trained on, silently. Two
checks bound the risk here:

- the vocabulary is byte-identical (sha256 `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`)
  to the `vocab.txt` redistributed with the checkpoint in a public ONNX copy (`MrVolts/splade-v3-onnx`);
- the assembled tokenizer was compared against that copy's `tokenizer.json` on every document and
  query of both corpora — 13 162 texts — and produced **the same token ids on all of them**.

What is not established is that the redistributed `tokenizer.json` is the checkpoint's own; it is a
third party's copy of a gated file.

## Encode

The harness reads its corpora from `bench/LexiSharp.Eval/data/<corpus>/`, downloaded and
md5-verified on the first harness run. Encode both roles per corpus:

```bash
for corpus in nfcorpus scifact arguana; do
  bench/LexiSharp.Eval/data/sparse-venv/bin/python bench/LexiSharp.Eval/sparse/encode_splade.py \
    --model-dir bench/LexiSharp.Eval/data/models/splade-v3-onnx \
    --corpus bench/LexiSharp.Eval/data/$corpus/corpus.jsonl \
    --queries bench/LexiSharp.Eval/data/$corpus/queries.jsonl \
    --out bench/LexiSharp.Eval/data/sparse-vectors \
    --name $corpus \
    --truncate 256 --top-k 256 --threads 6
done
```

`--threads 6` because ONNX Runtime was measured faster at 6 intra-op threads than at 12 on the
12-core host these vectors were produced on (per-document mean over twelve 256-token NFCorpus
documents: 401 ms at 6 threads, 545 ms at 12). Two encoders run side by side, one per corpus, at 6
threads each.

## Output

One line per text, tab-separated, the digest of the **exact UTF-8 bytes** first:

```
20d51c2c…b4724	stat:2.98895931	##hort:2.50634861	breast:2.40705681	…
```

- **Documents are encoded as `title + "\n" + text`** when the BEIR record has a title, and as `text`
  otherwise. `SparseVectorSet.DocumentText` applies the same rule on the C# side; the sparse engine
  has no field concept, so no title weighting exists in a sparse row. The manifest records the rule.
- **Queries are encoded as their text verbatim.** SPLADE-v3 is symmetric: the same graph and no
  query/document prefix.
- **Weights are written with 9 significant digits**, which round-trips a `float32` exactly, and read
  back with `float.Parse(…, CultureInfo.InvariantCulture)`.
- **Terms are sorted by descending weight, then by vocabulary id**, so the surviving set after a
  top-k cut is the one a reader recomputing it would keep. The top-256 budget binds: on a
  30-document NFCorpus sample the export emitted a median of 254 non-zero terms and a maximum of 339,
  and 13 of the 30 documents were cut.
- Identical text is a legitimate duplicate — 40 NFCorpus document pairs share a title and a body —
  and the loader accepts a repeated digest only when it carries the same vector.
- A text that activates no term **fails the encode** rather than writing an empty vector: an
  unsearchable document would show up only as a lower metric.

`<name>.meta.json` beside the two files records the model, its sha256, whether it was the quantised
export, the truncation, the top-k, the document-text rule, the tokenizer route, the runtime versions
and the per-role counts. The harness reads the row's name from that manifest rather than from a flag,
so the printed model/truncation/top-k cannot disagree with the file it read.

The SHA-256 key is why a missing text is caught: `--sparse` refuses a corpus the vectors do not
cover, counting the documents and queries that are absent and naming one of each, instead of scoring
the subset that happens to be there. `SparseVectorProvider` refuses an individual miss the same way,
and `tests/LexiSharp.Tests/SparseVectorsTests.cs` pins the digest against a fixture produced by this
script (`tests/LexiSharp.Tests/TestData/sparse/hash-fixture.jsonl`).

## Score

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --ndcg-gain linear --sparse bench/LexiSharp.Eval/data/sparse-vectors
```

The BM25 rows printed above the sparse row come from the same run, so the comparison is between two
rankings over one corpus, one query list and one gain convention — which is what `--ndcg-gain linear`
makes comparable with a published figure.

## Measured here

The three corpora the row is measured on, encoded on 2026-10-26 with the command above, on the
12-core host this repository is developed on. NFCorpus and SciFact ran as two encoders side by side
(one per corpus, 6 ONNX Runtime threads each); ArguAna ran alone on one encoder at 6 threads:

| corpus | documents | queries | document terms (min / mean / max) | encode wall clock | vector files |
|---|---:|---:|---|---:|---|
| NFCorpus | 3 633 | 3 237 | 99 / 232.3 / 256 | 48.1 min | 16.3 MB + 1.8 MB |
| SciFact | 5 183 | 1 109 | 99 / 241.2 / 256 | 57.2 min | 24.2 MB + 2.9 MB |
| ArguAna | 8 674 | 1 406 | 33 / 247.2 / 256 | 28.7 min (one encoder, 6 threads) | 42.4 MB + 6.9 MB |

Query vectors average 25.8 terms (NFCorpus), 132.6 (SciFact) and 249.6 (ArguAna) — SciFact's
`queries.jsonl` carries long claim sentences, of which 300 have test judgements, and every ArguAna
query is a whole argument.

The measured rows, and what they do and do not establish, are on
[the evaluation page](../../docs/evaluation.md#learned-sparse-splade-through-onnx) — the only place
this repository keeps measured numbers, because that page states their limits beside them.
