"""Generate the browser demo's precomputed document vectors.

The WASM demo's "similar documents" feature needs one vector per corpus document,
but no model runs in the browser: the vectors are computed here, once, and the
page only loads them and compares them with a cosine.

Run from the repository root in an environment with fastembed installed:

    python3 scripts/generate-embeddings.py

`BAAI/bge-small-en-v1.5` is MIT-licensed, 384-dimensional and English-only,
which matches a hand-written English corpus. The model itself is never
committed: the output is numbers plus the metadata needed to reproduce them,
and that metadata is what the page shows next to the feature.
"""

from __future__ import annotations

import json
from pathlib import Path

import fastembed
import numpy as np
from fastembed import TextEmbedding

MODEL = "BAAI/bge-small-en-v1.5"
DIMENSION = 384
CORPUS = Path("samples/LexiSharp.WasmDemo/wwwroot/corpus.json")
OUTPUT = Path("samples/LexiSharp.WasmDemo/wwwroot/embeddings.json")


def main() -> int:
    documents = json.loads(CORPUS.read_text(encoding="utf-8"))
    if not documents:
        print(f"{CORPUS} is empty; nothing to embed.")
        return 1

    model = TextEmbedding(model_name=MODEL)
    vectors = list(model.passage_embed([document["text"] for document in documents]))

    payload = {
        "model": MODEL,
        # The ONNX build fastembed fetches for this model, named so the file can be audited
        # without guessing which conversion produced the numbers.
        "source": "Qdrant/bge-small-en-v1.5-onnx-Q",
        "dimension": DIMENSION,
        "normalized": True,
        "generator": f"fastembed {fastembed.__version__} (passage_embed)",
        "documents": {},
    }

    for document, vector in zip(documents, vectors, strict=True):
        values = np.asarray(vector, dtype=np.float32)
        if values.shape != (DIMENSION,):
            raise SystemExit(f"{document['id']}: expected {DIMENSION} values, got {values.shape}.")

        values = values / np.linalg.norm(values)
        payload["documents"][document["id"]] = [round(float(value), 6) for value in values]

    OUTPUT.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(f"{len(documents)} documents x {DIMENSION} values -> {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
