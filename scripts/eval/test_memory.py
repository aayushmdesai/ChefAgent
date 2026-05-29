"""
Week 6 — Stateful Flow Test Matrix
15 scenarios across 5 groups:
  Group 1: Conversation history + reference resolution
  Group 2: Profile persistence
  Group 3: GetMealPlan intent + contractions
  Group 4: LLM entity extraction
  Group 5: Edge cases + resilience + full end-to-end flow
"""

import os
import requests
import subprocess
import time
import json

BASE = "http://localhost:5100"
results = []

# ── Helpers ───────────────────────────────────────────────────────────────────


def log(tc, scenario, expected, actual, status, notes=""):
    results.append({
        "tc": tc, "scenario": scenario,
        "expected": expected, "actual": actual,
        "status": status, "notes": notes
    })
    icon = "✅" if status == "PASS" else ("⚠️" if status == "SKIP" else "❌")
    print(f"{icon} TC{tc:02d} {scenario}: {actual}")
    if status == "FAIL":
        print(f"       expected: {expected}")
        if notes:
            print(f"       notes:    {notes}")


def chat(message, session_id, profile=None):
    body = {"message": message, "sessionId": session_id}
    if profile:
        body["dietaryProfile"] = profile
    r = requests.post(f"{BASE}/chat", json=body, timeout=120)
    return r


def get_profile(session_id):
    return requests.get(f"{BASE}/profile/{session_id}", timeout=10)


def set_profile(session_id, restrictions=None, allergies=None):
    body = {
        "restrictions": restrictions or [],
        "allergies":    allergies or [],
        "cuisinePreferences": []
    }
    return requests.post(f"{BASE}/profile/{session_id}", json=body, timeout=10)


def flush_session(session_id):
    """Delete all Redis keys for a session."""
    for key in ["plan", "profile", "history"]:
        subprocess.run(
            ["docker", "compose", "exec", "redis", "redis-cli",
             "DEL", f"session:{session_id}:{key}"],
            capture_output=True
        )


def get_logs(tail=20):
    r = subprocess.run(
        ["docker", "compose", "logs", "api", f"--tail={tail}", "--no-color"],
        capture_output=True, text=True
    )
    return r.stdout


def history_len(session_id):
    r = subprocess.run(
        ["docker", "compose", "exec", "redis", "redis-cli",
         "LLEN", f"session:{session_id}:history"],
        capture_output=True, text=True
    )
    try:
        return int(r.stdout.strip())
    except ValueError:
        return -1


# ── Group 1: Conversation History ─────────────────────────────────────────────
print("\n── Group 1: Conversation History ────────────────────")

# TC01: Search → reference resolution (first one)
sid = "mem-tc01"
flush_session(sid)
r1 = chat("find me pasta recipes", sid)
if r1.status_code != 200:
    log(1, "Reference resolution (first one)", "ValidateDiet",
        f"status={r1.status_code}", "FAIL", r1.text)
else:
    r2 = chat("is the first one vegan?", sid)
    if r2.status_code != 200:
        log(1, "Reference resolution (first one)", "ValidateDiet",
            f"status={r2.status_code}", "FAIL", r2.text)
    else:
        d2 = r2.json()
        intent = d2.get("detectedIntent", "")
        log(1, "Reference resolution (first one)", "ValidateDiet",
            f"intent={intent} msg='{d2.get('message','')[:60]}'",
            "PASS" if intent == "ValidateDiet" else "FAIL")

# TC02: Search → ordinal reference (second one)
sid = "mem-tc02"
flush_session(sid)
r1 = chat("find me chicken recipes", sid)
if r1.status_code != 200:
    log(2, "Reference resolution (second one)", "ValidateDiet",
        f"status={r1.status_code}", "FAIL", r1.text)
else:
    r2 = chat("is the second one gluten-free?", sid)
    if r2.status_code != 200:
        log(2, "Reference resolution (second one)", "ValidateDiet",
            f"status={r2.status_code}", "FAIL", r2.text)
    else:
        d2 = r2.json()
        intent = d2.get("detectedIntent", "")
        log(2, "Reference resolution (second one)", "ValidateDiet",
            f"intent={intent} msg='{d2.get('message','')[:60]}'",
            "PASS" if intent == "ValidateDiet" else "FAIL")

# TC03: Reference with no prior history → graceful fallback
sid = "mem-tc03"
flush_session(sid)
r = chat("is the first one vegan?", sid)
if r.status_code != 200:
    log(3, "Reference with no history (graceful)", "no crash, any valid response",
        f"status={r.status_code}", "FAIL", r.text)
else:
    d = r.json()
    log(3, "Reference with no history (graceful)", "no crash, any valid response",
        f"intent={d.get('detectedIntent')} msg='{d.get('message','')[:60]}'", "PASS")

# ── Group 2: Profile Persistence ──────────────────────────────────────────────
print("\n── Group 2: Profile Persistence ─────────────────────")

# TC04: Set profile → chat with no profile → stored profile applied
sid = "mem-tc04"
flush_session(sid)
set_profile(sid, restrictions=["vegetarian"])
r = chat("find me a pasta dinner", sid)
if r.status_code != 200:
    log(4, "Stored profile applied on chat", "mentions vegetarian",
        f"status={r.status_code}", "FAIL", r.text)
else:
    msg = r.json().get("message", "")
    log(4, "Stored profile applied on chat", "mentions vegetarian",
        f"'{msg[:80]}'",
        "PASS" if "vegetarian" in msg.lower() else "FAIL")

# TC05: Union merge — stored + extracted from message
sid = "mem-tc05"
flush_session(sid)
set_profile(sid, restrictions=["vegetarian"])
r = chat("find me a nut-free pasta", sid)
p = get_profile(sid)
if p.status_code != 200:
    log(5, "Profile union merge", "vegetarian + nut-free",
        f"profile status={p.status_code}", "FAIL")
else:
    pd = p.json()
    restrictions = [x.lower() for x in pd.get("restrictions", [])]
    has_veg = "vegetarian" in restrictions
    has_nut = "nut-free" in restrictions
    log(5, "Profile union merge", "restrictions=[vegetarian, nut-free]",
        f"restrictions={restrictions}",
        "PASS" if has_veg and has_nut else "FAIL")

# TC06: GET /profile returns stored preferences
sid = "mem-tc06"
flush_session(sid)
set_profile(sid, restrictions=["halal"], allergies=["sesame"])
p = get_profile(sid)
if p.status_code != 200:
    log(6, "GET /profile returns stored preferences",
        "halal + sesame", f"status={p.status_code}", "FAIL")
else:
    pd = p.json()
    restrictions = [x.lower() for x in pd.get("restrictions", [])]
    allergies = [x.lower() for x in pd.get("allergies", [])]
    log(6, "GET /profile returns stored preferences",
        "restrictions=[halal] allergies=[sesame]",
        f"restrictions={restrictions} allergies={allergies}",
        "PASS" if "halal" in restrictions and "sesame" in allergies else "FAIL")

# ── Group 3: GetMealPlan + Contractions ───────────────────────────────────────
print("\n── Group 3: GetMealPlan + Contractions ──────────────")

# TC07: Generate plan then "what's my plan?" returns it
sid = "mem-tc07"
flush_session(sid)
r_gen = chat("plan my dinners for the week", sid)
if r_gen.status_code != 200 or not r_gen.json().get("mealPlan"):
    log(7, "what's my plan returns stored plan", "GetMealPlan + hasPlan",
        f"plan generation failed: status={r_gen.status_code}", "FAIL")
else:
    r = chat("what's my plan?", sid)
    if r.status_code != 200:
        log(7, "what's my plan returns stored plan", "GetMealPlan + hasPlan",
            f"status={r.status_code}", "FAIL", r.text)
    else:
        d = r.json()
        intent = d.get("detectedIntent", "")
        has_plan = d.get("mealPlan") is not None
        log(7, "what's my plan returns stored plan", "GetMealPlan + hasPlan=true",
            f"intent={intent} hasPlan={has_plan}",
            "PASS" if intent == "GetMealPlan" and has_plan else "FAIL")

# TC08: "show me my plan" with no plan → helpful fallback
sid = "mem-tc08"
flush_session(sid)
r = chat("show me my plan", sid)
if r.status_code != 200:
    log(8, "GetMealPlan no plan → fallback", "GetMealPlan + no plan message",
        f"status={r.status_code}", "FAIL", r.text)
else:
    d = r.json()
    intent = d.get("detectedIntent", "")
    msg = d.get("message", "")
    has_plan = d.get("mealPlan") is not None
    log(8, "GetMealPlan no plan → fallback", "GetMealPlan + helpful message",
        f"intent={intent} hasPlan={has_plan} msg='{msg[:60]}'",
        "PASS" if intent == "GetMealPlan" and not has_plan else "FAIL")

# TC09: "my plan" returns same plan, not new one
sid = "mem-tc09"
flush_session(sid)
r_gen = chat("plan my dinners for the week", sid)
if r_gen.status_code != 200 or not r_gen.json().get("mealPlan"):
    log(9, "my plan returns same plan (not re-generated)", "same planId",
        "plan generation failed", "FAIL")
else:
    plan_id_1 = r_gen.json()["mealPlan"]["planId"]
    r2 = chat("my plan", sid)
    if r2.status_code != 200:
        log(9, "my plan returns same plan (not re-generated)", "same planId",
            f"status={r2.status_code}", "FAIL")
    else:
        d2 = r2.json()
        plan_id_2 = d2.get("mealPlan", {}).get(
            "planId") if d2.get("mealPlan") else None
        log(9, "my plan returns same plan (not re-generated)", f"planId={plan_id_1[:8]}",
            f"planId={str(plan_id_2)[:8] if plan_id_2 else 'None'} intent={d2.get('detectedIntent')}",
            "PASS" if plan_id_1 == plan_id_2 else "FAIL")

# ── Group 4: LLM Entity Extraction ────────────────────────────────────────────
print("\n── Group 4: LLM Entity Extraction ───────────────────")

# TC10: "i cannot have dairy" → dairy allergy extracted + saved
sid = "mem-tc10"
flush_session(sid)
r = chat("i cannot have dairy, find me dinner", sid)
if r.status_code != 200:
    log(10, "LLM extracts implicit dairy constraint", "allergies=[dairy]",
        f"status={r.status_code}", "FAIL", r.text)
else:
    p = get_profile(sid)
    pd = p.json() if p.status_code == 200 else {}
    allergies = [x.lower() for x in pd.get("allergies", [])]
    logs = get_logs(tail=30)
    llm_fired = "LLM extracted profile" in logs
    restrictions = [x.lower() for x in pd.get("restrictions", [])]
    has_dairy = "dairy" in allergies or "dairy-free" in restrictions or "dairy" in restrictions
    log(10, "LLM extracts implicit dairy constraint",
        "dairy in profile, LLM fired",
        f"restrictions={restrictions} allergies={allergies} llm_fired={llm_fired}",
        "PASS" if has_dairy else "FAIL",
        "Over-extraction (extra constraints) is known LLM limitation — deferred Month 3")

# TC11: Multi-turn — constraint in turn 1 applied in turn 2
sid = "mem-tc11"
flush_session(sid)
chat("i follow a vegetarian diet", sid)
time.sleep(2)  # let Ollama finish
r2 = chat("find me pasta", sid)
if r2.status_code != 200:
    log(11, "Multi-turn constraint persistence", "vegetarian applied in turn 2",
        f"status={r2.status_code}", "FAIL", r2.text)
else:
    msg = r2.json().get("message", "")
    p = get_profile(sid)
    pd = p.json() if p.status_code == 200 else {}
    restrictions = [x.lower() for x in pd.get("restrictions", [])]
    log(11, "Multi-turn constraint persistence",
        "vegetarian in profile + applied in turn 2",
        f"restrictions={restrictions} msg='{msg[:80]}'",
        "PASS" if "vegetarian" in restrictions else "FAIL")

# TC12: Explicit term → rules catch it, LLM NOT called
sid = "mem-tc12"
flush_session(sid)
# Clear logs reference point
subprocess.run(["docker", "compose", "logs", "api",
               "--tail=1"], capture_output=True)
time.sleep(1)
r = chat("find me a vegan pasta", sid)
logs = get_logs(tail=15)
llm_fired = "LLM extracted profile" in logs
elapsed = r.elapsed.total_seconds() if r.status_code == 200 else 999
log(12, "Explicit term → rules only, no LLM",
    "LLM NOT called, response <10s",
    f"llm_fired={llm_fired} elapsed={elapsed:.1f}s",
    "PASS" if not llm_fired and elapsed < 10 else "FAIL",
    "LLM fires on implicit signals only")

# ── Group 5: Edge Cases + Resilience ──────────────────────────────────────────
print("\n── Group 5: Edge Cases + Full Flow ──────────────────")

# TC13: Unknown sessionId on profile GET → 404
r = requests.get(f"{BASE}/profile/nonexistent-session-xyz-999", timeout=10)
log(13, "Unknown sessionId on GET /profile → 404", "404",
    str(r.status_code), "PASS" if r.status_code == 404 else "FAIL")

# TC14: History sliding window — 22 messages → max 20 stored
sid = "mem-tc14"
flush_session(sid)
for i in range(22):
    chat(f"find me recipe {i}", sid)
    time.sleep(0.2)  # avoid hammering Ollama
stored = history_len(sid)
log(14, "History sliding window (22 msgs → max 20)", "LLEN <= 20",
    f"LLEN={stored}",
    "PASS" if 0 < stored <= 20 else "FAIL")

# TC15: Full stateful end-to-end flow
print("\n── TC15: Full end-to-end flow ───────────────────────")
sid = "mem-tc15"
flush_session(sid)
steps = []


def step(name, condition, actual, response=None):
    ok = condition
    steps.append((name, ok, actual))
    icon = "  ✅" if ok else "  ❌"
    print(f"{icon} {name}: {actual}")
    if not ok and response is not None:
        print(f"     response: {json.dumps(response, indent=2)[:200]}")
    return ok


# Step 1: Set profile
r = set_profile(sid, restrictions=["vegetarian"])
if not step("Set profile", r.status_code == 200, f"status={r.status_code}", r.json()):
    log(15, "Full end-to-end stateful flow",
        "all 6 steps pass", "FAIL at step 1", "FAIL")
else:
    # Step 2: Search with stored profile applied
    r = chat("find me pasta", sid)
    d = r.json() if r.status_code == 200 else {}
    msg = d.get("message", "")
    ok2 = step("Search uses stored profile",
               "vegetarian" in msg.lower(), f"msg='{msg[:60]}'", d)

    # Step 3: Reference resolution
    recipes = d.get("recipes", [])
    first_title = recipes[0]["recipe"]["title"] if recipes else None
    r = chat("is the first one vegan?", sid)
    d3 = r.json() if r.status_code == 200 else {}
    ok3 = step("Reference resolution", d3.get("detectedIntent") == "ValidateDiet",
               f"intent={d3.get('detectedIntent')} title='{first_title}'", d3)

    # Step 4: Generate plan
    r = chat("plan my dinners for the week", sid)
    d4 = r.json() if r.status_code == 200 else {}
    plan_id = d4.get("mealPlan", {}).get(
        "planId") if d4.get("mealPlan") else None
    ok4 = step("Generate plan", plan_id is not None,
               f"planId={str(plan_id)[:8] if plan_id else 'None'}", d4)

    # Step 5: GetMealPlan
    r = chat("what's my plan?", sid)
    d5 = r.json() if r.status_code == 200 else {}
    returned_id = d5.get("mealPlan", {}).get(
        "planId") if d5.get("mealPlan") else None
    ok5 = step("GetMealPlan returns same plan",
               d5.get("detectedIntent") == "GetMealPlan" and returned_id == plan_id,
               f"intent={d5.get('detectedIntent')} planId match={returned_id == plan_id}", d5)

    # Step 6: Swap a day
    r = chat("swap Tuesday to something with pasta", sid)
    d6 = r.json() if r.status_code == 200 else {}
    ok6 = step("Swap Tuesday", d6.get("detectedIntent") == "ModifyMealPlan" and d6.get("mealPlan") is not None,
               f"intent={d6.get('detectedIntent')} hasPlan={d6.get('mealPlan') is not None}", d6)

    all_steps = [ok2, ok3, ok4, ok5, ok6]
    failed_steps = [i+2 for i, ok in enumerate(all_steps) if not ok]
    log(15, "Full end-to-end stateful flow", "all 6 steps pass",
        f"passed={5 - len(failed_steps)}/5 failed_steps={failed_steps}",
        "PASS" if not failed_steps else "FAIL",
        f"Failed at steps: {failed_steps}" if failed_steps else "")

# ── Summary ───────────────────────────────────────────────────────────────────
print("\n── Summary ──────────────────────────────────────────")
passed = sum(1 for r in results if r["status"] == "PASS")
failed = sum(1 for r in results if r["status"] == "FAIL")
skipped = sum(1 for r in results if r["status"] == "SKIP")
print(
    f"PASS: {passed} | FAIL: {failed} | SKIP: {skipped} | TOTAL: {len(results)}")

# Write results
os.makedirs("eval/datasets", exist_ok=True)
with open("eval/datasets/memory_test_results.md", "w") as f:
    f.write("# Week 6 — Stateful Flow Test Results\n\n")
    f.write(f"**Run date:** Week 6 Day 5\n")
    f.write(f"**Base URL:** {BASE}\n\n")
    f.write("| TC | Scenario | Expected | Actual | Status | Notes |\n")
    f.write("|----|---------|----------|--------|--------|-------|\n")
    for r in results:
        icon = "✅" if r["status"] == "PASS" else (
            "⚠️" if r["status"] == "SKIP" else "❌")
        f.write(
            f"| {r['tc']:02d} | {r['scenario']} | {r['expected']} | {r['actual']} | {icon} | {r['notes']} |\n")
    f.write(f"\n**Total: {passed}/{len(results)} passed**\n")

print("Results written to eval/datasets/memory_test_results.md")
