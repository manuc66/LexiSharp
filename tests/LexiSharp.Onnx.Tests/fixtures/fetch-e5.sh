#!/usr/bin/env bash
# Fetches a real multilingual E5 export for the opt-in SentencePiece test.
#
#   tests/LexiSharp.Onnx.Tests/fixtures/fetch-e5.sh [target-dir]
#   LEXISHARP_ONNX_E5_DIR=target-dir dotnet test tests/LexiSharp.Onnx.Tests
#
# Xenova/multilingual-e5-small is the ONNX export of intfloat/multilingual-e5-small (MIT).
# Nothing here is redistributed by this repository; the download is the caller's.
set -euo pipefail

target="${1:-.model-cache/e5-small}"
base="https://huggingface.co/Xenova/multilingual-e5-small/resolve/main"

mkdir -p "$target"
curl -fL --retry 3 "$base/onnx/model.onnx" -o "$target/model.onnx"
curl -fL --retry 3 "$base/sentencepiece.bpe.model" -o "$target/sentencepiece.bpe.model"
curl -fL --retry 3 "$base/tokenizer.json" -o "$target/tokenizer.json"

echo "model, SentencePiece model and tokenizer written to $target"
