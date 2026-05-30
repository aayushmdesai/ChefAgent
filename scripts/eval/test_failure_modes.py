#!/usr/bin/env python3
"""
ChefAgent — Failure Mode Matrix Test Script
Week 8, Day 1: Systematically break every service and document behavior.

Usage (from repo root):
    python3 scripts/eval/test_failure_modes.py

Fully automated — stops/starts Docker Compose services between scenarios.
Generates eval/datasets/failure_mode_matrix.md with full results.
"""

import requests
import subprocess
import time
import sys
from datetime import datetime

BASE_URL = "http://localhost:5100"
SESSION_ID = "failure-test"
RESULTS = []


# ─── HELPERS ──────────────────────────────────────────────────────────

def run_cmd(cmd: str, quiet: bool = True):
    """Run a shell command."""
    result = subprocess.run(cmd, shell=True, capture_output=quiet, text=True)
    if not quiet and result.returncode != 0:
        print(f"  ⚠️  Command failed: {cmd}")
    return result


def stop_services(*services: str):
    """Stop one or more Docker Compose services."""
    names = " ".join(services)
    print(f"  🔴 Stopping: {names}")
    run_cmd(f"docker compose stop {names}")
    time.sleep(3)


def start_services(*services: str, wait: int = 5):
    """Start one or more Docker Compose services and wait."""
    names = " ".join(services)
    print(f"  🟢 Starting: {names}")
    run_cmd(f"docker compose start {names}")
    print(f"  ⏳ Waiting {wait}s for startup...")
    time.sleep(wait)


def send_chat(message: str, session_id: str = SESSION_ID) -> dict:
    """Send a /chat request and capture status, body, and latency."""
    start = time.time()
    try:
        resp = requests.post(
            f"{BASE_URL}/chat",
            json={"message": message, "sessionId": session_id},
            timeout=120,
        )
        elapsed = time.time() - start
        try:
            body = resp.json()
        except Exception:
            body = {"raw": resp.text[:500]}
        return {
            "status": resp.status_code,
            "body": body,
            "latency_s": round(elapsed, 2),
            "error": None,
        }
    except requests.exceptions.ConnectionError:
        return {"status": None, "body": None, "latency_s": round(time.time() - start, 2),
                "error": "ConnectionError — API unreachable"}
    except requests.exceptions.Timeout:
        return {"status": None, "body": None, "latency_s": round(time.time() - start, 2),
                "error": "Timeout"}
    except Exception as e:
        return {"status": None, "body": None, "latency_s": round(time.time() - start, 2),
                "error": str(e)}


def check_health() -> bool:
    try:
        return requests.get(f"{BASE_URL}/health", timeout=5).status_code == 200
    except Exception:
        return False


def get_audit_events(event_filter: str = "") -> list:
    """Fetch guardrail audit events, optionally filtered."""
    try:
        resp = requests.get(f"{BASE_URL}/admin/guardrails", timeout=5)
        events = resp.json() if resp.status_code == 200 else []
        if event_filter:
            events = [e for e in events if event_filter in e.get(
                "eventType", "")]
        return events
    except Exception:
        return []


def record(scenario: str, tc_num: int, query: str, expected: str, result: dict):
    """Record a test case result."""
    status = result["status"]
    is_500 = status is not None and status >= 500
    body = result["body"] or {}
    message = body.get("message", body.get("raw", result.get("error", "")))
    intent = body.get("intent", "N/A")
    confidence = body.get("confidence", "N/A")
    msg_short = (str(message)[:120] +
                 "...") if len(str(message)) > 120 else str(message)

    passed = not is_500 and result["error"] is None
    status_display = "CONN_FAIL" if status is None else str(status)

    entry = {
        "scenario": scenario,
        "tc": tc_num,
        "query": query,
        "expected": expected,
        "status": status_display,
        "intent": intent,
        "confidence": confidence,
        "message": msg_short,
        "latency": result["latency_s"],
        "passed": passed,
        "is_500": is_500,
    }
    RESULTS.append(entry)

    icon = "✅" if passed else "❌"
    print(
        f"  TC{tc_num:02d} {icon} [{status_display}] {result['latency_s']}s — {msg_short[:80]}")
    if is_500:
        print(f"       ⚠️  GOT 500 — NEEDS FIXING")


# ─── SCENARIOS ────────────────────────────────────────────────────────

def scenario_baseline() -> int:
    """Scenario 0: All services up — establish baseline."""
    print("\n━━ SCENARIO 0: BASELINE (all services up) ━━")
    tc = 0

    tc += 1
    record("baseline", tc, "find me pasta recipes",
           "200 + recipes", send_chat("find me pasta recipes", "baseline-1"))
    tc += 1
    record("baseline", tc, "nut allergy check",
           "200 + diet validation", send_chat("is this recipe safe for someone with a nut allergy?", "baseline-1"))
    tc += 1
    record("baseline", tc, "plan my dinners",
           "200 + 7-day plan", send_chat("plan my dinners for the week", "baseline-1"))
    tc += 1
    record("baseline", tc, "get meal plan",
           "200 + plan from Redis", send_chat("what's my plan?", "baseline-1"))
    tc += 1
    record("baseline", tc, "general question",
           "200 + LLM answer", send_chat("what's the best way to cook steak?", "baseline-1"))
    return tc


def scenario_qdrant_down(tc_start: int) -> int:
    print("\n━━ SCENARIO 1: QDRANT DOWN ━━")
    stop_services("qdrant")
    tc = tc_start

    tc += 1
    record("qdrant_down", tc, "recipe search",
           "200 + search unavailable msg", send_chat("find me pasta recipes", "qdrant-down"))
    tc += 1
    record("qdrant_down", tc, "get meal plan (Redis only)",
           "200 + plan or no plan", send_chat("what's my plan?", "qdrant-down"))
    tc += 1
    record("qdrant_down", tc, "plan generation (needs search)",
           "200 + graceful error", send_chat("plan my dinners for the week", "qdrant-down"))
    tc += 1
    record("qdrant_down", tc, "general question (Ollama only)",
           "200 + LLM answer", send_chat("what's the best way to cook steak?", "qdrant-down"))
    tc += 1
    record("qdrant_down", tc, "diet check (no Qdrant needed)",
           "200 + diet result", send_chat("is chicken safe for a nut allergy?", "qdrant-down"))

    # Recovery
    start_services("qdrant", wait=8)
    tc += 1
    record("qdrant_down", tc, "recovery — search after restart",
           "200 + recipes", send_chat("find me pasta recipes", "qdrant-recovery"))
    return tc


def scenario_ollama_down(tc_start: int) -> int:
    print("\n━━ SCENARIO 2: OLLAMA DOWN ━━")
    stop_services("ollama")
    tc = tc_start

    tc += 1
    record("ollama_down", tc, "recipe search (no embedding)",
           "200 + 0 results or error msg", send_chat("find me pasta recipes", "ollama-down"))
    tc += 1
    record("ollama_down", tc, "general question (LLM down)",
           "200 + fallback msg", send_chat("what's the best way to cook steak?", "ollama-down"))
    tc += 1
    record("ollama_down", tc, "diet check (rules-only fallback)",
           "200 + rules result", send_chat("is this safe for nut allergy?", "ollama-down"))
    tc += 1
    record("ollama_down", tc, "get meal plan (Redis only)",
           "200 + plan or no plan", send_chat("what's my plan?", "ollama-down"))
    tc += 1
    record("ollama_down", tc, "plan generation (search will fail)",
           "200 + graceful error", send_chat("plan my dinners for the week", "ollama-down"))

    # Check circuit breaker state in audit log
    circuit_events = get_audit_events("circuit")
    print(f"  📋 Circuit breaker events in audit: {len(circuit_events)}")
    for e in circuit_events[-3:]:
        print(f"     {e.get('eventType')} — {e.get('detail', '')}")

    # Recovery — Ollama needs extra time to load models
    start_services("ollama", wait=20)
    tc += 1
    record("ollama_down", tc, "recovery — search after Ollama restart",
           "200 + recipes (may need model warm-up)", send_chat("find me pasta recipes", "ollama-recovery"))
    return tc


def scenario_redis_down(tc_start: int) -> int:
    print("\n━━ SCENARIO 3: REDIS DOWN ━━")
    stop_services("redis")
    tc = tc_start

    tc += 1
    record("redis_down", tc, "recipe search (stateless)",
           "200 + recipes, no profile", send_chat("find me pasta recipes", "redis-down"))
    tc += 1
    record("redis_down", tc, "get meal plan (Redis gone)",
           "200 + no plan found", send_chat("what's my plan?", "redis-down"))
    tc += 1
    record("redis_down", tc, "plan generation (can't persist)",
           "200 + plan returned but not saved", send_chat("plan my dinners", "redis-down"))
    tc += 1
    record("redis_down", tc, "reference resolution (no history)",
           "200 + can't resolve", send_chat("the first one", "redis-down"))
    tc += 1
    record("redis_down", tc, "general question (no Redis needed)",
           "200 + LLM answer", send_chat("what's the best way to cook steak?", "redis-down"))

    # Recovery
    start_services("redis", wait=5)
    tc += 1
    record("redis_down", tc, "recovery — full flow",
           "200 + recipes", send_chat("find me pasta recipes", "redis-recovery"))
    return tc


def scenario_qdrant_ollama_down(tc_start: int) -> int:
    print("\n━━ SCENARIO 4: QDRANT + OLLAMA DOWN ━━")
    stop_services("qdrant", "ollama")
    tc = tc_start

    tc += 1
    record("qdrant+ollama", tc, "recipe search (nothing works)",
           "200 + graceful error", send_chat("find me pasta recipes", "qo-down"))
    tc += 1
    record("qdrant+ollama", tc, "get meal plan (Redis still up)",
           "200 + plan or no plan", send_chat("what's my plan?", "qo-down"))
    tc += 1
    record("qdrant+ollama", tc, "general question (Ollama down)",
           "200 + fallback", send_chat("what's the best way to cook steak?", "qo-down"))
    tc += 1
    record("qdrant+ollama", tc, "diet check (rules only)",
           "200 + rules-only result", send_chat("is chicken safe for nut allergy?", "qo-down"))

    # Recovery
    start_services("qdrant", "ollama", wait=20)
    tc += 1
    record("qdrant+ollama", tc, "recovery — full restart",
           "200 + recipes", send_chat("find me pasta recipes", "qo-recovery"))
    return tc


def scenario_ollama_redis_down(tc_start: int) -> int:
    print("\n━━ SCENARIO 5: OLLAMA + REDIS DOWN ━━")
    stop_services("ollama", "redis")
    tc = tc_start

    tc += 1
    record("ollama+redis", tc, "recipe search (no embed + no profile)",
           "200 + graceful error", send_chat("find me pasta recipes", "or-down"))
    tc += 1
    record("ollama+redis", tc, "get meal plan (Redis down)",
           "200 + graceful error", send_chat("what's my plan?", "or-down"))
    tc += 1
    record("ollama+redis", tc, "general question (Ollama down)",
           "200 + fallback", send_chat("what's the best way to cook steak?", "or-down"))

    # Recovery
    start_services("ollama", "redis", wait=20)
    tc += 1
    record("ollama+redis", tc, "recovery",
           "200 + recipes", send_chat("find me pasta recipes", "or-recovery"))
    return tc


def scenario_all_down(tc_start: int) -> int:
    print("\n━━ SCENARIO 6: ALL SERVICES DOWN ━━")
    stop_services("qdrant", "ollama", "redis")
    tc = tc_start

    tc += 1
    record("all_down", tc, "recipe search",
           "200 + clear error, no stack trace", send_chat("find me pasta recipes", "all-down"))
    tc += 1
    record("all_down", tc, "get meal plan",
           "200 + clear error", send_chat("what's my plan?", "all-down"))
    tc += 1
    record("all_down", tc, "simple greeting",
           "200 + helpful response", send_chat("hello", "all-down"))

    # Full recovery
    start_services("qdrant", "ollama", "redis", wait=20)
    tc += 1
    record("all_down", tc, "full recovery",
           "200 + recipes", send_chat("find me pasta recipes", "all-recovery"))
    return tc


# ─── REPORT ───────────────────────────────────────────────────────────

def generate_report() -> str:
    now = datetime.now().strftime("%Y-%m-%d %H:%M")
    total = len(RESULTS)
    passed = sum(1 for r in RESULTS if r["passed"])
    failed_500 = sum(1 for r in RESULTS if r["is_500"])

    lines = [
        "# ChefAgent — Failure Mode Matrix",
        "",
        f"**Date:** {now}",
        f"**Total test cases:** {total}",
        f"**Passed (no 500s, no connection errors):** {passed}/{total}",
        f"**500 errors:** {failed_500}",
        "",
        "---",
        "",
    ]

    scenario_labels = {
        "baseline":        "Scenario 0: Baseline (All Services Up)",
        "qdrant_down":     "Scenario 1: Qdrant Down",
        "ollama_down":     "Scenario 2: Ollama Down",
        "redis_down":      "Scenario 3: Redis Down",
        "qdrant+ollama":   "Scenario 4: Qdrant + Ollama Down",
        "ollama+redis":    "Scenario 5: Ollama + Redis Down",
        "all_down":        "Scenario 6: All Services Down",
    }

    scenarios = {}
    for r in RESULTS:
        scenarios.setdefault(r["scenario"], []).append(r)

    for key, label in scenario_labels.items():
        if key not in scenarios:
            continue
        entries = scenarios[key]
        s_passed = sum(1 for e in entries if e["passed"])

        lines.append(f"## {label}")
        lines.append("")
        lines.append(f"**Result: {s_passed}/{len(entries)}**")
        lines.append("")
        lines.append(
            "| TC | Query | Expected | Status | Intent | Confidence | Latency | Pass |")
        lines.append(
            "|----|-------|----------|--------|--------|------------|---------|------|")
        for e in entries:
            icon = "✅" if e["passed"] else "❌"
            lines.append(
                f"| {e['tc']:02d} | {e['query'][:35]} | {e['expected'][:40]} "
                f"| {e['status']} | {e['intent']} | {e['confidence']} | {e['latency']}s | {icon} |"
            )

        lines.append("")
        lines.append("**User-visible messages:**")
        lines.append("")
        for e in entries:
            lines.append(f"- TC{e['tc']:02d}: `{e['message']}`")
        lines.append("")
        lines.append("---")
        lines.append("")

    # Degradation summary
    lines.extend([
        "## Degradation Summary",
        "",
        "| Scenario | Search | Diet | Plan Gen | Plan Read | General Q | Profile | 500s |",
        "|----------|--------|------|----------|-----------|-----------|---------|------|",
    ])
    # Auto-fill from results
    for key, label in scenario_labels.items():
        if key not in scenarios:
            continue
        short = key.replace("_down", "↓").replace("+", "+")
        entries = scenarios[key]
        has_500 = any(e["is_500"] for e in entries)
        lines.append(
            f"| {short} | — | — | — | — | — | — | {'YES ⚠️' if has_500 else 'No'} |")
    lines.extend(
        ["", "*(Fill in degradation details per cell after reviewing messages above)*", ""])

    # Key findings + fix paths
    lines.extend([
        "## Key Findings",
        "",
        "1. ",
        "2. **Critical insight:** Ollama down = no search (embedding dependency)",
        "3. **Recovery times:** ",
        "",
        "## Fix Paths (Month 3)",
        "",
        "| Issue | Fix | Priority |",
        "|-------|-----|----------|",
        "| Embedding requires Ollama | Embedding cache or keyword fallback | Medium |",
        "| Sequential plan gen amplifies failures | Parallel search + early exit | High |",
        "| | | |",
        "",
    ])

    return "\n".join(lines)


# ─── MAIN ─────────────────────────────────────────────────────────────

def main():
    print("=" * 60)
    print("  ChefAgent — Failure Mode Matrix")
    print("  Week 8 · Day 1")
    print("=" * 60)
    print()

    if not check_health():
        print("⚠️  API not reachable at", BASE_URL)
        print("   Run: make up && make health")
        sys.exit(1)
    print(f"✅ API healthy at {BASE_URL}\n")

    tc = scenario_baseline()
    tc = scenario_qdrant_down(tc)
    tc = scenario_ollama_down(tc)
    tc = scenario_redis_down(tc)
    tc = scenario_qdrant_ollama_down(tc)
    tc = scenario_ollama_redis_down(tc)
    tc = scenario_all_down(tc)

    # Final summary
    total = len(RESULTS)
    passed = sum(1 for r in RESULTS if r["passed"])
    failed_500 = sum(1 for r in RESULTS if r["is_500"])

    print("\n" + "=" * 60)
    print(f"  RESULTS: {passed}/{total} passed")
    if failed_500:
        print(f"  ⚠️  {failed_500} returned 500 — FIX THESE")
    else:
        print(f"  ✅ Zero 500 errors")
    print("=" * 60)

    report = generate_report()
    report_path = "eval/datasets/failure_mode_matrix.md"
    try:
        with open(report_path, "w") as f:
            f.write(report)
        print(f"\n📄 Report: {report_path}")
    except FileNotFoundError:
        with open("failure_mode_matrix.md", "w") as f:
            f.write(report)
        print(f"\n📄 Report: failure_mode_matrix.md (move to {report_path})")

    print("Done. Review the report, fill in the summary table, and commit.\n")


if __name__ == "__main__":
    main()
