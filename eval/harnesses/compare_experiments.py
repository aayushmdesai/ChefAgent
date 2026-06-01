"""
Compare two eval experiment result files and show score deltas.

Usage:
    python eval/harnesses/compare_experiments.py \
        eval/experiments/2026-06-01_baseline.json \
        eval/experiments/2026-06-07_spell_check.json
"""

import json
import sys


def load(path: str) -> dict:
    with open(path) as f:
        return json.load(f)


def compare(baseline_path: str, experiment_path: str):
    base = load(baseline_path)
    exp = load(experiment_path)

    print(f"\n{'='*70}")
    print("ChefAgent — Experiment Comparison")
    print(f"{'='*70}")
    print(f"Baseline   : {baseline_path}")
    print(f"Experiment : {experiment_path}")
    print(f"Questions  : {base['question_count']} → {exp['question_count']}")

    print(f"\n{'─'*70}")
    print(f"{'OVERALL':<25} {'Baseline':>10} {'Experiment':>12} {'Delta':>8}")
    print(f"{'─'*70}")
    for metric in ["context_relevance", "faithfulness", "answer_relevancy"]:
        b = base["overall"].get(metric, 0)
        e = exp["overall"].get(metric, 0)
        delta = e - b
        arrow = "↑" if delta > 0.01 else ("↓" if delta < -0.01 else "→")
        print(f"  {metric:<23} {b:>10.3f} {e:>12.3f} {arrow} {delta:>+.3f}")

    print(f"\n{'─'*70}")
    print(f"{'CONTEXT RELEVANCE BY CATEGORY':<25} {'Baseline':>10} {'Experiment':>12} {'Delta':>8}")
    print(f"{'─'*70}")

    all_cats = sorted(set(
        list(base.get("per_category", {}).keys()) +
        list(exp.get("per_category", {}).keys())
    ))

    for cat in all_cats:
        b_val = base.get("per_category", {}).get(
            cat, {}).get("context_relevance", 0)
        e_val = exp.get("per_category", {}).get(
            cat, {}).get("context_relevance", 0)
        delta = e_val - b_val
        arrow = "↑" if delta > 0.01 else ("↓" if delta < -0.01 else "→")
        print(f"  {cat:<23} {b_val:>10.3f} {e_val:>12.3f} {arrow} {delta:>+.3f}")

    print(f"\n{'─'*70}")
    print(f"{'ANSWER RELEVANCY BY CATEGORY':<25} {'Baseline':>10} {'Experiment':>12} {'Delta':>8}")
    print(f"{'─'*70}")

    for cat in all_cats:
        b_val = base.get("per_category", {}).get(
            cat, {}).get("answer_relevancy", 0)
        e_val = exp.get("per_category", {}).get(
            cat, {}).get("answer_relevancy", 0)
        delta = e_val - b_val
        arrow = "↑" if delta > 0.01 else ("↓" if delta < -0.01 else "→")
        print(f"  {cat:<23} {b_val:>10.3f} {e_val:>12.3f} {arrow} {delta:>+.3f}")

    print()


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: compare_experiments.py <baseline.json> <experiment.json>")
        sys.exit(1)
    compare(sys.argv[1], sys.argv[2])
