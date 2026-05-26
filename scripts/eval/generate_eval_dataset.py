#!/usr/bin/env python3
"""
Generate Evaluation Dataset
============================
Runs test queries through the ChefAgent search API and generates
a markdown file for manual relevance rating.

Usage:
    python scripts/generate_eval_dataset.py

Prerequisites:
    - API running: cd src/api && dotnet run
    - Ollama running: ollama serve
    - Qdrant running: docker compose up -d qdrant
"""

import requests
from datetime import datetime

API_URL = "http://localhost:5000/recipes/search"

EVAL_QUERIES = [
    # === Original 12 ===
    # Exact Match
    {"category": "Exact Match", "query": "chocolate chip cookies"},
    {"category": "Exact Match", "query": "banana bread"},
    # By Ingredients
    {"category": "By Ingredients", "query": "what can I make with chicken and rice"},
    {"category": "By Ingredients", "query": "recipes using eggs cheese and spinach"},
    # Dietary
    {"category": "Dietary", "query": "vegetarian pasta dinner"},
    {"category": "Dietary", "query": "dessert without dairy"},
    # Cuisine
    {"category": "Cuisine", "query": "spicy Mexican dinner"},
    {"category": "Cuisine", "query": "Italian soup"},
    # Situation
    {"category": "Situation", "query": "quick easy weeknight meal"},
    {"category": "Situation", "query": "something warm and comforting for winter"},
    # Irrelevant
    {"category": "Irrelevant", "query": "how to change a car tire"},
    {"category": "Irrelevant", "query": "python programming tutorial for beginners"},

    # === 12 New Edge Cases ===
    # Misspellings
    {"category": "Misspelling", "query": "chiken noodle soop"},
    {"category": "Misspelling", "query": "macoroni and cheeze"},
    # Time / quantity constraints
    {"category": "Constraint", "query": "30 minute dinner for two"},
    {"category": "Constraint", "query": "5 ingredient easy lunch"},
    # Negative constraints
    {"category": "Negative", "query": "pasta without tomatoes"},
    {"category": "Negative", "query": "cookies without eggs"},
    # Multi-intent
    {"category": "Multi-Intent", "query": "healthy breakfast that kids will love"},
    {"category": "Multi-Intent", "query": "cheap high protein meal prep"},
    # Vague / conversational
    {"category": "Conversational", "query": "I'm bored of eating the same thing"},
    {"category": "Conversational", "query": "what should I bring to a potluck"},
    # Technique-based
    {"category": "Technique", "query": "slow cooker beef stew"},
    {"category": "Technique", "query": "grilled fish with lemon"},
]


def search(query: str, max_results: int = 3) -> list:
    try:
        resp = requests.post(
            API_URL,
            json={"query": query, "maxResults": max_results},
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json().get("recipes", [])
    except requests.ConnectionError:
        print("❌ Cannot connect to API at", API_URL)
        print("   Make sure the API is running: cd src/api && dotnet run")
        exit(1)
    except Exception as e:
        print(f"⚠️  Error for '{query}': {e}")
        return []


def main():
    print("🔍 Running 24 eval queries against ChefAgent API...")

    lines = []
    lines.append("# ChefAgent — Retrieval Evaluation Dataset")
    lines.append(f"\nGenerated: {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    lines.append(f"Collection: recipes (10K documents)")
    lines.append(f"Embedding model: nomic-embed-text (768 dim)")
    lines.append("")
    lines.append("## Rating Guide")
    lines.append("")
    lines.append("| Rating | Meaning |")
    lines.append("|--------|---------|")
    lines.append("| ✅ Relevant | Top result directly answers the query |")
    lines.append("| 🟡 Partial | Related but missing key aspects (wrong cuisine, has excluded ingredient) |")
    lines.append("| ❌ Bad | Irrelevant or nonsensical result |")
    lines.append("| ⬛ Expected Bad | Query is intentionally irrelevant — low score is correct |")
    lines.append("")

    current_category = ""

    for i, test in enumerate(EVAL_QUERIES, 1):
        if test["category"] != current_category:
            current_category = test["category"]
            lines.append(f"---\n")
            lines.append(f"## {current_category}\n")

        results = search(test["query"])
        print(f"  [{i}/24] {test['query']}")

        lines.append(f"### Q{i}: \"{test['query']}\"")
        lines.append("")
        lines.append("| Rank | Title | Score | Rating |")
        lines.append("|------|-------|-------|--------|")

        for j, r in enumerate(results, 1):
            title = r.get("title", "N/A")
            score = r.get("relevanceScore", 0)
            ingredients_preview = ", ".join(r.get("ingredients", [])[:4])
            lines.append(f"| {j} | {title} | {score:.4f} | <!-- YOUR RATING --> |")

        lines.append(f"\n**Top ingredients:** {ingredients_preview}")
        lines.append(f"**Notes:** <!-- Your observations -->\n")

    # Summary table
    lines.append("---\n")
    lines.append("## Summary\n")
    lines.append("| Category | Queries | Avg Top Score | Overall Rating |")
    lines.append("|----------|---------|---------------|----------------|")

    category_scores = {}
    idx = 0
    for test in EVAL_QUERIES:
        cat = test["category"]
        results = search(test["query"])
        top_score = results[0]["relevanceScore"] if results else 0
        if cat not in category_scores:
            category_scores[cat] = []
        category_scores[cat].append(top_score)

    for cat, scores in category_scores.items():
        avg = sum(scores) / len(scores) if scores else 0
        lines.append(f"| {cat} | {len(scores)} | {avg:.4f} | <!-- RATE --> |")

    lines.append(f"\n**Month 1 baseline — revisit with RAGAS eval in Month 3**\n")

    # Write file
    output = "eval/datasets/retrieval_baseline.md"
    import os
    os.makedirs("eval/datasets", exist_ok=True)
    with open(output, "w") as f:
        f.write("\n".join(lines))

    print(f"\n✅ Eval dataset saved to {output}")
    print(f"   Open it and fill in your ratings in the 'Rating' column")
    print(f"   Add observations in the 'Notes' sections")


if __name__ == "__main__":
    main()