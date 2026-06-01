"""
Simple RAGAS-style scorer using Ollama directly.
No RAGAS executor — just sequential LLM calls.
Scores: context_relevance, faithfulness, answer_relevancy

Usage:
    python eval/harnesses/score_simple.py --limit 25
"""

import json
import re
import argparse
import requests
from datetime import datetime

OLLAMA_URL = "http://localhost:11434/api/generate"
MODEL = "llama3.2"
CONTEXTS_PATH = "eval/datasets/retrieved_contexts.json"
RESULTS_PATH = "eval/datasets/ragas_results.json"


def ask_ollama(prompt: str) -> str:
    try:
        r = requests.post(OLLAMA_URL, json={
            "model": MODEL,
            "prompt": prompt,
            "stream": False,
        }, timeout=120)
        return r.json().get("response", "").strip()
    except Exception as e:
        print(f"    [WARN] Ollama call failed: {e}")
        return ""


def score_context_relevance(question: str, contexts: list[str]) -> float:
    """Are the retrieved contexts relevant to the question?"""
    context_text = "\n---\n".join(contexts[:3])
    prompt = f"""Rate how relevant these retrieved recipes are to the user's question.

Question: {question}

Retrieved recipes:
{context_text}

Rate from 0.0 to 1.0 where:
1.0 = all recipes are highly relevant to the question
0.5 = some recipes are relevant
0.0 = none of the recipes are relevant

Reply with ONLY a number between 0.0 and 1.0, nothing else."""

    response = ask_ollama(prompt)
    try:
        return round(min(1.0, max(0.0, float(response))), 3)
    except:
        return 0.5


def score_faithfulness(answer: str, contexts: list[str]) -> float:
    """Is the answer grounded in the retrieved contexts?"""
    context_text = "\n---\n".join(contexts[:3])
    prompt = f"""Does the answer come from the retrieved recipes, or is it made up?

Retrieved recipes:
{context_text}

Answer given: {answer}

Rate from 0.0 to 1.0 where:
1.0 = answer is fully grounded in the retrieved recipes
0.5 = answer is partially grounded
0.0 = answer is not grounded in the retrieved recipes at all

Reply with ONLY a number between 0.0 and 1.0, nothing else."""

    response = ask_ollama(prompt)
    try:
        return round(min(1.0, max(0.0, float(response))), 3)
    except:
        return 0.5


def score_answer_relevancy(question: str, answer: str, ground_truth: str) -> float:
    """Does the answer address the question compared to the ground truth?"""
    prompt = f"""Rate how well the answer addresses the user's question compared to what was expected.

Question: {question}
Expected: {ground_truth}
Actual answer: {answer}

Rate from 0.0 to 1.0 where:
1.0 = answer perfectly addresses the question
0.5 = answer partially addresses the question  
0.0 = answer does not address the question at all

Reply with ONLY a number between 0.0 and 1.0, nothing else."""

    response = ask_ollama(prompt)
    try:
        return round(min(1.0, max(0.0, float(response))), 3)
    except:
        return 0.5


def run_scoring(limit=None):
    print(f"\n{'='*60}")
    print("ChefAgent Simple Eval — Ollama Judge")
    print(f"{'='*60}")

    with open(CONTEXTS_PATH) as f:
        data = json.load(f)

    records = data["records"]
    if limit:
        records = records[:limit]

    print(f"Scoring {len(records)} questions with {MODEL}...\n")

    results = []
    for i, r in enumerate(records):
        q = r["question"]
        print(f"[{i+1}/{len(records)}] {q[:55]}")

        cr = score_context_relevance(q, r["contexts"])
        fa = score_faithfulness(r["answer"], r["contexts"])
        ar = score_answer_relevancy(q, r["answer"], r["ground_truth"])

        print(
            f"    context_relevance={cr}  faithfulness={fa}  answer_relevancy={ar}")

        results.append({
            "question": q,
            "category": r["category"],
            "answer": r["answer"],
            "context_relevance": cr,
            "faithfulness": fa,
            "answer_relevancy": ar,
        })

    # Overall averages
    overall = {
        "context_relevance": round(sum(r["context_relevance"] for r in results) / len(results), 3),
        "faithfulness":      round(sum(r["faithfulness"] for r in results) / len(results), 3),
        "answer_relevancy":  round(sum(r["answer_relevancy"] for r in results) / len(results), 3),
    }

    # Per-category
    categories = {}
    for r in results:
        cat = r["category"]
        if cat not in categories:
            categories[cat] = []
        categories[cat].append(r)

    per_category = {}
    for cat, items in categories.items():
        per_category[cat] = {
            "context_relevance": round(sum(r["context_relevance"] for r in items) / len(items), 3),
            "faithfulness":      round(sum(r["faithfulness"] for r in items) / len(items), 3),
            "answer_relevancy":  round(sum(r["answer_relevancy"] for r in items) / len(items), 3),
            "count": len(items),
        }

    scores = {
        "timestamp": datetime.utcnow().isoformat(),
        "question_count": len(results),
        "judge_model": MODEL,
        "overall": overall,
        "per_category": per_category,
        "per_question": results,
    }

    with open(RESULTS_PATH, "w") as f:
        json.dump(scores, f, indent=2)

    print(f"\n{'='*60}")
    print("RESULTS SUMMARY")
    print(f"{'='*60}")
    print(f"Context Relevance : {overall['context_relevance']:.3f}")
    print(f"Faithfulness      : {overall['faithfulness']:.3f}")
    print(f"Answer Relevancy  : {overall['answer_relevancy']:.3f}")
    print(f"\nPer-category:")
    for cat, s in per_category.items():
        print(
            f"  {cat:<20} relevance={s['context_relevance']:.2f}  faith={s['faithfulness']:.2f}  relevancy={s['answer_relevancy']:.2f}")
    print(f"\nSaved to {RESULTS_PATH}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int)
    args = parser.parse_args()
    run_scoring(limit=args.limit)
