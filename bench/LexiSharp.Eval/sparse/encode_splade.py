#!/usr/bin/env python3
"""Precompute SPLADE-v3 sparse vectors for a BEIR corpus, for the evaluation harness.

The library never runs a model. This script is the producer: it runs a SPLADE ONNX export
once over a corpus and its queries, writes one line per text, and `LexiSharp.Eval --sparse`
consumes those lines. Nothing here is referenced by LexiSharp itself.

Output, one file per role, one line per text:

    sha256(text) \t token:weight \t token:weight \t ...

`sha256` is the lowercase hex digest of the exact UTF-8 bytes of the *encoded* text, which is
what the harness recomputes with `SHA256.HashData(Encoding.UTF8.GetBytes(text))`. Keying on text
rather than on document id is deliberate: an id can be re-pointed at different text, a digest
cannot, and a missing digest is a refused lookup rather than a silently wrong vector.

The document text is `title + "\n" + text` when the BEIR record has a title, and `text`
otherwise. This is the same rule the harness applies, and it is what `DocumentText` says in the
manifest — a corpus whose titles were dropped on one side would otherwise look like a model
result rather than an encoding difference.

The model does the pooling itself: the export's graph computes
`max over tokens of log1p(relu(logits))`, masked by the attention mask, and returns only the
non-zero terms as `output_idx` / `output_weights`. This script does not re-do that pooling —
it applies truncation before inference and one optional pruning step after it.

Exit status is non-zero on any text that produced no terms, because a document with an empty
vector is silently unsearchable and its absence would show up only as a lower metric.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time
from typing import Iterable

import numpy as np
import onnxruntime as ort
from tokenizers import BertWordPieceTokenizer


def document_text(record: dict) -> str:
    """The text a document is encoded from. Must match `SparseVectorSet.DocumentText` in C#."""
    title = record.get("title") or ""
    body = record.get("text") or ""
    return f"{title}\n{body}" if title else body


def digest(text: str) -> str:
    """sha256 of the exact UTF-8 bytes, lowercase hex — the key the harness looks up."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def read_jsonl(path: str) -> list[dict]:
    with open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


class SpladeEncoder:
    """A SPLADE ONNX export plus the tokenizer assembled from the checkpoint's vocabulary."""

    def __init__(self, model_dir: str, model_file: str, vocab_file: str,
                 truncate: int, threads: int) -> None:
        model_path = os.path.join(model_dir, model_file)
        vocab_path = os.path.join(model_dir, vocab_file)

        if not os.path.isfile(model_path):
            raise SystemExit(f"model not found: {model_path}")
        if not os.path.isfile(vocab_path):
            raise SystemExit(f"vocabulary not found: {vocab_path}")

        # BertWordPieceTokenizer builds the same normalizer and pre-tokenizer as the uncased BERT
        # tokenizer the checkpoint was trained with: lowercase, accent stripping, whitespace and
        # punctuation pre-tokenization, WordPiece with `##`. It is assembled from the published
        # vocabulary because the checkpoint's own tokenizer files are behind a gated repository.
        self.tokenizer = BertWordPieceTokenizer(
            vocab_path, lowercase=True, strip_accents=True, wordpieces_prefix="##")
        self.tokenizer.enable_truncation(max_length=truncate, strategy="longest_first")

        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        self.session = ort.InferenceSession(
            model_path, options, providers=["CPUExecutionProvider"])
        self.model_path = model_path
        self.truncate = truncate

    def encode(self, text: str) -> list[tuple[int, float]]:
        encoding = self.tokenizer.encode(text)
        ids = np.array([encoding.ids], dtype=np.int64)
        feeds = {
            "input_ids": ids,
            "attention_mask": np.ones_like(ids),
            "token_type_ids": np.zeros_like(ids),
        }
        names = {i.name for i in self.session.get_inputs()}
        outputs = self.session.run(None, {k: v for k, v in feeds.items() if k in names})

        indices = np.asarray(outputs[0]).ravel()
        weights = np.asarray(outputs[1]).ravel()
        if indices.size != weights.size:
            raise SystemExit(
                f"model returned {indices.size} indices and {weights.size} weights; "
                "the export is not the index/weight pair this script expects")

        return [(int(i), float(w)) for i, w in zip(indices, weights) if w > 0.0]


def write_vectors(path: str, encoder: SpladeEncoder, texts: Iterable[str], what: str,
                  top_k: int) -> dict:
    """Encode `texts` and write one line per text; returns the statistics the manifest records."""
    count = 0
    total_terms = 0
    min_terms = None
    max_terms = 0
    empty: list[str] = []
    started = time.time()

    temp_path = path + ".part"

    with open(temp_path, "w", encoding="utf-8", newline="\n") as out:
        for text in texts:
            terms = encoder.encode(text)

            if not terms:
                # A document that activates nothing is indexed as a document with no postings:
                # it can never be retrieved, and the metric would read lower for a reason the
                # harness cannot see. Stopping here names the text that did it.
                empty.append(digest(text))
                continue

            # Descending weight, ties by vocabulary id: deterministic output, and the surviving
            # set after a top-k cut is the same one a reader recomputing it would keep.
            terms.sort(key=lambda pair: (-pair[1], pair[0]))
            kept = terms if top_k <= 0 else terms[:top_k]

            out.write(digest(text))
            for term_id, weight in kept:
                out.write(f"\t{encoder.tokenizer.id_to_token(term_id)}:{weight:.9g}")
            out.write("\n")

            count += 1
            total_terms += len(kept)
            min_terms = len(kept) if min_terms is None else min(min_terms, len(kept))
            max_terms = max(max_terms, len(kept))

            if count % 100 == 0:
                elapsed = time.time() - started
                rate = count / elapsed
                print(
                    f"  {what}: {count} texts, {rate:.2f}/s, "
                    f"{elapsed / 60:.1f} min elapsed",
                    file=sys.stderr, flush=True)

    if empty:
        os.remove(temp_path)
        raise SystemExit(
            f"{len(empty)} {what} text(s) produced no terms, first {empty[0]}: the encoder "
            "refuses rather than write a vector that cannot be retrieved")

    # Renamed only when complete: an interrupted run leaves no file that looks finished.
    os.replace(temp_path, path)

    return {
        "texts": count,
        "terms_min": min_terms or 0,
        "terms_mean": (total_terms / count) if count else 0.0,
        "terms_max": max_terms,
        "seconds": round(time.time() - started, 1),
    }


def file_sha256(path: str) -> str:
    hash = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            hash.update(block)
    return hash.hexdigest()


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model-dir", required=True,
                        help="directory holding the ONNX export and its vocabulary")
    parser.add_argument("--model-file", default="splade-v3.onnx",
                        help="ONNX file inside --model-dir (default: splade-v3.onnx, the "
                             "full-precision export; splade-v3-8bit.onnx is the quantised one)")
    parser.add_argument("--vocab-file", default="splade-v3-vocab.txt")
    parser.add_argument("--corpus", required=True, help="the BEIR corpus.jsonl")
    parser.add_argument("--queries", required=True, help="the BEIR queries.jsonl")
    parser.add_argument("--out", required=True, help="output directory")
    parser.add_argument("--name", required=True, help="corpus name, e.g. nfcorpus")
    parser.add_argument("--truncate", type=int, default=256,
                        help="tokens per text after truncation (default 256, the model card's "
                             "evaluation setting; 512 is the checkpoint's maximum)")
    parser.add_argument("--top-k", type=int, default=256,
                        help="terms kept per text (default 256; 0 keeps every non-zero term)")
    parser.add_argument("--threads", type=int, default=6,
                        help="ONNX Runtime intra-op threads (default 6: measured faster than 12 "
                             "on the 12-core host used for these vectors)")
    parser.add_argument("--limit", type=int, default=0,
                        help="encode only the first N documents and queries (smoke tests)")
    parser.add_argument("--model-name", default="SPLADE-v3")
    parser.add_argument("--base-model", default="naver/splade-v3")
    parser.add_argument("--license", default="CC BY-NC-SA 4.0")

    args = parser.parse_args(argv)

    documents = read_jsonl(args.corpus)
    queries = read_jsonl(args.queries)

    if args.limit > 0:
        documents = documents[:args.limit]
        queries = queries[:args.limit]

    if args.truncate <= 0:
        raise SystemExit("--truncate must be positive")

    os.makedirs(args.out, exist_ok=True)

    encoder = SpladeEncoder(args.model_dir, args.model_file, args.vocab_file,
                            args.truncate, args.threads)

    quantised = "8bit" in os.path.basename(args.model_file).lower()

    print(f"model:    {os.path.abspath(encoder.model_path)} "
          f"({os.path.getsize(encoder.model_path) / 1e6:.0f} MB"
          f"{', quantised' if quantised else ''})", file=sys.stderr)
    print(f"texts:    {len(documents)} documents, {len(queries)} queries",
          file=sys.stderr)
    print(f"encoding: truncate {args.truncate}, top-k {args.top_k or 'all non-zero'}",
          file=sys.stderr, flush=True)

    docs_path = os.path.join(args.out, f"{args.name}.docs.tsv")
    queries_path = os.path.join(args.out, f"{args.name}.queries.tsv")

    started = time.time()

    doc_stats = write_vectors(
        docs_path, encoder, (document_text(d) for d in documents), "documents", args.top_k)
    query_stats = write_vectors(
        queries_path, encoder, (q["text"] for q in queries), "queries", args.top_k)

    elapsed = time.time() - started

    manifest = {
        "model": args.model_name,
        "base_model": args.base_model,
        "license": args.license,
        "model_file": os.path.basename(args.model_file),
        "model_bytes": os.path.getsize(encoder.model_path),
        "model_sha256": file_sha256(encoder.model_path),
        "quantised": quantised,
        "truncate": args.truncate,
        "top_k": args.top_k,
        "document_text": "title + \\n + text when the record has a title, else text",
        "tokenizer": (
            "tokenizers BertWordPieceTokenizer assembled from the checkpoint vocabulary "
            "(lowercase, accent stripping); the checkpoint's own tokenizer files are gated"),
        "runtime": f"onnxruntime {ort.__version__}, numpy {np.__version__}",
        "encoded": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "documents": doc_stats,
        "queries": query_stats,
        "seconds": round(elapsed, 1),
        "bytes": {
            "documents": os.path.getsize(docs_path),
            "queries": os.path.getsize(queries_path),
        },
    }

    meta_path = os.path.join(args.out, f"{args.name}.meta.json")
    with open(meta_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2, sort_keys=True)
        handle.write("\n")

    print(
        f"done: {doc_stats['texts']} documents "
        f"(terms {doc_stats['terms_min']}-{doc_stats['terms_max']}, "
        f"mean {doc_stats['terms_mean']:.1f}) and {query_stats['texts']} queries in "
        f"{elapsed / 60:.1f} min; "
        f"{manifest['bytes']['documents'] / 1e6:.1f} MB + "
        f"{manifest['bytes']['queries'] / 1e6:.1f} MB, wrote {meta_path}",
        file=sys.stderr)

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
