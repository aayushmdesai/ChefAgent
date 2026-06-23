"""
Step 1 of 2: Retrieve contexts from ChefAgent /chat for each golden question.
Resumable: re-running only re-fetches questions whose contexts are empty.

Usage:
    python eval/harnesses/retrieve.py
    python eval/harnesses/retrieve.py --limit 25
"""

import json
import argparse
import os
import time
import requests
from datetime import datetime, timezone

API_URL = "https://chefagent-production.up.railway.app/chat"
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


def search_recipes(question: str, session_id: str, _retried: bool = False) -> tuple[list[str], str]:
    try:
        response = requests.post(
            API_URL,
            json={"message": question, "sessionId": session_id},
            timeout=180,
        )
        if response.status_code == 429 and not _retried:
            wait = int(response.headers.get("Retry-After", 20))
            print(f"    429 — waiting {wait}s")
            time.sleep(wait)
            return search_recipes(question, session_id, _retried=True)
        response.raise_for_status()
        body = response.json()
        recipes = body.get("recipes", [])
        contexts = [build_context(r.get("recipe", {})) for r in recipes]
        answer = body.get("message", "") or build_answer(contexts)
        return contexts, answer
    except requests.exceptions.Timeout:
        if not _retried:
            print("    timeout — waiting 20s, retrying once")
            time.sleep(20)
            return search_recipes(question, session_id, _retried=True)
        print(f"  [WARN] Timeout (retry failed): '{question}'")
        return [], ""
    except Exception as e:
        print(f"  [WARN] Search failed for '{question}': {e}")
        return [], ""


def load_existing() -> dict:
    """Map question -> record, but only for records that already succeeded."""
    if not os.path.exists(OUTPUT_PATH):
        return {}
    with open(OUTPUT_PATH) as f:
        data = json.load(f)
    good = {}
    for rec in data.get("records", []):
        if rec.get("contexts") and rec["contexts"] != ["No results found."]:
            good[rec["question"]] = rec
    return good


def run_retrieval(limit: int | None = None):
    print(f"\n{'='*60}")
    print("ChefAgent — Retrieval Step")
    print(f"{'='*60}")

    with open(GOLDEN_DATASET_PATH) as f:
        golden = json.load(f)

    if limit:
        categories = {}
        for entry in golden:
            categories.setdefault(entry["category"], []).append(entry)
        sampled = []
        per_category = max(1, limit // len(categories))
        for entries in categories.values():
            sampled.extend(entries[:per_category])
        golden = sampled[:limit]

    existing = load_existing()
    print(f"Retrieving contexts for {len(golden)} questions "
          f"({len(existing)} already cached, will skip)...")

    records = []
    for i, entry in enumerate(golden):
        question = entry["question"]

        if question in existing:
            print(f"  [{i+1}/{len(golden)}] (cached) {question[:55]}")
            records.append(existing[question])
            continue

        print(f"  [{i+1}/{len(golden)}] {question[:55]}")
        contexts, answer = search_recipes(question, session_id=f"ragas-{i}")
        records.append({
            "question": question,
            "ground_truth": entry["ground_truth"],
            "category": entry["category"],
            "answer": answer,
            "contexts": contexts if contexts else ["No results found."],
        })
        time.sleep(21)  # pace only on live calls, ~3 RPM

    output = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "question_count": len(records),
        "records": records,
    }
    with open(OUTPUT_PATH, "w") as f:
        json.dump(output, f, indent=2)

    failed = sum(1 for r in records if r["contexts"] == ["No results found."])
    print(f"\nDone. Saved to {OUTPUT_PATH}")
    print(f"  {len(records) - failed}/{len(records)} succeeded, {failed} still empty.")
    if failed:
        print("  Re-run to retry the empty ones.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, help="Limit number of questions")
    args = parser.parse_args()
    run_retrieval(limit=args.limit)