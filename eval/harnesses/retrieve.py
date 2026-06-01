"""
Step 1 of 2: Retrieve contexts from ChefAgent API for each golden dataset question.
Run this locally, then upload the output to Colab for scoring.

Usage:
    python eval/harnesses/retrieve.py                  # full 100 questions
    python eval/harnesses/retrieve.py --limit 25       # subset
"""

import json
import argparse
import requests
from datetime import datetime

API_URL = "http://localhost:5100/recipes/search"
GOLDEN_DATASET_PATH = "eval/datasets/golden_dataset.json"
OUTPUT_PATH = "eval/datasets/retrieved_contexts.json"


def build_context(recipe: dict) -> str:
    title = recipe.get("title", "")
    ingredients = ", ".join(recipe.get("ingredients", []))
    directions = " ".join(recipe.get("directions", []))
    return f"Title: {title}\nIngredients: {ingredients}\nDirections: {directions}"


def build_answer(contexts: list[str]) -> str:
    if not contexts:
        return "No recipes found."
    return contexts[0].split("\n")[0].replace("Title: ", "")


def search_recipes(question: str, max_results: int = 5) -> list[str]:
    try:
        response = requests.post(
            API_URL,
            json={"query": question, "maxResults": max_results},
            timeout=30,
        )
        response.raise_for_status()
        recipes = response.json().get("recipes", [])
        return [build_context(r) for r in recipes]
    except Exception as e:
        print(f"  [WARN] Search failed for '{question}': {e}")
        return []


def run_retrieval(limit: int | None = None):
    print(f"\n{'='*60}")
    print("ChefAgent — Retrieval Step")
    print(f"{'='*60}")

    with open(GOLDEN_DATASET_PATH) as f:
        golden = json.load(f)

    if limit:
        categories = {}
        for entry in golden:
            cat = entry["category"]
            if cat not in categories:
                categories[cat] = []
            categories[cat].append(entry)

        sampled = []
        per_category = max(1, limit // len(categories))
        for cat, entries in categories.items():
            sampled.extend(entries[:per_category])
        golden = sampled[:limit]

    print(f"Retrieving contexts for {len(golden)} questions...")

    records = []
    for i, entry in enumerate(golden):
        question = entry["question"]
        print(f"  [{i+1}/{len(golden)}] {question[:60]}")
        retrieved = search_recipes(question)

        records.append({
            "question": question,
            "ground_truth": entry["ground_truth"],
            "category": entry["category"],
            "answer": build_answer(retrieved),
            "contexts": retrieved if retrieved else ["No results found."],
        })

    output = {
        "timestamp": datetime.utcnow().isoformat(),
        "question_count": len(records),
        "records": records,
    }

    with open(OUTPUT_PATH, "w") as f:
        json.dump(output, f, indent=2)

    print(f"\nDone. Saved to {OUTPUT_PATH}")
    print(f"Upload this file to Colab for scoring.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, help="Limit number of questions")
    args = parser.parse_args()
    run_retrieval(limit=args.limit)
