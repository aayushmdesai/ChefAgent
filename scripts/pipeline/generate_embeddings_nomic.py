#!/usr/bin/env python3
"""
Embedding Generator — Nomic Atlas API
======================================
Reads processed recipes from JSONL, generates embeddings via Nomic Atlas API
(nomic-embed-text-v1), and outputs vectors ready for Qdrant ingestion.

Mirrors NomicEmbeddingProvider.cs exactly:
  - Endpoint: https://api-atlas.nomic.ai/v1/embedding/text
  - Model:    nomic-embed-text-v1
  - Prefix:   search_document: (for stored vectors; queries use search_query:)

Usage (Colab):
    !pip install requests tqdm
    !python generate_embeddings_nomic.py \
        --input recipes.jsonl \
        --output recipe_vectors.jsonl \
        --api-key YOUR_NOMIC_API_KEY

Dependencies:
    pip install requests tqdm
"""

import argparse
import json
import time
import requests
from pathlib import Path
from tqdm import tqdm


NOMIC_URL   = "https://api-atlas.nomic.ai/v1/embedding/text"
NOMIC_MODEL = "nomic-embed-text-v1"
PREFIX      = "search_document: "   # asymmetric search — docs use this prefix
MAX_RETRIES = 3
RETRY_DELAY = 5   # seconds between retries on rate limit / 5xx


def embed_batch(texts: list[str], api_key: str) -> list[list[float]]:
    """Call Nomic Atlas API for a batch of texts. Returns list of vectors."""
    prefixed = [PREFIX + t for t in texts]

    for attempt in range(MAX_RETRIES):
        response = requests.post(
            NOMIC_URL,
            headers={"Authorization": f"Bearer {api_key}"},
            json={"model": NOMIC_MODEL, "texts": prefixed},
        )

        if response.status_code == 200:
            return response.json()["embeddings"]

        if response.status_code == 429 or response.status_code >= 500:
            wait = RETRY_DELAY * (attempt + 1)
            print(f"\n⚠️  HTTP {response.status_code} — retrying in {wait}s...")
            time.sleep(wait)
            continue

        response.raise_for_status()

    raise RuntimeError(f"Failed after {MAX_RETRIES} retries")


def main():
    parser = argparse.ArgumentParser(description="Generate Nomic embeddings for recipe documents")
    parser.add_argument("--input",      default="recipes.jsonl",         help="Input JSONL from prepare_recipes.py")
    parser.add_argument("--output",     default="recipe_vectors.jsonl",  help="Output JSONL with embeddings")
    parser.add_argument("--api-key",    required=True,                   help="Nomic Atlas API key")
    parser.add_argument("--batch-size", type=int, default=100,           help="Texts per API call (max ~200)")
    parser.add_argument("--resume",     action="store_true",             help="Skip already-embedded docs (resume interrupted run)")
    args = parser.parse_args()

    input_path  = Path(args.input)
    output_path = Path(args.output)

    # Load all docs
    docs = [json.loads(line) for line in input_path.read_text().strip().splitlines()]
    print(f"📄 Loaded {len(docs):,} recipes from {input_path}")

    # Resume support — skip docs already in output
    already_done = set()
    if args.resume and output_path.exists():
        with open(output_path) as f:
            for line in f:
                try:
                    already_done.add(json.loads(line)["id"])
                except Exception:
                    pass
        print(f"⏩ Resuming — {len(already_done):,} already embedded, skipping")

    pending = [d for d in docs if d["id"] not in already_done]
    print(f"🔢 To embed: {len(pending):,}")

    if not pending:
        print("✅ Nothing to do.")
        return

    # Verify API key with a single test call
    print("🔑 Verifying Nomic API key...")
    try:
        test = embed_batch(["this is a test recipe with some ingredients"], args.api_key)
        dim = len(test[0])
        print(f"✅ API key valid — embedding dimension: {dim}")
    except Exception as e:
        print(f"❌ API key check failed: {e}")
        return

    # Embed in batches
    mode = "a" if args.resume else "w"
    total_written = len(already_done)

    with open(output_path, mode) as f:
        for i in tqdm(range(0, len(pending), args.batch_size), desc="Embedding"):
            batch = pending[i : i + args.batch_size]
            texts = [doc["combined_text"][:2000] for doc in batch]  # Nomic max ~8192 tokens but cap for safety

            try:
                vectors = embed_batch(texts, args.api_key)
            except Exception as e:
                print(f"\n❌ Batch {i}–{i+len(batch)} failed: {e}")
                print("   Run again with --resume to continue from here.")
                break

            for doc, vector in zip(batch, vectors):
                doc["embedding"] = vector
                f.write(json.dumps(doc) + "\n")
                total_written += 1

    print(f"\n✅ Done — {total_written:,} recipes with embeddings → {output_path}")
    print(f"   Dimension: {dim}")
    print(f"\nNext step: python load_qdrant.py --input {output_path}")


if __name__ == "__main__":
    main()