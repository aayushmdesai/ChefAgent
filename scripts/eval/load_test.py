#!/usr/bin/env python3
"""
Load test — 10 concurrent requests against Railway deployment.
Documents p95 latency and success rate for portfolio.
"""
import asyncio
import aiohttp
import time
import statistics

RAILWAY_URL = "https://chefagent-production.up.railway.app/chat"

QUERIES = [
    "chicken dinner",
    "vegan soup",
    "pasta recipes",
    "what is a roux",
    "plan my dinners",
    "dairy-free cookies",
    "gluten-free bread",
    "quick breakfast ideas",
    "find me a salad",
    "mexican dinner",
]

async def send_query(session, query, i):
    start = time.time()
    try:
        async with session.post(
            RAILWAY_URL,
            json={"message": query, "sessionId": f"load-{i}"},
            timeout=aiohttp.ClientTimeout(total=30)
        ) as resp:
            await resp.json()
            elapsed = (time.time() - start) * 1000
            return {"query": query, "status": resp.status, "latency_ms": round(elapsed), "error": None}
    except Exception as e:
        elapsed = (time.time() - start) * 1000
        return {"query": query, "status": 0, "latency_ms": round(elapsed), "error": str(e)}

async def main():
    print(f"Sending {len(QUERIES)} concurrent requests to Railway...\n")
    async with aiohttp.ClientSession() as session:
        tasks = [send_query(session, q, i) for i, q in enumerate(QUERIES)]
        results = await asyncio.gather(*tasks)

    successes = [r for r in results if r["status"] == 200]
    failures = [r for r in results if r["status"] != 200]
    latencies = [r["latency_ms"] for r in successes]

    print(f"{'Query':<30} {'Status':>6} {'Latency':>10}")
    print("-" * 50)
    for r in results:
        status = "✅" if r["status"] == 200 else "❌"
        print(f"{r['query']:<30} {status:>6} {r['latency_ms']:>8}ms")

    print(f"\n{'─'*50}")
    print(f"Success rate : {len(successes)}/{len(results)}")
    if latencies:
        print(f"p50 latency  : {round(statistics.median(latencies))}ms")
        print(f"p95 latency  : {round(sorted(latencies)[int(len(latencies)*0.95)])if len(latencies) > 1 else latencies[0]}ms")
        print(f"Max latency  : {max(latencies)}ms")
        print(f"Min latency  : {min(latencies)}ms")
    if failures:
        print(f"\nFailures:")
        for r in failures:
            print(f"  {r['query']}: {r['error']}")

asyncio.run(main())