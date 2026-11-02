"""A scikit-learn reference baseline on the same 20 Newsgroups split as LexiSharp.

The corpus text, labels, header handling and cross-split deduplication mirror
bench/LexiSharp.Eval/TwentyNewsgroups.cs. The tokenizer is scikit-learn's, so this is an
external reference point, not a bit-for-bit replacement for the harness's analysis.
"""

from __future__ import annotations

import hashlib
import sys
import time
import warnings
from pathlib import Path

from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.metrics import accuracy_score, f1_score
from sklearn.model_selection import train_test_split
from sklearn.svm import LinearSVC

ROOT = Path("bench/LexiSharp.Eval/data/20news-bydate")
TRAIN_DIR = ROOT / "20news-bydate-train"
TEST_DIR = ROOT / "20news-bydate-test"


def parse_message(raw: str) -> str:
    normalized = raw.replace("\r\n", "\n")
    separator = normalized.find("\n\n")
    headers = normalized[:separator] if separator >= 0 else normalized
    body = normalized[separator + 2 :] if separator >= 0 else ""

    subject = None
    for line in headers.split("\n"):
        if line.lower().startswith("subject:"):
            subject = line[len("subject:") :].strip()
            break

    body = body.strip()
    if not subject:
        return body
    return subject if not body else subject + "\n" + body


def read_split(root: Path) -> list[tuple[str, str]]:
    documents: list[tuple[str, str]] = []

    for category_dir in sorted(path for path in root.iterdir() if path.is_dir()):
        category = category_dir.name
        for path in sorted(path for path in category_dir.iterdir() if path.is_file()):
            text = parse_message(path.read_text(encoding="utf-8", errors="replace"))
            if text:
                documents.append((category, text))

    return documents


def deduplicate(
    documents: list[tuple[str, str]], seen: set[str]
) -> list[tuple[str, str]]:
    unique: list[tuple[str, str]] = []
    for category, text in documents:
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
        if digest not in seen:
            seen.add(digest)
            unique.append((category, text))
    return unique


def main() -> int:
    warnings.filterwarnings("ignore")

    started = time.perf_counter()
    seen: set[str] = set()
    train = deduplicate(read_split(TRAIN_DIR), seen)
    test = deduplicate(read_split(TEST_DIR), seen)

    print(f"train={len(train)} test={len(test)} categories={len({c for c, _ in train})}")
    if (len(train), len(test)) != (11300, 7525):
        print("WARNING: split sizes differ from the harness", file=sys.stderr)

    x_train = [text for _, text in train]
    y_train = [category for category, _ in train]
    x_test = [text for _, text in test]
    y_test = [category for category, _ in test]

    x_fit, x_validation, y_fit, y_validation = train_test_split(
        x_train,
        y_train,
        test_size=0.15,
        random_state=7,
        stratify=y_train,
    )

    candidates: list[tuple[float, float, tuple[int, int], str | None]] = []
    for ngram in ((1, 1), (1, 2)):
        for stop_words in (None, "english"):
            vectorizer = TfidfVectorizer(
                sublinear_tf=True,
                min_df=2,
                ngram_range=ngram,
                stop_words=stop_words,
            )
            fit_matrix = vectorizer.fit_transform(x_fit)
            validation_matrix = vectorizer.transform(x_validation)

            for c in (0.1, 0.5, 1.0, 5.0):
                classifier = LinearSVC(C=c, max_iter=5_000)
                classifier.fit(fit_matrix, y_fit)
                accuracy = accuracy_score(y_validation, classifier.predict(validation_matrix))
                candidates.append((accuracy, c, ngram, stop_words))
                print(
                    f"  C={c:<4} ngram={ngram} stop={stop_words!s:<7} "
                    f"validation accuracy={accuracy:.4f}"
                )

    best_accuracy, best_c, best_ngram, best_stop = max(
        candidates, key=lambda candidate: (candidate[0], -candidate[1])
    )

    vectorizer = TfidfVectorizer(
        sublinear_tf=True,
        min_df=2,
        ngram_range=best_ngram,
        stop_words=best_stop,
    )
    train_matrix = vectorizer.fit_transform(x_train)
    test_matrix = vectorizer.transform(x_test)

    classifier = LinearSVC(C=best_c, max_iter=5_000)
    classifier.fit(train_matrix, y_train)
    predictions = classifier.predict(test_matrix)

    accuracy = accuracy_score(y_test, predictions)
    macro_f1 = f1_score(y_test, predictions, average="macro")

    print()
    print(
        f"selected: C={best_c} ngram={best_ngram} stop={best_stop} "
        f"(validation accuracy={best_accuracy:.4f})"
    )
    print(f"test accuracy={accuracy:.4f} macro-F1={macro_f1:.4f}")
    print(f"elapsed={time.perf_counter() - started:.1f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
