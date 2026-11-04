"""Generate the retrieval-metric differential fixture from ir_measures.

Run from the repository root with ir_measures installed:
    python3 scripts/generate-retrieval-metrics-fixture.py
"""

from __future__ import annotations

import json
import random
from pathlib import Path

import ir_measures
from ir_measures import AP, P, RR, Recall, nDCG

OUTPUT = Path("tests/LexiSharp.Tests/TestData/retrieval-metrics-ir-measures.json")
GAINS = {0: 0, 1: 1, 2: 3, 3: 7}  # 2^rel - 1 for grades 0..3


def measure_value(measure, qrels: dict[str, dict[str, int]], run: dict[str, dict[str, float]]) -> float:
    return next(ir_measures.iter_calc([measure], qrels, run)).value


def case(name: str, qrels: dict[str, int], retrieved: list[str], k: int) -> dict:
    run = {"q": {document: float(len(retrieved) - index) for index, document in enumerate(retrieved)}}
    qrels_by_query = {"q": qrels}

    return {
        "name": name,
        "k": k,
        "qrels": qrels,
        "retrieved": retrieved,
        "expected": {
            "ndcgLinear": measure_value(nDCG(cutoff=k), qrels_by_query, run),
            "ndcgExponential": measure_value(nDCG(cutoff=k, gains=GAINS), qrels_by_query, run),
            "ap": measure_value(AP @ k, qrels_by_query, run),
            "rr": measure_value(RR @ k, qrels_by_query, run),
            "recall": measure_value(Recall @ k, qrels_by_query, run),
            "precision": measure_value(P @ k, qrels_by_query, run),
        },
    }


def main() -> int:
    cases: list[dict] = [
        case("rank-1", {"a": 1}, ["a"], 10),
        case("at-k", {"a": 1, "b": 1}, ["x", "a", "b"], 2),
        case("beyond-k", {"a": 1}, ["x", "y", "a"], 2),
        case("fewer-than-k", {"a": 1}, ["a"], 10),
        case("more-relevant-than-k", {"a": 1, "b": 1, "c": 1}, ["a", "b", "c"], 2),
        case("graded", {"a": 3, "b": 2, "c": 1}, ["b", "a", "x", "c"], 3),
        case("graded-with-zero", {"a": 3, "b": 0, "c": 1}, ["b", "a", "c"], 3),
        case("no-relevant-retrieved", {"a": 2}, ["x", "y"], 10),
        case("all-relevant", {"a": 1, "b": 1, "c": 1}, ["a", "b", "c"], 10),
        case("empty-run", {"a": 1}, [], 10),
    ]

    # The fixture generator draws from random.Random with a fixed seed: the point is a
    # reproducible fixture, not unpredictability, so S2245 does not apply. Each use is marked
    # below rather than the rule being switched off for the project.
    random_source = random.Random(20261102)
    uniform = [f"d{index:02d}" for index in range(1, 26)]

    for index in range(120):
        judged = random_source.sample(uniform, random_source.randint(5, 24))
        qrels = {
            document: random_source.choices([0, 1, 2, 3], weights=[0.50, 0.25, 0.15, 0.10])[0]
            for document in judged
        }

        if not any(grade > 0 for grade in qrels.values()):
            qrels[judged[0]] = random_source.randint(1, 3)

        retrieved = random_source.sample(judged, random_source.randint(0, min(20, len(judged))))

        for unjudged in range(random_source.randint(0, 5)):
            retrieved.append(f"u{index:03d}-{unjudged}")

        random_source.shuffle(retrieved)
        cases.append(case(f"random-{index:03d}", qrels, retrieved, random_source.choice([1, 3, 5, 10, 20])))

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(
        json.dumps(
            {
                "source": "ir_measures",
                "version": ir_measures.__version__,
                "seed": 20261102,
                "cases": cases,
            },
            indent=2,
        )
        + "\n"
    )
    print(f"wrote {len(cases)} cases to {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
