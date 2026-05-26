#!/usr/bin/env python3
"""
ChefAgent — Diet Agent Test Script
====================================
Runs structured test cases against /recipes/search-validated and reports
which layer handled each validation: rules engine, LLM, skip, or clean pass.

Covers all 20 test cases from eval/datasets/diet_agent_test_cases.md.
LLM-dependent cases are marked and skipped when Ollama is unavailable.

Usage:
    python scripts/test_diet_agent.py

Prerequisites:
    - API running:    cd src/api && dotnet run
    - Qdrant running: docker compose up -d qdrant
    - Ollama running: ollama serve   (optional — LLM cases skipped if unavailable)
"""

import json
import sys
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional
import os

import requests

API_URL = "http://localhost:5000/recipes/search-validated"
HEALTH_URL = "http://localhost:5000/health"
OLLAMA_URL = "http://localhost:11434/api/tags"

# ── Test case definition ──────────────────────────────────────────────────────

@dataclass
class DietTestCase:
    id: int
    group: str
    scenario: str
    query: str                          # Search query to find a recipe
    profile: dict                       # DietaryProfile JSON
    expected_layer: str                 # "rules", "llm", "skip", "pass", "false_positive"
    expected_compatible: bool           # True = recipe should pass
    requires_llm: bool = False          # Skip if Ollama unavailable
    notes: str = ""

TEST_CASES = [
    # ── Group 1: Rules Engine Catches It ─────────────────────────────────────
    DietTestCase(
        id=1, group="Rules Catches It",
        scenario="Simple dairy allergy",
        query="creamy pasta with butter and milk",
        profile={"allergies": ["dairy"], "restrictions": []},
        expected_layer="rules", expected_compatible=False,
        notes="Rules should flag milk and butter immediately"
    ),
    DietTestCase(
        id=2, group="Rules Catches It",
        scenario="Simple nut allergy — peanut butter",
        query="peanut butter cookies",
        profile={"allergies": ["nuts"], "restrictions": []},
        expected_layer="rules", expected_compatible=False,
        notes="'peanut butter' is in NutIngredients — should match"
    ),
    DietTestCase(
        id=3, group="Rules Catches It",
        scenario="Vegetarian — chicken and bacon",
        query="chicken bacon dinner",
        profile={"allergies": [], "restrictions": ["vegetarian"]},
        expected_layer="rules", expected_compatible=False,
        notes="Both chicken and bacon in MeatIngredients"
    ),
    DietTestCase(
        id=4, group="Rules Catches It",
        scenario="Gluten-free — soy sauce (hidden gluten)",
        query="stir fry with soy sauce",
        profile={"allergies": [], "restrictions": ["gluten-free"]},
        expected_layer="rules", expected_compatible=False,
        notes="Soy sauce is in GlutenIngredients — hidden gluten source"
    ),
    DietTestCase(
        id=5, group="Rules Catches It",
        scenario="Vegan — eggs, milk, honey",
        query="classic cake with eggs and honey",
        profile={"allergies": [], "restrictions": ["vegan"]},
        expected_layer="rules", expected_compatible=False,
        notes="Multiple vegan violations — all in knowledge base"
    ),

    # ── Group 2: Rules Engine Misses It ──────────────────────────────────────
    DietTestCase(
        id=6, group="Rules Misses It",
        scenario="Hidden nut in pesto",
        query="pasta with pesto sauce",
        profile={"allergies": ["nuts"], "restrictions": []},
        expected_layer="llm", expected_compatible=False,
        requires_llm=True,
        notes="'pine nut' not inside 'pesto' — rules blind spot, LLM needed"
    ),
    DietTestCase(
        id=7, group="Rules Misses It",
        scenario="Anchovies in worcestershire",
        query="beef stew with worcestershire sauce",
        profile={"allergies": [], "restrictions": ["vegetarian"]},
        expected_layer="llm", expected_compatible=False,
        requires_llm=True,
        notes="Worcestershire has anchovies — in gluten list but not seafood list"
    ),
    DietTestCase(
        id=8, group="Rules Misses It",
        scenario="Gelatin in dessert — verify rules catch it",
        query="jello dessert with gelatin",
        profile={"allergies": [], "restrictions": ["vegan"]},
        expected_layer="rules", expected_compatible=False,
        notes="'gelatin' IS in MeatIngredients — rules should catch this"
    ),
    DietTestCase(
        id=9, group="Rules Misses It",
        scenario="Protein powder — composition unknown",
        query="protein shake with vanilla protein powder",
        profile={"allergies": ["dairy"], "restrictions": []},
        expected_layer="llm", expected_compatible=False,
        requires_llm=True,
        notes="Generic 'protein powder' not in rules — could contain whey (dairy)"
    ),

    # ── Group 3: False Positives ──────────────────────────────────────────────
    DietTestCase(
        id=10, group="False Positives",
        scenario="Coconut milk flagged as tree nut",
        query="thai curry with coconut milk",
        profile={"allergies": ["nuts"], "restrictions": []},
        expected_layer="false_positive", expected_compatible=True,
        notes="Rules flags 'coconut' as tree nut. Most tree-nut allergic people tolerate coconut. Over-flagging."
    ),
    DietTestCase(
        id=11, group="False Positives",
        scenario="Peanut butter — verify NOT flagged as dairy",
        query="peanut butter cookies",
        profile={"allergies": ["dairy"], "restrictions": []},
        expected_layer="pass", expected_compatible=True,
        notes="'butter' is in dairy list but 'peanut butter' phrase is in NutIngredients not DairyIngredients. Should NOT be a false positive."
    ),
    DietTestCase(
        id=12, group="False Positives",
        scenario="Almond milk — correctly flagged for nut-free",
        query="smoothie with almond milk",
        profile={"allergies": ["nuts"], "restrictions": []},
        expected_layer="rules", expected_compatible=False,
        notes="Almond IS a tree nut — this is correct behavior, not a false positive"
    ),

    # ── Group 4: Ambiguous Tier ───────────────────────────────────────────────
    DietTestCase(
        id=13, group="Ambiguous Tier",
        scenario="Taco seasoning + restriction only → skip",
        query="taco dinner with seasoning",
        profile={"allergies": [], "restrictions": ["vegetarian"]},
        expected_layer="skip", expected_compatible=False,
        notes="'seasoning' is ambiguous signal + restriction-only → recipe skipped, no LLM"
    ),
    DietTestCase(
        id=14, group="Ambiguous Tier",
        scenario="Taco seasoning + allergy → LLM",
        query="taco dinner with seasoning",
        profile={"allergies": ["nuts"], "restrictions": []},
        expected_layer="llm", expected_compatible=False,
        requires_llm=True,
        notes="'seasoning' is ambiguous signal + allergy present → must escalate to LLM"
    ),
    DietTestCase(
        id=15, group="Ambiguous Tier",
        scenario="Italian dressing + vegetarian → skip",
        query="pasta salad with italian dressing",
        profile={"allergies": [], "restrictions": ["vegetarian"]},
        expected_layer="skip", expected_compatible=False,
        notes="'dressing' is ambiguous signal (could contain anchovies) + restriction → skip"
    ),
    DietTestCase(
        id=16, group="Ambiguous Tier",
        scenario="Spice blend + vegan → skip",
        query="curry with spice blend",
        profile={"allergies": [], "restrictions": ["vegan"]},
        expected_layer="skip", expected_compatible=False,
        notes="'spice blend' triggers ambiguous + restriction-only → skipped, no LLM"
    ),

    # ── Group 5: Multiple Restrictions ───────────────────────────────────────
    DietTestCase(
        id=17, group="Multiple Restrictions",
        scenario="Vegan + nut-free — both enforced",
        query="cashew honey dessert",
        profile={"allergies": ["nuts"], "restrictions": ["vegan"]},
        expected_layer="rules", expected_compatible=False,
        notes="cashews (nut allergy) + honey (vegan) — both should be flagged"
    ),
    DietTestCase(
        id=18, group="Multiple Restrictions",
        scenario="Jain + gluten-free — both enforced",
        query="stir fry with onion and soy sauce",
        profile={"allergies": [], "restrictions": ["jain", "gluten-free"]},
        expected_layer="rules", expected_compatible=False,
        notes="onion (jain) + soy sauce (gluten) — both should be flagged"
    ),

    # ── Group 6: Clean Pass ───────────────────────────────────────────────────
    DietTestCase(
        id=19, group="Clean Pass",
        scenario="Vegetarian + vegetable soup — broth triggers skip",
        query="simple vegetable soup",
        profile={"allergies": [], "restrictions": ["vegetarian"]},
        expected_layer="skip", expected_compatible=False,
        notes="'broth' is an ambiguous signal — over-cautious skip. Known limitation."
    ),
    DietTestCase(
        id=20, group="Clean Pass",
        scenario="Vegan + fruit salad — clean pass",
        query="fresh fruit salad with lime and mint",
        profile={"allergies": [], "restrictions": ["vegan"]},
        expected_layer="pass", expected_compatible=True,
        notes="No meat, dairy, eggs, honey — should pass cleanly with no ambiguous signals"
    ),
]

# ── API calls ─────────────────────────────────────────────────────────────────

def check_ollama_available() -> bool:
    try:
        resp = requests.get(OLLAMA_URL, timeout=3)
        return resp.status_code == 200
    except Exception:
        return False


def check_api_available() -> bool:
    try:
        resp = requests.get(HEALTH_URL, timeout=3)
        return resp.status_code == 200
    except Exception:
        return False


def run_test(test: DietTestCase, ollama_available: bool) -> dict:
    """Run a single test case against the API."""
    if test.requires_llm and not ollama_available:
        return {
            "id": test.id,
            "status": "skipped",
            "reason": "LLM required but Ollama unavailable",
            "test": test,
        }

    payload = {
        "query": test.query,
        "maxResults": 3,
        "dietaryProfile": test.profile,
    }

    try:
        # Use 120s for all — even non-LLM cases can hit Ollama via ambiguous signal escalation
        resp = requests.post(API_URL, json=payload, timeout=120)
        resp.raise_for_status()
        data = resp.json()
        recipes = data.get("recipes", [])

        if not recipes:
            return {
                "id": test.id,
                "status": "no_results",
                "reason": "No recipes returned for query",
                "test": test,
            }

        # API returns [{ "recipe": {...}, "dietary": {...} }]
        # Unwrap the envelope
        top_envelope = recipes[0]
        dietary = top_envelope.get("dietary", {})
        recipe_data = top_envelope.get("recipe", {})
        is_compatible = dietary.get("isCompatible", True)
        violations = dietary.get("violations", [])
        explanation = dietary.get("explanation", "")

        # Determine actual layer from response
        actual_layer = infer_layer(is_compatible, violations, explanation)

        # Pass/fail
        compatible_match = (is_compatible == test.expected_compatible)

        # For false_positive tests — we expect compatible=True but rules flagged it
        if test.expected_layer == "false_positive":
            passed = not is_compatible  # if rules flagged it, that confirms the false positive
            note = "False positive confirmed — rules over-flagged" if not is_compatible else "No false positive — rules correctly passed"
        else:
            passed = compatible_match

        return {
            "id": test.id,
            "status": "pass" if passed else "fail",
            "test": test,
            "actual_compatible": is_compatible,
            "actual_layer": actual_layer,
            "violations": violations,
            "explanation": explanation,
            "top_recipe": recipe_data.get("title", "N/A"),
            "compatible_match": compatible_match,
        }

    except requests.ConnectionError:
        print(f"\n❌ Cannot connect to API at {API_URL}")
        print("   Make sure the API is running: cd src/api && dotnet run")
        sys.exit(1)
    except Exception as e:
        return {
            "id": test.id,
            "status": "error",
            "reason": str(e),
            "test": test,
        }


def infer_layer(is_compatible: bool, violations: list, explanation: str) -> str:
    """Infer which layer handled the validation from the response.
    detectedBy is serialized as int: 0 = Rules, 1 = Llm
    """
    if is_compatible and not violations:
        if "passed all rule checks" in explanation:
            return "pass (rules)"
        return "pass"

    if not is_compatible and not violations:
        if "ambiguous" in explanation.lower():
            return "skip"
        return "unknown"

    if violations:
        # detectedBy: 0 = Rules, 1 = Llm (C# enum serialized as int)
        detected_by = [v.get("detectedBy", 0) for v in violations]
        if any(d == 1 for d in detected_by):
            return "llm"
        return "rules"

    return "unknown"


# ── Reporting ─────────────────────────────────────────────────────────────────

def print_results(results: list, ollama_available: bool):
    print(f"\n{'=' * 72}")
    print("  ChefAgent — Diet Agent Test Results")
    print(f"{'=' * 72}")
    print(f"  Ollama available: {'✅ Yes' if ollama_available else '❌ No (LLM cases skipped)'}")
    print()

    current_group = None
    for r in results:
        test = r["test"]

        if test.group != current_group:
            current_group = test.group
            print(f"\n{'─' * 72}")
            print(f"  {current_group}")
            print(f"{'─' * 72}")

        status = r["status"]

        if status == "skipped":
            icon = "⏩"
            line = f"  {icon} TC{test.id:02d} [{test.expected_layer.upper():<12}] {test.scenario}"
            print(line)
            print(f"        ↳ Skipped: {r['reason']}")
            continue

        if status == "error":
            print(f"  ❗ TC{test.id:02d} ERROR: {r.get('reason', 'unknown')}")
            continue

        if status == "no_results":
            print(f"  ⚠️  TC{test.id:02d} NO RESULTS for query: \"{test.query}\"")
            continue

        # Determine display icon
        if test.expected_layer == "false_positive":
            icon = "🔍"  # investigating — false positive is nuanced
        elif r["status"] == "pass":
            icon = "✅"
        else:
            icon = "❌"

        actual_layer = r.get("actual_layer", "?")
        recipe_title = r.get("top_recipe", "N/A")[:40]
        compatible = r.get("actual_compatible")

        print(f"\n  {icon} TC{test.id:02d} [{test.expected_layer.upper():<12}] {test.scenario}")
        print(f"        Recipe : {recipe_title}")
        print(f"        Layer  : {actual_layer}")
        print(f"        Result : {'compatible ✅' if compatible else 'incompatible ❌'}")

        violations = r.get("violations", [])
        if violations:
            for v in violations[:3]:  # show up to 3
                ingredient = v.get("ingredient", "?")
                category = v.get("category", "?")
                detected = v.get("detectedBy", "?")
                matched = v.get("matchedRule", "")
                print(f"        ⚠️  {ingredient!r} → {category} (by {detected}, rule: {matched!r})")

        explanation = r.get("explanation", "")
        if explanation:
            print(f"        💬 {explanation[:100]}")

        if test.notes:
            print(f"        📝 {test.notes}")

    # Summary
    print(f"\n{'=' * 72}")
    print("  SUMMARY")
    print(f"{'=' * 72}\n")

    total = len(results)
    passed = sum(1 for r in results if r["status"] == "pass")
    failed = sum(1 for r in results if r["status"] == "fail")
    skipped = sum(1 for r in results if r["status"] == "skipped")
    errors = sum(1 for r in results if r["status"] in ("error", "no_results"))

    print(f"  Total:   {total}")
    print(f"  ✅ Pass:  {passed}")
    print(f"  ❌ Fail:  {failed}")
    print(f"  ⏩ Skip:  {skipped} (LLM unavailable)")
    print(f"  ❗ Error: {errors}")

    # Layer breakdown
    layer_counts = {}
    for r in results:
        if r["status"] in ("pass", "fail"):
            layer = r.get("actual_layer", "unknown")
            layer_counts[layer] = layer_counts.get(layer, 0) + 1

    print(f"\n  Layer breakdown (rules vs LLM):")
    for layer, count in sorted(layer_counts.items()):
        pct = count / max(total - skipped - errors, 1) * 100
        bar = "█" * int(pct / 5)
        print(f"    {layer:<20} {count:2d} cases  ({pct:.0f}%)  {bar}")

    rules_count = sum(v for k, v in layer_counts.items() if "rules" in k)
    total_run = total - skipped - errors
    if total_run > 0:
        rules_pct = rules_count / total_run * 100
        print(f"\n  Rules engine coverage: {rules_count}/{total_run} cases = {rules_pct:.0f}%")
        print(f"  Interview talking point: rules handle {rules_pct:.0f}% of cases with zero LLM calls")

    # Known limitations found
    print(f"\n  Known limitations confirmed:")
    fp_results = [r for r in results if r["test"].expected_layer == "false_positive" and r["status"] == "pass"]
    if fp_results:
        for r in fp_results:
            print(f"    🔍 TC{r['test'].id:02d}: {r['test'].scenario}")

    # Save markdown report
    save_report(results, ollama_available)


def save_report(results: list, ollama_available: bool):
    lines = []
    lines.append("# ChefAgent — Diet Agent Test Results")
    lines.append(f"\n**Generated:** {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    lines.append(f"**Ollama available:** {'Yes' if ollama_available else 'No — LLM cases skipped'}")
    lines.append("")
    lines.append("| TC | Group | Scenario | Expected Layer | Actual Layer | Compatible | Status |")
    lines.append("|----|-------|----------|---------------|-------------|------------|--------|")

    for r in results:
        test = r["test"]
        status_icon = {"pass": "✅", "fail": "❌", "skipped": "⏩", "error": "❗", "no_results": "⚠️"}.get(r["status"], "?")
        actual_layer = r.get("actual_layer", r.get("reason", "skipped"))
        compatible = r.get("actual_compatible", "—")
        lines.append(
            f"| {test.id:02d} | {test.group} | {test.scenario} | "
            f"{test.expected_layer} | {actual_layer} | {compatible} | {status_icon} |"
        )

    lines.append("")
    lines.append("## Known Limitations")
    lines.append("")
    lines.append("- **TC10 Coconut false positive**: Rules flag coconut as tree nut per FDA labeling. Most tree-nut allergic people tolerate coconut. Surface as warning, not violation.")
    lines.append("- **TC06 Pesto blind spot**: Rules cannot infer pine nuts from 'pesto'. LLM required.")
    lines.append("- **TC19 Broth over-skip**: 'vegetable broth' triggers ambiguous signal unnecessarily. Add to safe-list.")
    lines.append("- **TC07 Worcestershire**: Missing from SeafoodIngredients — anchovies not caught by rules.")

    output_path = "eval/datasets/diet_agent_test_results.md"
    try:
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, "w") as f:
            f.write("\n".join(lines))
        print(f"\n  Report saved to: {output_path}")
    except Exception as e:
        fallback = "diet_agent_test_results.md"
        with open(fallback, "w") as f:
            f.write("\n".join(lines))
        print(f"\n  Report saved to: {fallback} ({e})")


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    if not check_api_available():
        print("❌ API not available at http://localhost:5000")
        print("   Run: cd src/api && dotnet run")
        sys.exit(1)

    ollama_available = check_ollama_available()

    print(f"Running {len(TEST_CASES)} test cases...")
    if not ollama_available:
        llm_cases = sum(1 for t in TEST_CASES if t.requires_llm)
        print(f"⚠️  Ollama not available — {llm_cases} LLM-dependent cases will be skipped")

    results = []
    for test in TEST_CASES:
        result = run_test(test, ollama_available)
        results.append(result)
        dot = "✅" if result["status"] == "pass" else ("⏩" if result["status"] == "skipped" else "❌")
        print(f"  {dot} TC{test.id:02d}: {test.scenario[:50]}")

    print_results(results, ollama_available)


if __name__ == "__main__":
    main()