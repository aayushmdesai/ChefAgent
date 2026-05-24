#!/usr/bin/env python3
"""
Recipe Data Pipeline
====================
Downloads corbt/all-recipes dataset from Hugging Face, parses and chunks
recipes into searchable documents for embedding + vector DB ingestion.

Usage:
    python scripts/prepare_recipes.py --output data/processed/recipes.jsonl --limit 10000

Dependencies:
    pip install datasets pandas tqdm
"""

import argparse
import json
import hashlib
import re
from pathlib import Path


def download_dataset(limit: int | None = None):
    """Download corbt/all-recipes parquet directly from Hugging Face."""
    import pandas as pd

    print("📥 Downloading corbt/all-recipes dataset from Hugging Face...")
    url = "https://huggingface.co/datasets/corbt/all-recipes/resolve/refs%2Fconvert%2Fparquet/default/train/0000.parquet"
    df = pd.read_parquet(url)

    if limit:
        df = df.head(limit)
        print(f"   Limited to {len(df)} recipes")
    else:
        print(f"   Loaded {len(df)} recipes")

    return df

def parse_recipe_text(text: str) -> dict:
    """
    Parse the single 'input' field from corbt/all-recipes into structured fields.

    Format: "Title Ingredients: - item1 - item2 Directions: - step1 - step2"
    """
    # Split on "Ingredients:" and "Directions:"
    ing_split = re.split(r'\bIngredients:\s*', text, maxsplit=1)
    if len(ing_split) < 2:
        return None

    title = ing_split[0].strip()
    remainder = ing_split[1]

    dir_split = re.split(r'\bDirections:\s*', remainder, maxsplit=1)
    if len(dir_split) < 2:
        return None

    ingredients_raw = dir_split[0].strip()
    directions_raw = dir_split[1].strip()

    # Parse ingredients: split on " - " pattern (each ingredient starts with "- ")
    ingredients = [
        ing.strip() for ing in re.split(r'\s*-\s+', ingredients_raw)
        if ing.strip()
    ]

    # Parse directions: split on " - " pattern
    directions = [
        step.strip() for step in re.split(r'\s*-\s+', directions_raw)
        if step.strip()
    ]

    return {
        "title": title,
        "ingredients": ingredients,
        "directions": directions,
    }


def chunk_recipe(parsed: dict) -> dict:
    """
    Transform a parsed recipe into a structured document for indexing.

    Produces a single document per recipe with fields optimized for:
    - Vector search (combined text field for embedding)
    - Keyword search (title, individual ingredients)
    - Filtering (ingredient count, step count)
    - Display (structured fields for UI rendering)
    """
    title = parsed["title"]
    ingredients = parsed["ingredients"]
    directions = parsed["directions"]

    # Build combined text for embedding
    ingredients_text = "\n".join(f"- {ing}" for ing in ingredients)
    directions_text = "\n".join(
        f"{i+1}. {step}" for i, step in enumerate(directions)
    )

    combined_text = f"""Recipe: {title}

Ingredients:
{ingredients_text}

Instructions:
{directions_text}"""

    # Generate stable document ID from title
    doc_id = hashlib.md5(f"{title}".encode()).hexdigest()[:16]

    return {
        "id": doc_id,
        "title": title,
        "ingredients": ingredients,
        "ingredients_text": ingredients_text,
        "directions": directions,
        "directions_text": directions_text,
        "combined_text": combined_text,
        "ingredient_count": len(ingredients),
        "step_count": len(directions),
    }


def main():
    parser = argparse.ArgumentParser(
        description="Prepare recipe dataset for vector DB ingestion"
    )
    parser.add_argument(
        "--output",
        type=str,
        default="data/processed/recipes.jsonl",
        help="Output path for processed recipes (JSONL)",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Limit number of recipes to process (for dev/testing)",
    )
    args = parser.parse_args()

    # Download
    ds = download_dataset(limit=args.limit)

    # Process
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    print(f"🔪 Chunking {len(ds)} recipes...")
    processed = 0
    skipped = 0

    with open(output_path, "w") as f:
        for _, row in ds.iterrows():
            try:
                text = row.get("input", "")
                parsed = parse_recipe_text(text)
                if parsed is None:
                    skipped += 1
                    continue

                doc = chunk_recipe(parsed)

                # Skip recipes with missing critical fields
                if not doc["title"] or not doc["ingredients"] or not doc["directions"]:
                    skipped += 1
                    continue

                f.write(json.dumps(doc) + "\n")
                processed += 1
            except Exception as e:
                skipped += 1
                if skipped <= 5:
                    print(f"   ⚠️  Skipped recipe: {e}")

    print(f"✅ Processed {processed} recipes, skipped {skipped}")
    print(f"   Output: {output_path}")
    print(f"   Size: {output_path.stat().st_size / 1024 / 1024:.1f} MB")
    print(f"\nNext step: Run scripts/generate_embeddings.py to create vectors")


if __name__ == "__main__":
    main()