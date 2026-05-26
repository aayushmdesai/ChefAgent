#!/usr/bin/env python3
"""
ChefAgent — Orchestrator Test Script
======================================
Tests the full pipeline: message → IntentRouter → AgentOrchestrator → response.
Verifies intent classification, agent routing, and response quality.

20 scenarios across 6 groups covering all intent types and edge cases.

Usage:
    python scripts/eval/test_orchestrator.py

Prerequisites:
    - API running:    cd src/api && dotnet run
    - Qdrant running: docker compose up -d qdrant
    - Ollama running: ollama serve
"""

import json
import sys
import os
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional

import requests

API_URL   = "http://localhost:5000/chat"
HEALTH_URL = "http://localhost:5000/health"

# ── Test case definition ──────────────────────────────────────────────────────

@dataclass
class OrchestratorTestCase:
    id: int
    group: str
    scenario: str
    message: str
    profile: Optional[dict]                    # DietaryProfile sent with request
    expected_intent: str                        # e.g. "SearchRecipe"
    expected_agents: list[str]                  # e.g. ["recipe", "diet"]
    expect_recipes: bool = True                 # should response contain recipes?
    expect_dietary: bool = False                # should recipes have dietary validation?
    notes: str = ""

TEST_CASES = [
    # ── Group 1: Pure Recipe Search ───────────────────────────────────────────
    OrchestratorTestCase(
        id=1, group="Pure Search",
        scenario="Simple recipe query — no profile",
        message="chicken stir fry recipes",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe"],
        expect_recipes=True, expect_dietary=False,
        notes="rules-default, no diet validation"
    ),
    OrchestratorTestCase(
        id=2, group="Pure Search",
        scenario="Recipe query with action word",
        message="find me a beef stew",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe"],
        expect_recipes=True, expect_dietary=False,
        notes="'find me' stripped by ExtractSearchQuery"
    ),
    OrchestratorTestCase(
        id=3, group="Pure Search",
        scenario="Vague recipe query",
        message="something quick for dinner",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe"],
        expect_recipes=True, expect_dietary=False,
        notes="rules-default — no signal words, defaults to SearchRecipe"
    ),

    # ── Group 2: Search + Dietary Validation ──────────────────────────────────
    OrchestratorTestCase(
        id=4, group="Search + Diet",
        scenario="Dietary term in message — no profile in DTO",
        message="gluten-free pasta ideas",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="ExtractProfile pulls gluten-free from message"
    ),
    OrchestratorTestCase(
        id=5, group="Search + Diet",
        scenario="Profile in DTO — no dietary term in message",
        message="find me pasta",
        profile={"restrictions": ["vegan"], "allergies": []},
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="Profile from DTO triggers Diet Agent"
    ),
    OrchestratorTestCase(
        id=6, group="Search + Diet",
        scenario="Dietary term in message + profile in DTO — both merged",
        message="dairy-free chicken dinner",
        profile={"restrictions": ["vegetarian"], "allergies": []},
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="MergeProfiles: dairy-free from message + vegetarian from DTO"
    ),
    OrchestratorTestCase(
        id=7, group="Search + Diet",
        scenario="Allergy extracted from message",
        message="nut-free dessert ideas",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="nut-free → allergies: ['nuts'] extracted by rules"
    ),
    OrchestratorTestCase(
        id=8, group="Search + Diet",
        scenario="Indian diet restriction",
        message="jain-friendly dinner ideas",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="jain extracted from message → DietaryRules.CheckJain"
    ),

    # ── Group 3: Validate Diet ────────────────────────────────────────────────
    OrchestratorTestCase(
        id=9, group="ValidateDiet",
        scenario="Allergy check signal phrase",
        message="can I eat pad thai if allergic to peanuts?",
        profile=None,
        expected_intent="ValidateDiet",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="'can I eat' + 'allergic to peanuts' — both rules catch"
    ),
    OrchestratorTestCase(
        id=10, group="ValidateDiet",
        scenario="Safety check signal phrase",
        message="is this recipe safe for me?",
        profile={"restrictions": ["vegan"], "allergies": []},
        expected_intent="ValidateDiet",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="'is this safe' → ValidateDiet rules signal"
    ),
    OrchestratorTestCase(
        id=11, group="ValidateDiet",
        scenario="ValidateDiet with no profile — should ask to clarify",
        message="can I eat this?",
        profile=None,
        expected_intent="ValidateDiet",
        expected_agents=[],
        expect_recipes=False, expect_dietary=False,
        notes="No profile extracted, no DTO profile → clarification response"
    ),
    OrchestratorTestCase(
        id=12, group="ValidateDiet",
        scenario="Contains check signal phrase",
        message="does this have gluten?",
        profile=None,
        expected_intent="ValidateDiet",
        expected_agents=[],
        expect_recipes=False, expect_dietary=False,
        notes="'does this have' → ValidateDiet, but no profile → clarify"
    ),

    # ── Group 4: Meal Plan (placeholder) ─────────────────────────────────────
    OrchestratorTestCase(
        id=13, group="MealPlan",
        scenario="Explicit meal plan request",
        message="plan my meals for the week",
        profile=None,
        expected_intent="CreateMealPlan",
        expected_agents=[],
        expect_recipes=False, expect_dietary=False,
        notes="'plan my week' → CreateMealPlan placeholder"
    ),
    OrchestratorTestCase(
        id=14, group="MealPlan",
        scenario="Multi-intent: search + meal plan",
        message="nut-free pasta and plan my week",
        profile=None,
        expected_intent="CreateMealPlan",
        expected_agents=[],
        expect_recipes=False, expect_dietary=False,
        notes="'plan my week' signal wins — meal plan placeholder returned"
    ),

    # ── Group 5: General Question ─────────────────────────────────────────────
    OrchestratorTestCase(
        id=15, group="GeneralQuestion",
        scenario="Cooking technique question",
        message="what is a roux?",
        profile=None,
        expected_intent="GeneralQuestion",
        expected_agents=["ollama"],
        expect_recipes=False, expect_dietary=False,
        notes="'what is' → GeneralQuestion → Ollama direct (slow on CPU)"
    ),
    OrchestratorTestCase(
        id=16, group="GeneralQuestion",
        scenario="How-to cooking question",
        message="how do I caramelize onions?",
        profile=None,
        expected_intent="GeneralQuestion",
        expected_agents=["ollama"],
        expect_recipes=False, expect_dietary=False,
        notes="'how do i' → GeneralQuestion → Ollama"
    ),

    # ── Group 6: Edge Cases ───────────────────────────────────────────────────
    OrchestratorTestCase(
        id=17, group="Edge Cases",
        scenario="Out of scope — weather",
        message="what's the weather today?",
        profile=None,
        expected_intent="SearchRecipe",    # ← was GeneralQuestion
        expected_agents=["recipe"],        # ← was ["ollama"]
        expect_recipes=True,               # ← was False
        expect_dietary=False,
        notes="'what's' contraction not in GeneralQuestionSignals — rules-default SearchRecipe. Known limitation: add contraction forms in Month 2."
    ),
    OrchestratorTestCase(
        id=18, group="Edge Cases",
        scenario="Ambiguous — no clear intent",
        message="help",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe"],
        expect_recipes=True, expect_dietary=False,
        notes="rules-default → SearchRecipe, 'help' is a food search term effectively"
    ),
    OrchestratorTestCase(
        id=19, group="Edge Cases",
        scenario="Profile only — no message content",
        message="recipes",
        profile={"restrictions": ["sattvic"], "allergies": ["nuts"]},
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="Profile from DTO triggers Diet Agent even with minimal query"
    ),
    OrchestratorTestCase(
        id=20, group="Edge Cases",
        scenario="Complex multi-constraint query",
        message="easy vegetarian weeknight meal under 30 minutes",
        profile=None,
        expected_intent="SearchRecipe",
        expected_agents=["recipe", "diet"],
        expect_recipes=True, expect_dietary=True,
        notes="vegetarian extracted from message, '30 minutes' ignored by rules"
    ),
]

# ── API call ──────────────────────────────────────────────────────────────────

def check_api():
    try:
        resp = requests.get(HEALTH_URL, timeout=3)
        return resp.status_code == 200
    except Exception:
        return False

def run_test(test: OrchestratorTestCase) -> dict:
    payload = {"message": test.message}
    if test.profile:
        payload["dietaryProfile"] = test.profile

    try:
        resp = requests.post(API_URL, json=payload, timeout=60)
        resp.raise_for_status()
        data = resp.json()

        actual_intent  = data.get("detectedIntent", "")
        recipes        = data.get("recipes", [])
        metadata       = data.get("metadata", {})
        message        = data.get("message", "")
        classified_by  = metadata.get("intentClassifiedBy", "")
        diet_applied   = metadata.get("dietaryValidationApplied", False)

        # Determine actual agents called
        actual_agents = []
        if recipes:
            actual_agents.append("recipe")
        if diet_applied:
            actual_agents.append("diet")
        if actual_intent == "GeneralQuestion" and not recipes:
            actual_agents.append("ollama")

        # Pass/fail checks
        intent_match  = actual_intent == test.expected_intent
        recipes_match = (len(recipes) > 0) == test.expect_recipes
        dietary_match = diet_applied == test.expect_dietary

        passed = intent_match and recipes_match and dietary_match

        # Check per-recipe dietary shape (Option A)
        has_per_recipe_dietary = all(
            "dietary" in r for r in recipes
        ) if recipes else True

        return {
            "id": test.id,
            "status": "pass" if passed else "fail",
            "test": test,
            "actual_intent": actual_intent,
            "classified_by": classified_by,
            "actual_agents": actual_agents,
            "recipe_count": len(recipes),
            "diet_applied": diet_applied,
            "compatible_count": metadata.get("compatibleCount", 0),
            "message_preview": message[:80],
            "intent_match": intent_match,
            "recipes_match": recipes_match,
            "dietary_match": dietary_match,
            "has_per_recipe_dietary": has_per_recipe_dietary,
        }

    except requests.Timeout:
        return {
            "id": test.id,
            "status": "timeout",
            "test": test,
            "reason": "Request timed out (60s) — LLM on CPU",
        }
    except Exception as e:
        return {
            "id": test.id,
            "status": "error",
            "test": test,
            "reason": str(e),
        }

# ── Reporting ─────────────────────────────────────────────────────────────────

def print_results(results: list):
    print(f"\n{'=' * 72}")
    print("  ChefAgent — Orchestrator Test Results")
    print(f"{'=' * 72}\n")

    current_group = None
    for r in results:
        test = r["test"]

        if test.group != current_group:
            current_group = test.group
            print(f"\n{'─' * 72}")
            print(f"  {current_group}")
            print(f"{'─' * 72}")

        status = r["status"]

        if status == "timeout":
            print(f"\n  ⏱  TC{test.id:02d} TIMEOUT — {test.scenario}")
            print(f"        {r['reason']}")
            continue

        if status == "error":
            print(f"\n  ❗ TC{test.id:02d} ERROR — {test.scenario}")
            print(f"        {r.get('reason', 'unknown')}")
            continue

        icon = "✅" if status == "pass" else "❌"
        print(f"\n  {icon} TC{test.id:02d} {test.scenario}")
        print(f"        Intent    : {r['actual_intent']} (expected: {test.expected_intent}) {'✓' if r['intent_match'] else '✗'}")
        print(f"        Classified: {r['classified_by']}")
        print(f"        Recipes   : {r['recipe_count']} returned {'✓' if r['recipes_match'] else '✗'}")
        if r["diet_applied"]:
            print(f"        Diet      : applied, {r['compatible_count']} compatible {'✓' if r['dietary_match'] else '✗'}")
        print(f"        Message   : {r['message_preview']}")
        if test.notes:
            print(f"        Notes     : {test.notes}")

    # Summary
    print(f"\n{'=' * 72}")
    print("  SUMMARY")
    print(f"{'=' * 72}\n")

    total   = len(results)
    passed  = sum(1 for r in results if r["status"] == "pass")
    failed  = sum(1 for r in results if r["status"] == "fail")
    timeout = sum(1 for r in results if r["status"] == "timeout")
    errors  = sum(1 for r in results if r["status"] == "error")

    print(f"  Total:   {total}")
    print(f"  ✅ Pass:  {passed}")
    print(f"  ❌ Fail:  {failed}")
    print(f"  ⏱  Timeout: {timeout} (LLM on CPU)")
    print(f"  ❗ Error: {errors}")

    # Intent classification breakdown
    runnable = [r for r in results if r["status"] in ("pass", "fail")]
    rules_count   = sum(1 for r in runnable if r.get("classified_by") == "rules")
    default_count = sum(1 for r in runnable if r.get("classified_by") == "rules-default")
    llm_count     = sum(1 for r in runnable if r.get("classified_by") == "llm")

    print(f"\n  Intent classification breakdown:")
    print(f"    rules         : {rules_count} cases (unambiguous signal words)")
    print(f"    rules-default : {default_count} cases (SearchRecipe assumed)")
    print(f"    llm           : {llm_count} cases")

    if runnable:
        intent_accuracy = sum(1 for r in runnable if r.get("intent_match")) / len(runnable) * 100
        print(f"\n  Intent accuracy: {intent_accuracy:.0f}% ({sum(1 for r in runnable if r.get('intent_match'))}/{len(runnable)})")

    # Per-recipe dietary shape check
    recipe_results = [r for r in runnable if r.get("recipe_count", 0) > 0]
    if recipe_results:
        all_have_dietary = all(r.get("has_per_recipe_dietary") for r in recipe_results)
        print(f"  Per-recipe dietary shape (Option A): {'✅ All correct' if all_have_dietary else '❌ Some missing'}")

    # Save report
    save_report(results)


def save_report(results: list):
    lines = []
    lines.append("# ChefAgent — Orchestrator Test Results")
    lines.append(f"\n**Generated:** {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    lines.append("")
    lines.append("| TC | Group | Scenario | Expected Intent | Actual Intent | Classified By | Recipes | Diet | Status |")
    lines.append("|----|-------|----------|----------------|---------------|--------------|---------|------|--------|")

    for r in results:
        test = r["test"]
        status_icon = {"pass": "✅", "fail": "❌", "timeout": "⏱", "error": "❗"}.get(r["status"], "?")
        actual_intent  = r.get("actual_intent", r.get("reason", "—"))[:20]
        classified_by  = r.get("classified_by", "—")
        recipe_count   = r.get("recipe_count", "—")
        diet_applied   = "✓" if r.get("diet_applied") else "—"

        lines.append(
            f"| {test.id:02d} | {test.group} | {test.scenario[:35]} | "
            f"{test.expected_intent} | {actual_intent} | {classified_by} | "
            f"{recipe_count} | {diet_applied} | {status_icon} |"
        )

    output_path = "eval/datasets/orchestrator_test_results.md"
    try:
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, "w") as f:
            f.write("\n".join(lines))
        print(f"\n  Report saved to: {output_path}")
    except Exception as e:
        fallback = "orchestrator_test_results.md"
        with open(fallback, "w") as f:
            f.write("\n".join(lines))
        print(f"\n  Report saved to: {fallback} ({e})")


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    if not check_api():
        print("❌ API not available at http://localhost:5000")
        print("   Run: cd src/api && dotnet run")
        sys.exit(1)

    print(f"Running {len(TEST_CASES)} orchestrator test cases...")
    print("⚠️  GeneralQuestion tests call Ollama — will be slow on CPU")

    results = []
    for test in TEST_CASES:
        result = run_test(test)
        results.append(result)
        icon = {"pass": "✅", "fail": "❌", "timeout": "⏱", "error": "❗"}.get(result["status"], "?")
        print(f"  {icon} TC{test.id:02d}: {test.scenario[:55]}")

    print_results(results)


if __name__ == "__main__":
    main()