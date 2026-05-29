"""
ChefAgent — Week 7, Day 3: Circuit Breaker Test Suite
Interactive test — requires manually stopping/starting Ollama.

Usage:
    python3 test_circuit_breaker.py [base_url]
    Default base_url: http://localhost:5100

Test flow:
    Phase 1: Normal operation (Ollama running)
    Phase 2: Stop Ollama → trigger failures → watch breaker trip
    Phase 3: Verify fast responses while circuit is open
    Phase 4: Restart Ollama → wait for cooldown → verify recovery
"""

import requests
import sys
import time
import subprocess

BASE_URL = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5100"
SESSION = f"cb-test-{int(time.time())}"

PASS = "✅"
FAIL = "❌"
INFO = "ℹ️"

results = []


def chat(message, session_id=None):
    payload = {"message": message, "sessionId": session_id or SESSION}
    try:
        start = time.time()
        r = requests.post(f"{BASE_URL}/chat", json=payload, timeout=120)
        elapsed = time.time() - start
        return r.status_code, r.json(), elapsed
    except Exception as e:
        return 0, {"error": str(e)}, 0


def record(tc_id, name, passed, detail="", notes=""):
    status = PASS if passed else FAIL
    results.append({"tc": tc_id, "name": name, "status": status})
    print(f"  {status} TC{tc_id:02d} | {name}")
    if detail:
        print(f"       {detail}")
    if notes:
        print(f"       Note: {notes}")
    print()


# ══════════════════════════════════════════════════════════════════
# Phase 1: Normal operation — Ollama is running
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("PHASE 1: Normal Operation (Ollama running)")
print("=" * 65)
print()

# TC01 — Recipe search works (no LLM needed)
code, resp, elapsed = chat("find me a chicken dinner")
record(1, "Recipe search works normally",
       code == 200 and len(resp.get("recipes", [])) > 0,
       f"Status={code}, recipes={len(resp.get('recipes', []))}, time={elapsed:.1f}s")

# TC02 — GeneralQuestion works (LLM required)
code, resp, elapsed = chat("what is a roux?")
# real LLM answer is longer than error msg
has_answer = len(resp.get("message", "")) > 20
record(2, "GeneralQuestion works (LLM call succeeds)",
       code == 200 and has_answer,
       f"Status={code}, answer_len={len(resp.get('message', ''))}, time={elapsed:.1f}s")

# ══════════════════════════════════════════════════════════════════
# Phase 2: Stop Ollama → trigger circuit breaker
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("PHASE 2: Triggering Circuit Breaker")
print("=" * 65)
print()
print(f"  {INFO} Stopping Ollama...")

try:
    subprocess.run(
        ["docker", "compose", "stop", "ollama"],
        capture_output=True, timeout=30,
        cwd="/workspaces/ChefAgent"
    )
    print(f"  {INFO} Ollama stopped via docker compose")
except Exception:
    print(f"  {INFO} Could not auto-stop. Run manually:")
    print(f"       docker compose stop ollama")
    input(f"  {INFO} Press Enter when Ollama is stopped...")

print()
time.sleep(2)  # let the container fully stop

# TC03-05 — Send 3 GeneralQuestion requests to trip the breaker
# Each should fail (Ollama down) and increment failure count
for i, tc_num in enumerate([3, 4, 5]):
    code, resp, elapsed = chat(f"what is braising? attempt {i+1}")
    msg = resp.get("message", "")
    is_error = "unavailable" in msg.lower(
    ) or "couldn't" in msg.lower() or "error" in str(resp).lower()
    record(tc_num, f"LLM failure #{i+1} (Ollama down)",
           code == 200,  # should still return 200, not 500
           f"Status={code}, time={elapsed:.1f}s, graceful={'yes' if is_error else 'no'}",
           notes="Should return 200 with error message, never 500")

# ══════════════════════════════════════════════════════════════════
# Phase 3: Circuit should be OPEN — LLM calls skipped
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("PHASE 3: Circuit Open — LLM Calls Should Be Skipped")
print("=" * 65)
print()

# TC06 — Recipe search still works (doesn't need LLM)
code, resp, elapsed = chat("find me pasta recipes")
record(6, "Recipe search works with circuit open",
       code == 200 and len(resp.get("recipes", [])) > 0,
       f"Status={code}, recipes={len(resp.get('recipes', []))}, time={elapsed:.1f}s",
       notes="Vector search doesn't need Ollama")

# TC07 — GeneralQuestion should fail FAST (circuit skips LLM)
code, resp, elapsed = chat("what is sauteing?")
record(7, "GeneralQuestion fails fast (circuit open, no LLM attempt)",
       code == 200 and elapsed < 5.0,  # should be near-instant, not 30s timeout
       f"Status={code}, time={elapsed:.1f}s (should be <5s, not 30s timeout)",
       notes="Circuit open = skip LLM entirely, respond immediately")

# TC08 — Another fast fail to confirm pattern
code, resp, elapsed = chat("explain deglazing")
record(8, "Second fast fail confirms circuit is open",
       code == 200 and elapsed < 5.0,
       f"Status={code}, time={elapsed:.1f}s")

# ══════════════════════════════════════════════════════════════════
# Phase 4: Restart Ollama → wait for cooldown → verify recovery
# ══════════════════════════════════════════════════════════════════
print("=" * 65)
print("PHASE 4: Recovery After Restart")
print("=" * 65)
print()
print(f"  {INFO} Restarting Ollama...")

try:
    subprocess.run(
        ["docker", "compose", "start", "ollama"],
        capture_output=True, timeout=30,
        cwd="/workspaces/ChefAgent"
    )
    print(f"  {INFO} Ollama restarted via docker compose")
except Exception:
    print(f"  {INFO} Could not auto-restart. Run manually:")
    print(f"       docker compose start ollama")
    input(f"  {INFO} Press Enter when Ollama is running...")

# Wait for cooldown (60s) + Ollama startup
print(f"  {INFO} Waiting 70s for circuit breaker cooldown (60s) + Ollama startup...")
for i in range(70, 0, -10):
    print(f"       {i}s remaining...", flush=True)
    time.sleep(10)
print()

# TC09 — GeneralQuestion should work again (HalfOpen → test → Closed)
code, resp, elapsed = chat("what is a roux?")
has_answer = len(resp.get("message", "")) > 20
record(9, "GeneralQuestion works after recovery (circuit closed)",
       code == 200 and has_answer,
       f"Status={code}, answer_len={len(resp.get('message', ''))}, time={elapsed:.1f}s",
       notes="HalfOpen → test call succeeds → circuit closes")

# TC10 — Second call confirms circuit is fully closed
code, resp, elapsed = chat("what is blanching?")
has_answer = len(resp.get("message", "")) > 20
record(10, "Second GeneralQuestion confirms circuit closed",
       code == 200 and has_answer,
       f"Status={code}, answer_len={len(resp.get('message', ''))}, time={elapsed:.1f}s")

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
