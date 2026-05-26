#!/usr/bin/env python3
"""
ChefAgent Search Quality Test
==============================
Runs structured test queries against the /recipes/search API endpoint
and reports on retrieval quality across different query categories.

Week 1: Original 12 queries across 6 categories (baseline)
Week 2: Added 12 queries for negation, filtering, misspelling, technique, multi-intent

Usage:
    python scripts/test_search_quality.py

Prerequisites:
    - API running: cd src/api && dotnet run
    - Ollama running: ollama serve
    - Qdrant running: docker compose up -d qdrant
"""

import json
import os
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime

import requests

API_URL = "http://localhost:5000/recipes/search"


@dataclass
class TestQuery:
    category: str
    query: str
    expect_relevant: bool  # Do we expect good results?
    week: int = 1  # Which week this test was added
    params: dict = field(default_factory=dict)  # Extra API params (filters, etc.)


TEST_QUERIES = [
    # ════════════════════════════════════════════
    #  Week 1 — Original baseline queries
    # ════════════════════════════════════════════

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

    # ════════════════════════════════════════════
    #  Week 2 — New queries for improved pipeline
    # ════════════════════════════════════════════

    # Negation — Week 1 FAILED ("pasta without tomatoes" returned tomato recipes)
    TestQuery("Negation", "pasta without tomatoes", True, week=2),
    TestQuery("Negation", "cookies without nuts", True, week=2),

    # Negation (X-free pattern)
    TestQuery("Negation (X-free)", "gluten-free dessert", True, week=2),
    TestQuery("Negation (X-free)", "dairy-free soup", True, week=2),

    # Filtering — structural constraints
    TestQuery("Filtering", "simple chicken dinner", True, week=2,
              params={"maxIngredients": 6}),
    TestQuery("Filtering", "easy dessert", True, week=2,
              params={"maxSteps": 5}),

    # Misspelling — Week 1 finding: degrades quality
    TestQuery("Misspelling", "chiken noodle soop", True, week=2),
    TestQuery("Misspelling", "lasanga recipe", True, week=2),

    # Technique — Week 1 finding: worked well (~0.78)
    TestQuery("Technique", "slow cooker beef stew", True, week=2),
    TestQuery("Technique", "grilled chicken breast", True, week=2),

    # Multi-Intent — complex queries with multiple constraints
    TestQuery("Multi-Intent", "healthy chicken meal under 30 minutes", True, week=2),
    TestQuery("Multi-Intent", "easy vegetarian dinner for two", True, week=2),
]

# Week 1 baselines (from original test_search_quality.py results)
WEEK1_BASELINES = {
    "Exact Match": 0.8053,
    "By Ingredients": 0.8282,
    "Dietary": 0.6995,
    "Cuisine": 0.6815,
    "Situation": 0.6373,
    "Irrelevant": 0.5822,
}


def search(query: str, max_results: int = 5, extra_params: dict = None) -> dict:
    """Run a single query against the API."""
    payload = {"query": query, "maxResults": max_results}
    if extra_params:
        payload.update(extra_params)

    try:
        resp = requests.post(API_URL, json=payload, timeout=30)
        resp.raise_for_status()
        return resp.json()
    except requests.ConnectionError:
        print(f"❌ Cannot connect to API at {API_URL}")
        print("   Make sure the API is running: cd src/api && dotnet run")
        sys.exit(1)
    except Exception as e:
        return {"error": str(e), "recipes": []}


def check_negation(test: TestQuery, recipes: list) -> list:
    """For negation queries, check if excluded terms appear in results."""
    query_lower = test.query.lower()
    violations = []
    excluded = []

    for pattern_word in ["without ", "no ", "excluding "]:
        if pattern_word in query_lower:
            term = query_lower.split(pattern_word)[-1].strip()
            excluded.append(term)

    free_matches = re.findall(r"(\w+)-free", query_lower)
    excluded.extend(free_matches)

    if not excluded:
        return violations

    for recipe in recipes:
        for exc in excluded:
            for ing in recipe.get("ingredients", []):
                if exc in ing.lower():
                    violations.append({
                        "recipe": recipe["title"],
                        "ingredient": ing,
                        "excluded_term": exc,
                    })
    return violations


def check_filter(test: TestQuery, recipes: list) -> bool:
    """For filter queries, check all results respect the constraint."""
    max_ing = test.params.get("maxIngredients")
    max_steps = test.params.get("maxSteps")

    for recipe in recipes:
        if max_ing and recipe.get("ingredientCount", 0) > max_ing:
            return False
        if max_steps and recipe.get("stepCount", 0) > max_steps:
            return False
    return True


def main():
    print("=" * 70)
    print("  ChefAgent — Search Quality Test (Week 1 + Week 2)")
    print("=" * 70)

    results_by_category = {}
    all_results = []

    for test in TEST_QUERIES:
        data = search(test.query, extra_params=test.params if test.params else None)
        recipes = data.get("recipes", [])

        scores = [r["relevanceScore"] for r in recipes]
        avg_score = sum(scores) / len(scores) if scores else 0
        top_score = scores[0] if scores else 0

        # Run checks
        negation_violations = []
        filter_ok = True
        if test.category.startswith("Negation"):
            negation_violations = check_negation(test, recipes)
        if test.category == "Filtering":
            filter_ok = check_filter(test, recipes)

        result = {
            "query": test.query,
            "category": test.category,
            "week": test.week,
            "params": test.params,
            "top_score": top_score,
            "avg_score": avg_score,
            "top_result": recipes[0]["title"] if recipes else "N/A",
            "top_ingredients": recipes[0].get("ingredientCount", "?") if recipes else "?",
            "expect_relevant": test.expect_relevant,
            "recipes": recipes,
            "negation_violations": negation_violations,
            "filter_ok": filter_ok,
        }

        if test.category not in results_by_category:
            results_by_category[test.category] = []
        results_by_category[test.category].append(result)
        all_results.append(result)

    # ── Print results by category ──
    for category, results in results_by_category.items():
        print(f"\n{'─' * 70}")
        print(f"  Category: {category}")
        print(f"{'─' * 70}")

        for r in results:
            # Determine pass/fail icon
            if r["category"].startswith("Negation"):
                icon = "✅" if not r["negation_violations"] else "❌"
            elif r["category"] == "Filtering":
                icon = "✅" if r["filter_ok"] else "❌"
            elif r["expect_relevant"] and r["top_score"] > 0.65:
                icon = "✅"
            elif not r["expect_relevant"] and r["top_score"] < 0.65:
                icon = "✅"
            else:
                icon = "⚠️"

            week_tag = f" [W{r['week']}]" if r["week"] > 1 else ""
            print(f"\n  {icon} \"{r['query']}\"{week_tag}")
            print(f"     Top result: {r['top_result']}")
            print(f"     Top score:  {r['top_score']:.4f}  |  Avg score: {r['avg_score']:.4f}")

            if r["params"]:
                print(f"     Filters:    {json.dumps(r['params'])}")
                print(f"     Top result ingredients: {r['top_ingredients']}")

            if r["negation_violations"]:
                for v in r["negation_violations"]:
                    print(f"     ❌ \"{v['recipe']}\" has \"{v['ingredient']}\" (excluded: \"{v['excluded_term']}\")")
            elif r["category"].startswith("Negation"):
                print(f"     ✅ No negation violations")

    # ── Summary ──
    print(f"\n{'=' * 70}")
    print("  SUMMARY")
    print(f"{'=' * 70}")

    relevant_queries = [r for r in all_results if r["expect_relevant"] and not r["category"].startswith("Negation") and r["category"] != "Filtering"]
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

    # Week 1 vs Week 2 comparison
    print(f"\n  Week 1 vs Week 2 comparison:")
    category_scores = defaultdict(list)
    for r in all_results:
        if r["top_score"] is not None:
            category_scores[r["category"]].append(r["top_score"])

    for category in sorted(category_scores.keys()):
        scores = category_scores[category]
        avg = sum(scores) / len(scores)
        baseline = WEEK1_BASELINES.get(category)
        if baseline:
            diff = avg - baseline
            arrow = "↑" if diff > 0.01 else ("↓" if diff < -0.01 else "→")
            print(f"    {category:<20} W2: {avg:.4f}  (W1: {baseline:.4f}, {diff:+.4f} {arrow})")
        else:
            print(f"    {category:<20} W2: {avg:.4f}  (new)")

    # Negation summary
    neg_queries = [r for r in all_results if r["category"].startswith("Negation")]
    neg_clean = sum(1 for r in neg_queries if not r["negation_violations"])
    print(f"\n  Negation: {neg_clean}/{len(neg_queries)} queries clean")

    # Filter summary
    filter_queries = [r for r in all_results if r["category"] == "Filtering"]
    filter_pass = sum(1 for r in filter_queries if r["filter_ok"])
    print(f"  Filtering: {filter_pass}/{len(filter_queries)} queries within constraints")

    # ── Save markdown report ──
    report = generate_markdown_report(all_results, results_by_category, category_scores)
    output_path = "eval/datasets/week2_retrieval_results.md"
    try:
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, "w") as f:
            f.write(report)
        print(f"\n  Report saved to: {output_path}")
    except Exception as e:
        print(f"\n  Couldn't save to {output_path}: {e}")
        fallback = "week2_retrieval_results.md"
        with open(fallback, "w") as f:
            f.write(report)
        print(f"  Report saved to: {fallback}")

    print()


def generate_markdown_report(all_results, results_by_category, category_scores):
    """Generate a markdown report for eval/datasets/."""
    lines = []
    lines.append("# ChefAgent — Week 2 Search Quality Report")
    lines.append(f"\n**Generated:** {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    lines.append(f"**Queries tested:** {len(all_results)} ({sum(1 for r in all_results if r['week'] == 1)} from Week 1, {sum(1 for r in all_results if r['week'] == 2)} new in Week 2)")
    lines.append("")

    # Category summary table
    lines.append("## Category Summary — Week 2 vs Week 1")
    lines.append("")
    lines.append("| Category | Week 1 Avg | Week 2 Avg | Change | Status |")
    lines.append("|----------|-----------|-----------|--------|--------|")

    for category in sorted(category_scores.keys()):
        scores = category_scores[category]
        avg = sum(scores) / len(scores)
        baseline = WEEK1_BASELINES.get(category)

        if baseline:
            change = avg - baseline
            direction = "📈" if change > 0.01 else ("📉" if change < -0.01 else "➡️")
            lines.append(f"| {category} | {baseline:.4f} | {avg:.4f} | {change:+.4f} | {direction} |")
        else:
            lines.append(f"| {category} | — (new) | {avg:.4f} | — | 🆕 |")

    lines.append("")

    # Negation summary
    lines.append("## Negation Handling")
    lines.append("")
    neg_queries = [r for r in all_results if r["category"].startswith("Negation")]
    clean = sum(1 for r in neg_queries if not r["negation_violations"])
    lines.append(f"- **{clean}/{len(neg_queries)} queries clean** (no excluded ingredients in results)")
    lines.append("- Week 1: \"pasta without tomatoes\" returned \"Pasta With Tomatoes\" as #2")
    lines.append("- Week 2: Negation handler strips excluded terms pre-search, filters violations post-search")
    lines.append("")

    # Per-query detail
    lines.append("## Per-Query Results")
    lines.append("")

    for category, results in results_by_category.items():
        lines.append(f"### {category}")
        lines.append("")
        lines.append("| Query | Top Result | Score | Notes |")
        lines.append("|-------|-----------|-------|-------|")

        for r in results:
            notes = ""
            if r["negation_violations"]:
                notes = f"❌ {len(r['negation_violations'])} violations"
            elif r["category"].startswith("Negation"):
                notes = "✅ Clean"
            elif r["category"] == "Filtering":
                notes = "✅ Within filter" if r["filter_ok"] else "❌ Exceeded filter"
            elif not r["expect_relevant"]:
                notes = "✅ Low score (expected)" if r["top_score"] < 0.65 else "⚠️ Too high"

            title = r["top_result"][:35]
            lines.append(f"| {r['query']} | {title} | {r['top_score']:.4f} | {notes} |")

        lines.append("")

    return "\n".join(lines)


if __name__ == "__main__":
    main()