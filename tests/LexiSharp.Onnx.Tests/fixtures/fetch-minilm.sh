#!/usr/bin/env bash
# Fetches a real, permissively licensed encoder for the opt-in compatibility tests.
#
#   tests/LexiSharp.Onnx.Tests/fixtures/fetch-minilm.sh [target-dir]
#   LEXISHARP_ONNX_MODEL_DIR=target-dir dotnet test tests/LexiSharp.Onnx.Tests
#
# Xenova/all-MiniLM-L6-v2 is the ONNX export of sentence-transformers/all-MiniLM-L6-v2
# (Apache-2.0). Nothing here is redistributed by this repository; the download is the caller's.
set -euo pipefail

target="${1:-.model-cache/minilm}"
base="https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main"

mkdir -p "$target"
curl -fL --retry 3 "$base/onnx/model.onnx" -o "$target/model.onnx"
curl -fL --retry 3 "$base/vocab.txt" -o "$target/vocab.txt"

echo "model and vocabulary written to $target"
