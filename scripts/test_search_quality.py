#!/usr/bin/env python3
"""
ChefAgent Search Quality Test
==============================
Runs structured test queries against the /recipes/search API endpoint
and reports on retrieval quality across different query categories.

Usage:
    python scripts/test_search_quality.py

Prerequisites:
    - API running: cd src/api && dotnet run
    - Ollama running: ollama serve
    - Qdrant running: docker compose up -d qdrant
"""

import requests
import json
from dataclasses import dataclass

API_URL = "http://localhost:5000/recipes/search"


@dataclass
class TestQuery:
    category: str
    query: str
    expect_relevant: bool  # Do we expect good results?


TEST_QUERIES = [
    # Exact match — should score highest
    TestQuery("Exact Match", "chocolate chip cookies", True),
    TestQuery("Exact Match", "banana bread", True),

    # Ingredient-based — user says what they have
    TestQuery("By Ingredients", "what can I make with chicken and rice", True),
    TestQuery("By Ingredients", "recipes using eggs cheese and spinach", True),

    # Dietary constraint — needs semantic understanding
    TestQuery("Dietary", "vegetarian pasta dinner", True),
    TestQuery("Dietary", "dessert without dairy", True),

    # Cuisine-specific
    TestQuery("Cuisine", "spicy Mexican dinner", True),
    TestQuery("Cuisine", "Italian soup", True),

    # Situation / mood — most abstract
    TestQuery("Situation", "quick easy weeknight meal", True),
    TestQuery("Situation", "something warm and comforting for winter", True),

    # Irrelevant — should score LOW
    TestQuery("Irrelevant", "how to change a car tire", False),
    TestQuery("Irrelevant", "python programming tutorial for beginners", False),
]


def search(query: str, max_results: int = 3) -> dict:
    try:
        resp = requests.post(
            API_URL,
            json={"query": query, "maxResults": max_results},
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()
    except requests.ConnectionError:
        print("❌ Cannot connect to API at", API_URL)
        print("   Make sure the API is running: cd src/api && dotnet run")
        exit(1)
    except Exception as e:
        return {"error": str(e), "recipes": []}


def main():
    print("=" * 70)
    print("  ChefAgent — Search Quality Test")
    print("=" * 70)

    results_by_category = {}

    for test in TEST_QUERIES:
        data = search(test.query)
        recipes = data.get("recipes", [])

        scores = [r["relevanceScore"] for r in recipes]
        avg_score = sum(scores) / len(scores) if scores else 0
        top_score = scores[0] if scores else 0

        if test.category not in results_by_category:
            results_by_category[test.category] = []

        results_by_category[test.category].append({
            "query": test.query,
            "top_score": top_score,
            "avg_score": avg_score,
            "top_result": recipes[0]["title"] if recipes else "N/A",
            "expect_relevant": test.expect_relevant,
        })

    # Print results by category
    for category, results in results_by_category.items():
        print(f"\n{'─' * 70}")
        print(f"  Category: {category}")
        print(f"{'─' * 70}")

        for r in results:
            icon = "✅" if (r["expect_relevant"] and r["top_score"] > 0.65) or \
                         (not r["expect_relevant"] and r["top_score"] < 0.65) else "⚠️"

            print(f"\n  {icon} \"{r['query']}\"")
            print(f"     Top result: {r['top_result']}")
            print(f"     Top score:  {r['top_score']:.4f}  |  Avg score: {r['avg_score']:.4f}")

    # Summary
    print(f"\n{'=' * 70}")
    print("  SUMMARY")
    print(f"{'=' * 70}")

    all_results = [r for results in results_by_category.values() for r in results]
    relevant_queries = [r for r in all_results if r["expect_relevant"]]
    irrelevant_queries = [r for r in all_results if not r["expect_relevant"]]

    avg_relevant = sum(r["top_score"] for r in relevant_queries) / len(relevant_queries) if relevant_queries else 0
    avg_irrelevant = sum(r["top_score"] for r in irrelevant_queries) / len(irrelevant_queries) if irrelevant_queries else 0

    print(f"\n  Avg top score (relevant queries):    {avg_relevant:.4f}")
    print(f"  Avg top score (irrelevant queries):   {avg_irrelevant:.4f}")
    print(f"  Score gap (higher = better):          {avg_relevant - avg_irrelevant:.4f}")

    # Category ranking
    print(f"\n  Category ranking by avg top score:")
    cat_avgs = []
    for category, results in results_by_category.items():
        relevant = [r for r in results if r["expect_relevant"]]
        if relevant:
            avg = sum(r["top_score"] for r in relevant) / len(relevant)
            cat_avgs.append((category, avg))

    for cat, avg in sorted(cat_avgs, key=lambda x: x[1], reverse=True):
        bar = "█" * int(avg * 40)
        print(f"    {cat:<20} {avg:.4f}  {bar}")

    print(f"\n  This is your Month 1 baseline. Revisit in Month 3 with RAGAS eval.")
    print()


if __name__ == "__main__":
    main()