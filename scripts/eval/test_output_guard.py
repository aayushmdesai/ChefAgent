"""
ChefAgent — Week 7, Day 2: OutputGuard + Confidence Test Suite
Tests output validation, recipe sanity checks, and confidence signaling.

Usage:
    python3 test_output_guard.py [base_url]
    Default base_url: http://localhost:5100
"""

import requests
import sys
import json
import time

BASE_URL = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5100"
SESSION = f"output-test-{int(time.time())}"

PASS = "✅"
FAIL = "❌"

results = []


def chat(message, session_id=None, profile=None):
    payload = {
        "message": message,
        "sessionId": session_id or SESSION,
    }
    if profile:
        payload["dietaryProfile"] = profile
    try:
        r = requests.post(f"{BASE_URL}/chat", json=payload, timeout=120)
        return r.status_code, r.json()
    except Exception as e:
        return 0, {"error": str(e)}


def test(tc_id, name, message, checks, session_id=None, profile=None, notes=""):
    """
    Run a test case with multiple check functions.
    checks: list of (description, check_fn) where check_fn(status_code, response) -> bool
    """
    status_code, resp = chat(message, session_id=session_id, profile=profile)

    all_passed = True
    check_details = []

    for desc, check_fn in checks:
        try:
            passed = check_fn(status_code, resp)
        except Exception as e:
            passed = False
            desc += f" (error: {e})"

        if not passed:
            all_passed = False
        check_details.append((desc, passed))

    status = PASS if all_passed else FAIL
    results.append({"tc": tc_id, "name": name, "status": status})

    print(f"  {status} TC{tc_id:02d} | {name}")
    for desc, passed in check_details:
        mark = PASS if passed else FAIL
        print(f"       {mark} {desc}")
    if notes:
        print(f"       Note: {notes}")
    print()


# ══════════════════════════════════════════════════════════════════
# Group 1: Confidence field present and correct
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("GROUP 1: Confidence Signaling")
print("=" * 65)

test(1, "SearchRecipe no profile → High confidence",
     "find me a chicken dinner",
     checks=[
         ("Response has confidence field",
          lambda s, r: "confidence" in r or "Confidence" in r),
         ("Confidence is High",
          lambda s, r: r.get("confidence", r.get("Confidence", "")) in ["High", 0, "high"]),
         ("Recipes returned",
          lambda s, r: len(r.get("recipes", [])) > 0),
     ])

test(2, "SearchRecipe with profile → Medium confidence",
     "find me a pasta dinner",
     profile={"restrictions": ["vegetarian"],
              "allergies": [], "cuisinePreferences": []},
     session_id=f"conf-test-{int(time.time())}",
     checks=[
         ("Response has confidence field",
          lambda s, r: "confidence" in r or "Confidence" in r),
         ("Confidence is Medium",
          lambda s, r: r.get("confidence", r.get("Confidence", "")) in ["Medium", 1, "medium"]),
     ],
     notes="Diet validation involved → Medium")

test(3, "GetMealPlan with no plan → High confidence",
     "show me my plan",
     session_id=f"no-plan-{int(time.time())}",
     checks=[
         ("Confidence is High",
          lambda s, r: r.get("confidence", r.get("Confidence", "")) in ["High", 0, "high"]),
         ("Helpful fallback message",
          lambda s, r: "do not have" in r.get("message", "").lower() or "don't have" in r.get("message", "").lower() or "yet" in r.get("message", "").lower()),
     ],
     notes="Pure Redis read, no agents → High")

test(4, "GeneralQuestion → Medium confidence",
     "what is a roux?",
     checks=[
         ("Confidence is Medium",
          lambda s, r: r.get("confidence", r.get("Confidence", "")) in ["Medium", 1, "medium"]),
     ],
     notes="LLM involved → Medium")

# ══════════════════════════════════════════════════════════════════
# Group 2: Recipe sanity — all returned recipes have valid data
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("GROUP 2: Recipe Sanity Checks")
print("=" * 65)

test(5, "All recipes have non-empty titles",
     "find me a quick chicken dinner",
     checks=[
         ("At least 1 recipe returned",
          lambda s, r: len(r.get("recipes", [])) > 0),
         ("All recipes have titles",
          lambda s, r: all(
              vr.get("recipe", {}).get("title", "").strip() != ""
              for vr in r.get("recipes", [])
          )),
     ])

test(6, "All recipes have ingredients",
     "find me a chicken casserole",
     checks=[
         ("At least 1 recipe returned",
          lambda s, r: len(r.get("recipes", [])) > 0),
         ("All recipes have ingredient lists",
          lambda s, r: all(
              len(vr.get("recipe", {}).get("ingredients", [])) > 0
              for vr in r.get("recipes", [])
          )),
     ])

test(7, "All recipes have relevance scores above 0.3",
     "find me a beef stew",
     checks=[
         ("At least 1 recipe returned",
          lambda s, r: len(r.get("recipes", [])) > 0),
         ("All scores >= 0.3",
          lambda s, r: all(
              (vr.get("recipe", {}).get("relevanceScore", 0) or 0) >= 0.3
              for vr in r.get("recipes", [])
          )),
     ],
     notes="OutputGuard drops recipes below 0.3 relevance")

# ══════════════════════════════════════════════════════════════════
# Group 3: Dietary validation + confidence interaction
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("GROUP 3: Dietary + Confidence Interaction")
print("=" * 65)

test(8, "Dietary profile → recipes annotated with validation",
     "find me a pasta dinner",
     profile={"restrictions": ["vegan"],
              "allergies": [], "cuisinePreferences": []},
     session_id=f"diet-conf-{int(time.time())}",
     checks=[
         ("Recipes returned",
          lambda s, r: len(r.get("recipes", [])) > 0),
         ("At least one recipe has dietary field",
          lambda s, r: any(
              vr.get("dietary") is not None
              for vr in r.get("recipes", [])
          )),
         ("Confidence is Medium (LLM-backed diet validation)",
          lambda s, r: r.get("confidence", r.get("Confidence", "")) in ["Medium", 1, "medium"]),
     ])

test(9, "Nut-free search → rules path, Medium confidence",
     "find me a nut-free pasta dinner",
     session_id=f"nut-free-{int(time.time())}",
     checks=[
         ("Recipes returned",
          lambda s, r: len(r.get("recipes", [])) > 0),
         ("Confidence is Medium",
          lambda s, r: r.get("confidence", r.get("Confidence", "")) in ["Medium", 1, "medium"]),
     ],
     notes="nut-free extracted → diet validation runs → Medium")

# ══════════════════════════════════════════════════════════════════
# Group 4: Error handling doesn't crash
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("GROUP 4: Graceful Handling")
print("=" * 65)

test(10, "Unknown intent → High confidence, helpful message",
     "what should I cook?",
     checks=[
         ("200 OK",
          lambda s, r: s == 200),
         ("Has message",
          lambda s, r: len(r.get("message", "")) > 0),
     ])

# ══════════════════════════════════════════════════════════════════
# Summary
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("SUMMARY")
print("=" * 65)

passed = sum(1 for r in results if r["status"] == PASS)
failed = sum(1 for r in results if r["status"] == FAIL)
total = len(results)

print(f"  Passed: {passed}/{total}")
if failed:
    print(f"  Failed: {failed}/{total}")
    print()
    print("  Failed cases:")
    for r in results:
        if r["status"] == FAIL:
            print(f"    TC{r['tc']:02d} {r['name']}")
print()

sys.exit(0 if failed == 0 else 1)
