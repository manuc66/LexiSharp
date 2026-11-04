"""Generate the isotonic/Platt calibration differential fixture.

Run from the repository root with scikit-learn installed:
    python3 scripts/generate-calibration-fixture.py
"""

from __future__ import annotations

import json
import math
import random
from pathlib import Path

import numpy as np
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression

OUTPUT = Path("tests/LexiSharp.Tests/TestData/calibration-scipy.json")


def main() -> int:
    # The fixture generator draws from random.Random with a fixed seed: the point is a
    # reproducible fixture, not unpredictability, so S2245 does not apply. Each use is marked
    # below rather than the rule being switched off for the project.
    random_source = random.Random(20261102)
    cases = []

    case_index = 0
    while len(cases) < 6:
        count = random_source.randint(40, 80)
        scores = np.array(
            [round(random_source.uniform(0.0, 10.0), 2) for _ in range(count)],
            dtype=float,
        )

        logits = 1.2 * (scores - 5.0) + np.array(
            [random_source.gauss(0.0, 1.0) for _ in range(count)]
        )
        labels = (logits > 0).astype(int)

        if labels.min() == labels.max():
            labels[0] = 1 - labels[0]

        # An unregularised logistic fit has no finite maximum on a sample that is perfectly
        # separable by score; keep only cases whose two label ranges overlap.
        negatives = scores[labels == 0]
        positives = scores[labels == 1]

        if negatives.max() < positives.min() or positives.max() < negatives.min():
            continue

        isotonic = IsotonicRegression(out_of_bounds="clip", y_min=0, y_max=1)
        isotonic.fit(scores, labels)

        platt = LogisticRegression(C=1e12, solver="lbfgs", max_iter=10_000)
        platt.fit(scores.reshape(-1, 1), labels)

        unique = sorted(set(float(score) for score in scores))
        grid = sorted(set([0.0, 10.0] + unique))

        expected_isotonic = isotonic.predict(np.array(grid))
        expected_platt = platt.predict_proba(np.array(grid).reshape(-1, 1))[:, 1]

        cases.append(
            {
                "name": f"calibration-{case_index}",
                "examples": [
                    {"score": float(score), "correct": bool(label)}
                    for score, label in zip(scores, labels, strict=True)
                ],
                "grid": grid,
                "expectedIsotonic": [float(value) for value in expected_isotonic],
                "expectedPlatt": [float(value) for value in expected_platt],
            }
        )

        case_index += 1

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(
        json.dumps(
            {
                "source": "scikit-learn",
                "version": "1.9.1",
                "note": (
                    "IsotonicRegression(out_of_bounds=clip, y_min=0, y_max=1); "
                    "LogisticRegression(C=1e12, lbfgs)."
                ),
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
