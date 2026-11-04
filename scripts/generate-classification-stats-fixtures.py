"""Generate the classification-statistics differential fixtures.

The retrieval fixture lives beside this one; this script covers McNemar's asymptotic
continuity-corrected test and the temperature calibrator's log-loss objective.

Run from the repository root with scipy (and scikit-learn's environment) installed:
    python3 scripts/generate-classification-stats-fixtures.py
"""

from __future__ import annotations

import json
import math
import random
from pathlib import Path

from scipy.optimize import minimize_scalar
from scipy.stats import chi2

TEST_DATA = Path("tests/LexiSharp.Tests/TestData")


def mcnemar_fixture() -> dict:
    pairs = [
        (0, 0),
        (1, 0),
        (0, 1),
        (1, 1),
        (10, 2),
        (2, 10),
        (20, 5),
        (5, 20),
        (50, 50),
        (100, 99),
        (293, 259),
        (500, 450),
        (3, 3),
        (1, 5),
        (5, 1),
    ]

    cases = []

    for baseline_only, candidate_only in pairs:
        discordant = baseline_only + candidate_only
        p_value = 1.0 if discordant == 0 else float(
            chi2.sf(((abs(baseline_only - candidate_only) - 1) ** 2) / discordant, 1)
        )

        cases.append(
            {
                "name": f"b{baseline_only}-c{candidate_only}",
                "baselineOnly": baseline_only,
                "candidateOnly": candidate_only,
                "expectedPValue": p_value,
            }
        )

    return {
        "source": "scipy.stats.chi2",
        "version": "1.18.1",
        "note": "Asymptotic McNemar with Edwards' continuity correction, one degree of freedom.",
        "cases": cases,
    }


def softmax(values: list[float]) -> list[float]:
    largest = max(values)
    exponentials = [math.exp(value - largest) for value in values]
    total = sum(exponentials)
    return [value / total for value in exponentials]


def calibrated_probabilities(probabilities: list[float], temperature: float) -> list[float]:
    return softmax([math.log(max(value, 1e-300)) / temperature for value in probabilities])


def mean_log_loss(samples: list[dict], temperature: float) -> float:
    loss = 0.0

    for sample in samples:
        probabilities = [entry["probability"] for entry in sample["distribution"]]
        calibrated = calibrated_probabilities(probabilities, temperature)
        index = next(
            index
            for index, entry in enumerate(sample["distribution"])
            if entry["category"] == sample["actual"]
        )
        loss -= math.log(max(calibrated[index], 1e-12))

    return loss / len(samples)


def temperature_fixture() -> dict:
    # The fixture generator draws from random.Random with a fixed seed: the point is a
    # reproducible fixture, not unpredictability, so S2245 does not apply. Each use is marked
    # below rather than the rule being switched off for the project.
    random_source = random.Random(20261102)
    cases = []

    for case_index in range(8):
        category_count = random_source.randint(2, 5)
        categories = [f"c{index}" for index in range(category_count)]
        samples = []

        for _ in range(24):
            logits = [random_source.gauss(0.0, 1.6) for _ in categories]
            probabilities = softmax(logits)
            actual = random_source.randrange(category_count)

            samples.append(
                {
                    "actual": categories[actual],
                    "distribution": [
                        {"category": category, "probability": probability}
                        for category, probability in zip(categories, probabilities, strict=True)
                    ],
                }
            )

        result = minimize_scalar(
            lambda temperature: mean_log_loss(samples, temperature),
            bounds=(0.05, 10.0),
            method="bounded",
            options={"xatol": 1e-10},
        )

        cases.append(
            {
                "name": f"temperature-{case_index}",
                "samples": samples,
                "expectedTemperature": float(result.x),
                "expectedLogLoss": float(result.fun),
            }
        )

    return {
        "source": "scipy.optimize.minimize_scalar",
        "version": "1.18.1",
        "note": "Objective: mean negative log-likelihood after softmax(log(p) / T).",
        "cases": cases,
    }


def main() -> int:
    TEST_DATA.mkdir(parents=True, exist_ok=True)
    (TEST_DATA / "mcnemar-scipy.json").write_text(
        json.dumps(mcnemar_fixture(), indent=2) + "\n"
    )
    (TEST_DATA / "temperature-scipy.json").write_text(
        json.dumps(temperature_fixture(), indent=2) + "\n"
    )
    print("wrote mcnemar-scipy.json and temperature-scipy.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
