"""
score_ragas.py — RAGAS scoring, Claude judge + Voyage embeddings.
Reads eval/datasets/retrieved_contexts.json (regenerated against /chat),
writes eval/experiments/<date>_final_portfolio.json.

Metrics (ragas 0.1.21): context_precision, faithfulness, answer_relevancy.
Empty-context records (degenerate/nonsense queries) are excluded from scoring
and reported separately.
"""

import json
from datetime import date, datetime, timezone
from datasets import Dataset
from ragas import evaluate
from ragas.metrics import context_precision, faithfulness, answer_relevancy
from ragas.llms import LangchainLLMWrapper
from ragas.embeddings import LangchainEmbeddingsWrapper
from ragas.run_config import RunConfig
from langchain_anthropic import ChatAnthropic
from langchain_voyageai import VoyageAIEmbeddings

CONTEXTS_PATH = "eval/datasets/retrieved_contexts.json"
OUT_PATH = f"eval/experiments/{date.today().isoformat()}_final_portfolio.json"

judge = LangchainLLMWrapper(ChatAnthropic(
    model="claude-sonnet-4-6", temperature=0.0, max_tokens=1024,
))
emb = LangchainEmbeddingsWrapper(VoyageAIEmbeddings(model="voyage-4-lite"))


def load_records():
    with open(CONTEXTS_PATH) as f:
        rows = json.load(f)["records"]
    scored, skipped = [], []
    for r in rows:
        ctx = r.get("contexts") or []
        if ctx == ["No results found."] or not ctx or not r.get("answer"):
            skipped.append(r["question"])
        else:
            scored.append(r)
    return scored, skipped


def main():
    rows, skipped = load_records()
    print(f"Scoring {len(rows)} records "
          f"({len(skipped)} skipped: empty contexts / no answer)")
    if skipped:
        print("  Skipped:", ", ".join(q[:30] for q in skipped))

    ds = Dataset.from_dict({
        "question":     [r["question"] for r in rows],
        "answer":       [r["answer"] for r in rows],
        "contexts":     [r["contexts"] for r in rows],
        "ground_truth": [r["ground_truth"] for r in rows],
    })
    categories = [r["category"] for r in rows]

    run_config = RunConfig(timeout=300, max_workers=2)  # Voyage 3 RPM + Anthropic limits

    result = evaluate(
        dataset=ds,
        metrics=[context_precision, faithfulness, answer_relevancy],
        llm=judge,
        embeddings=emb,
        run_config=run_config,
        raise_exceptions=False,
    )

    df = result.to_pandas()
    df["category"] = categories

    def col(name):  # pandas mean, NaN-safe
        return round(float(df[name].mean(skipna=True)), 3)

    overall = {
        "context_precision": col("context_precision"),
        "faithfulness":      col("faithfulness"),
        "answer_relevancy":  col("answer_relevancy"),
    }
    per_category = {}
    for cat in sorted(set(categories)):
        sub = df[df["category"] == cat]
        per_category[cat] = {
            "context_precision": round(float(sub["context_precision"].mean(skipna=True)), 3),
            "faithfulness":      round(float(sub["faithfulness"].mean(skipna=True)), 3),
            "answer_relevancy":  round(float(sub["answer_relevancy"].mean(skipna=True)), 3),
            "count": int(len(sub)),
        }

    out = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "experiment": "final_portfolio",
        "judge_model": "claude-sonnet-4-6",
        "embeddings_model": "voyage-4-lite",
        "ragas_version": "0.1.21",
        "scored_count": int(len(df)),
        "skipped_count": len(skipped),
        "skipped_questions": skipped,
        "overall": overall,
        "per_category": per_category,
        "per_question": json.loads(df.to_json(orient="records")),
    }
    with open(OUT_PATH, "w") as f:
        json.dump(out, f, indent=2)

    print(f"\nContext Precision: {overall['context_precision']}")
    print(f"Faithfulness:      {overall['faithfulness']}")
    print(f"Answer Relevancy:  {overall['answer_relevancy']}")
    print(f"\nSaved → {OUT_PATH}")


if __name__ == "__main__":
    main()