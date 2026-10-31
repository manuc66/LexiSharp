"""Trains the tiny SentencePiece model the tests load.

It is trained on a handful of sentences, so its vocabulary is nonsense outside those words; the
point is to exercise the real SentencePiece parser and the id-mapping seam, not language quality.

    python -m venv .venv && .venv/bin/pip install sentencepiece
    .venv/bin/python generate_sentencepiece.py
"""

import sentencepiece as spm

CORPUS = """hello world
search engine
dense retrieval
lexical search
rank fusion
document query
semantic search
hello search
world document
query engine
"""

with open("tiny-sp.txt", "w", encoding="utf-8") as handle:
    handle.write(CORPUS)

spm.SentencePieceTrainer.Train(
    input="tiny-sp.txt",
    model_prefix="tiny-sp",
    vocab_size=32,
    model_type="unigram",
    character_coverage=1.0,
    normalization_rule_name="identity",
    pad_id=0,
    unk_id=1,
    bos_id=2,
    eos_id=3,
)

print("wrote tiny-sp.model and tiny-sp.vocab")
