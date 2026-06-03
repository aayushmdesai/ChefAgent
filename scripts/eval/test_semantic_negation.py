"""
test_semantic_negation.py
Week 11 — Validates the semantic negation fix for X-free queries.

Before fix: "dairy-free pasta" excluded only the literal word "dairy"
            → returned recipes containing milk, cream, butter, cheese
After fix:  "dairy-free pasta" expands to full DietaryCategoryMap ingredient set
            → correctly excludes milk, cream, butter, cheese, yogurt, whey, etc.

Usage:
    python scripts/eval/test_semantic_negation.py

Requires:
    - API running on localhost:5100 (docker compose up -d && dotnet run)
    - pip install requests tabulate
"""

import requests
import json
from datetime import datetime
from tabulate import tabulate

API_BASE = "http://localhost:5100"
SEARCH_ENDPOINT = f"{API_BASE}/recipes/search"

# ── Dairy ingredients we expect to be ABSENT after the fix ────────────────────
# Subset of DietaryCategoryMap["dairy"] — the most common ones that appear
# in real recipe ingredient strings.
DAIRY_TERMS = {
    "milk", "whole milk", "skim milk", "buttermilk", "evaporated milk",
    "condensed milk", "powdered milk", "dry milk", "milk powder",
    "butter", "clarified butter", "ghee", "cream", "heavy cream",
    "light cream", "whipping cream", "double cream", "sour cream",
    "half and half", "half-and-half", "cheese", "cheddar", "mozzarella",
    "parmesan", "ricotta", "cottage cheese", "cream cheese", "brie",
    "gouda", "feta", "gruyere", "swiss cheese", "yogurt", "greek yogurt",
    "plain yogurt", "whey", "whey powder", "whey protein", "casein",
    "caseinate", "lactose", "crème fraîche", "creme fraiche",
}

GLUTEN_TERMS = {
    "wheat", "wheat flour", "all-purpose flour", "bread flour", "semolina",
    "spelt", "farro", "rye", "barley", "malt", "bread", "bread crumbs",
    "breadcrumbs", "panko", "pasta", "noodles", "udon", "ramen",
    "soy sauce", "hoisin sauce", "flour tortilla", "pita", "naan",
    "crackers", "croutons", "couscous", "seitan",
}

NUT_TERMS = {
    "almond", "almonds", "almond flour", "almond butter", "almond milk",
    "walnut", "walnuts", "cashew", "cashews", "cashew butter",
    "hazelnut", "hazelnuts", "pecan", "pecans", "pine nut", "pine nuts",
    "pistachio", "pistachios", "peanut", "peanuts", "peanut butter",
    "macadamia", "brazil nut", "mixed nuts", "nut butter",
}

# ── Test cases ────────────────────────────────────────────────────────────────
TEST_CASES = [
    # (query, category_label, violation_terms, max_results)
    ("dairy-free pasta dinner",         "dairy",  DAIRY_TERMS,  10),
    ("dairy-free soup",                 "dairy",  DAIRY_TERMS,  10),
    ("dairy-free chicken recipe",       "dairy",  DAIRY_TERMS,  10),
    ("gluten-free pasta",               "gluten", GLUTEN_TERMS, 10),
    ("gluten-free bread recipe",        "gluten", GLUTEN_TERMS, 10),
    ("nut-free cookies",                "nuts",   NUT_TERMS,    10),
    ("nut-free chocolate cake",         "nuts",   NUT_TERMS,    10),
    # Control: no x-free term — violations are expected / irrelevant
    ("pasta with cream sauce",          "none",   set(),        5),
]


def check_recipe_violations(recipe: dict, violation_terms: set[str]) -> list[str]:
    """
    Returns a list of violation strings found in the recipe's ingredients.
    Each violation is: '<ingredient_string> (matched: <term>)'
    """
    violations = []
    ingredients = recipe.get("ingredients", [])
    for ingredient in ingredients:
        lower = ingredient.lower()
        for term in violation_terms:
            if term in lower:
                violations.append(f"{ingredient!r} matched '{term}'")
                break  # one violation per ingredient line is enough
    return violations


def search(query: str, max_results: int) -> list[dict]:
    """Calls /recipes/search and returns the recipe list."""
    payload = {"query": query, "maxResults": max_results}
    resp = requests.post(SEARCH_ENDPOINT, json=payload, timeout=30)
    resp.raise_for_status()
    data = resp.json()
    return data.get("recipes", [])


def run_tests() -> dict:
    """Runs all test cases and returns structured results."""
    results = []
    summary_rows = []

    print(f"\n{'='*70}")
    print(
        f"  Semantic Negation Test — {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    print(f"  Endpoint: {SEARCH_ENDPOINT}")
    print(f"{'='*70}\n")

    for query, category, violation_terms, max_results in TEST_CASES:
        print(f"Query: \"{query}\"")

        try:
            recipes = search(query, max_results)
        except Exception as e:
            print(f"  ❌ Request failed: {e}\n")
            results.append({
                "query": query,
                "category": category,
                "status": "ERROR",
                "error": str(e),
            })
            summary_rows.append([query, category, "ERROR", "-", "-"])
            continue

        recipe_count = len(recipes)
        all_violations = []

        for recipe in recipes:
            title = recipe.get("title", "Unknown")
            violations = check_recipe_violations(recipe, violation_terms)
            if violations:
                all_violations.append((title, violations))

        # Determine pass/fail
        if category == "none":
            status = "CONTROL"
            icon = "⚪"
        elif len(all_violations) == 0:
            status = "PASS"
            icon = "✅"
        else:
            status = "FAIL"
            icon = "❌"

        # Print per-query detail
        print(f"  {icon} {status} — {recipe_count} recipes returned")

        if all_violations:
            print(
                f"  Violations found in {len(all_violations)}/{recipe_count} recipes:")
            for title, viols in all_violations:
                print(f"    • {title}")
                for v in viols[:3]:  # cap at 3 per recipe for readability
                    print(f"        {v}")
        else:
            if category != "none":
                print(
                    f"  No {category} ingredients found in any returned recipe.")

        # Print titles returned
        titles = [r.get("title", "?") for r in recipes]
        print(
            f"  Recipes: {', '.join(titles[:5])}" + (" ..." if len(titles) > 5 else ""))
        print()

        results.append({
            "query": query,
            "category": category,
            "status": status,
            "recipe_count": recipe_count,
            "violations": [
                {"title": t, "violations": v} for t, v in all_violations
            ],
            "titles": titles,
        })
        summary_rows.append([
            query,
            category,
            f"{icon} {status}",
            recipe_count,
            len(all_violations),
        ])

    # Summary table
    print(f"\n{'='*70}")
    print("  SUMMARY")
    print(f"{'='*70}")
    print(tabulate(
        summary_rows,
        headers=["Query", "Category", "Status", "Recipes", "Violations"],
        tablefmt="simple",
        maxcolwidths=[35, 8, 10, 7, 10],
    ))

    # Counts
    passed = sum(1 for r in results if r["status"] == "PASS")
    failed = sum(1 for r in results if r["status"] == "FAIL")
    errors = sum(1 for r in results if r["status"] == "ERROR")
    tested = sum(1 for r in results if r["status"] in ("PASS", "FAIL"))

    print(f"\n  Result: {passed}/{tested} passed", end="")
    if errors:
        print(f", {errors} errors", end="")
    print()

    return {
        "timestamp": datetime.now().isoformat(),
        "endpoint": SEARCH_ENDPOINT,
        "passed": passed,
        "failed": failed,
        "errors": errors,
        "cases": results,
    }


def save_results(results: dict):
    out_path = f"eval/datasets/week11_negation_test.json"
    with open(out_path, "w") as f:
        json.dump(results, f, indent=2)
    print(f"\n  Results saved → {out_path}")


if __name__ == "__main__":
    results = run_tests()
    save_results(results)
