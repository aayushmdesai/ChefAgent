#!/usr/bin/env python3
"""
Recipe Data Pipeline — Unified 50k
====================================
Combines two datasets into a single 50k recipe JSONL file:
  - corbt/all-recipes      (~44,062 western recipes)
  - Anupam007/indian-recipe-dataset (~5,938 Indian recipes)

Output schema is identical to the existing pipeline so generate_embeddings.py
and load_qdrant.py require zero changes.

New field added: `cuisine` ("indian" | "western") — available as a Qdrant
payload filter for future use.

Usage (Colab or local):
    pip install datasets pandas tqdm
    python prepare_recipes.py --output data/processed/recipes.jsonl

    # Custom split:
    python prepare_recipes.py --corbt-limit 44062 --indian-limit 5938
"""

import argparse
import hashlib
import json
import re
from pathlib import Path

from tqdm import tqdm


# ── Helpers ───────────────────────────────────────────────────────────────────

def make_id(title: str) -> str:
    return hashlib.md5(title.encode()).hexdigest()[:16]


def chunk_recipe(title: str, ingredients: list, directions: list, cuisine: str) -> dict:
    ingredients_text = "\n".join(f"- {ing}" for ing in ingredients)
    directions_text = "\n".join(f"{i+1}. {step}" for i, step in enumerate(directions))
    combined_text = f"""Recipe: {title}

Ingredients:
{ingredients_text}

Instructions:
{directions_text}"""

    return {
        "id": make_id(title),
        "title": title,
        "cuisine": cuisine,
        "ingredients": ingredients,
        "ingredients_text": ingredients_text,
        "directions": directions,
        "directions_text": directions_text,
        "combined_text": combined_text,
        "ingredient_count": len(ingredients),
        "step_count": len(directions),
    }


# ── corbt/all-recipes parser ──────────────────────────────────────────────────

def parse_corbt_row(text: str) -> dict | None:
    """
    Parse corbt's single `input` field.
    Format: "Title Ingredients: - item1 Directions: - step1"
    """
    ing_split = re.split(r'\bIngredients:\s*', text, maxsplit=1)
    if len(ing_split) < 2:
        return None

    title = ing_split[0].strip()
    remainder = ing_split[1]

    dir_split = re.split(r'\bDirections:\s*', remainder, maxsplit=1)
    if len(dir_split) < 2:
        return None

    ingredients = [i.strip() for i in re.split(r'\s*-\s+', dir_split[0].strip()) if i.strip()]
    directions  = [s.strip() for s in re.split(r'\s*-\s+', dir_split[1].strip()) if s.strip()]

    if not title or not ingredients or not directions:
        return None

    return {"title": title, "ingredients": ingredients, "directions": directions}


def load_corbt(limit: int) -> list[dict]:
    import pandas as pd

    print(f"\n📥 Loading corbt/all-recipes (limit={limit:,})...")
    url = (
        "https://huggingface.co/datasets/corbt/all-recipes/resolve/"
        "refs%2Fconvert%2Fparquet/default/train/0000.parquet"
    )
    df = pd.read_parquet(url).head(limit)
    print(f"   Downloaded {len(df):,} rows")

    docs, skipped = [], 0
    for _, row in tqdm(df.iterrows(), total=len(df), desc="Parsing corbt"):
        parsed = parse_corbt_row(row.get("input", ""))
        if parsed is None:
            skipped += 1
            continue
        docs.append(chunk_recipe(
            parsed["title"],
            parsed["ingredients"],
            parsed["directions"],
            cuisine="western",
        ))

    print(f"   ✅ {len(docs):,} parsed, {skipped:,} skipped")
    return docs


# ── Anupam007/indian-recipe-dataset parser ───────────────────────────────────

def parse_indian_ingredients(raw: str) -> list[str]:
    """
    Field is a comma-separated string of ingredients.
    e.g. "salt,cumin seeds,oil,onion,tomato"
    Some entries use ' ,' so strip carefully.
    """
    return [i.strip() for i in raw.split(",") if i.strip()]


def parse_indian_instructions(raw: str) -> list[str]:
    """
    Field is a period-separated string of steps.
    Split on '. ' but keep sentences intact.
    """
    steps = re.split(r'\.\s+', raw.strip())
    return [s.strip().rstrip(".") for s in steps if len(s.strip()) > 5]


def load_indian(limit: int) -> list[dict]:
    from datasets import load_dataset

    print(f"\n📥 Loading Anupam007/indian-recipe-dataset (limit={limit:,})...")
    ds = load_dataset("Anupam007/indian-recipe-dataset", split="train")
    print(f"   Downloaded {len(ds):,} rows")

    docs, skipped = [], 0
    for row in tqdm(ds, desc="Parsing Indian"):
        if len(docs) >= limit:
            break
        try:
            title        = (row.get("TranslatedRecipeName") or "").strip()
            ing_raw      = (row.get("TranslatedIngredients") or "").strip()
            dir_raw      = (row.get("TranslatedInstructions") or "").strip()

            if not title or not ing_raw or not dir_raw:
                skipped += 1
                continue

            ingredients = parse_indian_ingredients(ing_raw)
            directions  = parse_indian_instructions(dir_raw)

            if not ingredients or not directions:
                skipped += 1
                continue

            docs.append(chunk_recipe(title, ingredients, directions, cuisine="indian"))
        except Exception as e:
            skipped += 1

    print(f"   ✅ {len(docs):,} parsed, {skipped:,} skipped")
    return docs


# ── Dedup + merge ─────────────────────────────────────────────────────────────

def dedup(docs: list[dict]) -> list[dict]:
    seen, out = set(), []
    for doc in docs:
        if doc["id"] not in seen:
            seen.add(doc["id"])
            out.append(doc)
    return out


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Prepare unified 50k recipe dataset")
    parser.add_argument("--output",       default="data/processed/recipes.jsonl")
    parser.add_argument("--corbt-limit",  type=int, default=44062)
    parser.add_argument("--indian-limit", type=int, default=5938)
    args = parser.parse_args()

    indian_docs = load_indian(args.indian_limit)
    corbt_docs  = load_corbt(args.corbt_limit)

    all_docs = dedup(indian_docs + corbt_docs)
    print(f"\n🔀 Combined: {len(all_docs):,} unique recipes "
          f"(Indian: {sum(1 for d in all_docs if d['cuisine']=='indian'):,}, "
          f"Western: {sum(1 for d in all_docs if d['cuisine']=='western'):,})")

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with open(output_path, "w") as f:
        for doc in all_docs:
            f.write(json.dumps(doc) + "\n")

    size_mb = output_path.stat().st_size / 1024 / 1024
    print(f"\n✅ Written {len(all_docs):,} recipes → {output_path} ({size_mb:.1f} MB)")
    print("Next step: Run generate_embeddings.py")


if __name__ == "__main__":
    main()