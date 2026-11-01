#!/usr/bin/env bash
# Fetches GPT-2's tokenizer.json for the opt-in BPE test.
#
#   tests/LexiSharp.Onnx.Tests/fixtures/fetch-gpt2-tokenizer.sh [target-dir]
#   LEXISHARP_ONNX_GPT2_TOKENIZER=target-dir/tokenizer.json dotnet test tests/LexiSharp.Onnx.Tests
#
# GPT-2's tokenizer files are MIT-licensed; nothing here is redistributed by this repository.
set -euo pipefail

target="${1:-.model-cache/gpt2}"
mkdir -p "$target"
curl -fL --retry 3 "https://huggingface.co/gpt2/resolve/main/tokenizer.json" -o "$target/tokenizer.json"
echo "tokenizer written to $target/tokenizer.json"
