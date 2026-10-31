"""Produces reference embeddings with an independent stack (HuggingFace tokenizers + ONNX Runtime).

The .NET tests compare the package's vectors to these values, so a pooling or normalization bug
cannot pass by agreeing with itself. Run it when the reference fixture needs regenerating:

    python -m venv .venv && .venv/bin/pip install onnxruntime tokenizers numpy
    .venv/bin/python generate_reference.py \
        --model /path/model.onnx --tokenizer /path/tokenizer.json \
        --text "hello world" --prefix "" --output reference-minilm.json
"""

import argparse
import json

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer


def embed(model_path, tokenizer_path, text, prefix, pooling):
    tokenizer = Tokenizer.from_file(tokenizer_path)
    encoded = tokenizer.encode(prefix + text)

    ids = np.array([encoded.ids], dtype=np.int64)
    mask = np.array([encoded.attention_mask], dtype=np.int64)
    types = np.array([encoded.type_ids], dtype=np.int64)

    session = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    inputs = {spec.name for spec in session.get_inputs()}
    feed = {"input_ids": ids, "attention_mask": mask}

    if "token_type_ids" in inputs:
        feed["token_type_ids"] = types

    hidden = session.run(None, feed)[0][0].astype(np.float32)

    if pooling == "mean":
        weights = mask[0][:, None].astype(np.float32)
        vector = (hidden * weights).sum(axis=0) / weights.sum()
    else:
        vector = hidden[0]

    vector = vector / np.linalg.norm(vector)
    return vector.astype(float).tolist()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--tokenizer", required=True)
    parser.add_argument("--text", required=True)
    parser.add_argument("--prefix", default="")
    parser.add_argument("--pooling", choices=["mean", "cls"], default="mean")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    vector = embed(args.model, args.tokenizer, args.text, args.prefix, args.pooling)

    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(
            {
                "text": args.text,
                "prefix": args.prefix,
                "pooling": args.pooling,
                "dimension": len(vector),
                "vector": vector,
            },
            handle,
        )

    print(f"wrote {args.output} ({len(vector)} dimensions)")
