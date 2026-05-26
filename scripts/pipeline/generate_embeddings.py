#!/usr/bin/env python3
"""
Embedding Generator (Ollama)
============================
Reads processed recipes from JSONL, generates embeddings locally via Ollama,
and outputs vectors ready for Qdrant ingestion.

Usage:
    python scripts/generate_embeddings.py \
        --input data/processed/recipes.jsonl \
        --output data/embeddings/recipe_vectors.jsonl \
        --model nomic-embed-text \
        --batch-size 50

Dependencies:
    pip install requests tqdm

Prerequisite:
    Ollama running locally with embedding model pulled:
        ollama pull nomic-embed-text
"""

import argparse
import json
import requests
from pathlib import Path
from tqdm import tqdm


def get_embedding(text: str, model: str, ollama_url: str) -> list[float]:
    """Generate embedding for a single text via Ollama API."""
    response = requests.post(
        f"{ollama_url}/api/embed",
        json={"model": model, "input": text},
    )
    response.raise_for_status()
    return response.json()["embeddings"][0]


def get_embeddings_batch(texts: list[str], model: str, ollama_url: str) -> list[list[float]]:
    """Generate embeddings for a batch of texts via Ollama API."""
    response = requests.post(
        f"{ollama_url}/api/embed",
        json={"model": model, "input": texts},
    )
    response.raise_for_status()
    return response.json()["embeddings"]


def main():
    parser = argparse.ArgumentParser(description="Generate embeddings for recipe documents via Ollama")
    parser.add_argument("--input", type=str, default="data/processed/recipes.jsonl")
    parser.add_argument("--output", type=str, default="data/embeddings/recipe_vectors.jsonl")
    parser.add_argument("--model", type=str, default="nomic-embed-text")
    parser.add_argument("--ollama-url", type=str, default="http://localhost:11434")
    parser.add_argument("--batch-size", type=int, default=50)
    args = parser.parse_args()

    # Verify Ollama is running
    try:
        r = requests.get(f"{args.ollama_url}/api/tags")
        r.raise_for_status()
        models = [m["name"] for m in r.json().get("models", [])]
        if not any(args.model in m for m in models):
            print(f"⚠️  Model '{args.model}' not found. Pull it first:")
            print(f"   ollama pull {args.model}")
            return
        print(f"✅ Ollama running, model '{args.model}' available")
    except requests.ConnectionError:
        print(f"❌ Cannot reach Ollama at {args.ollama_url}")
        print("   Start it with: ollama serve (or docker compose up ollama)")
        return

    input_path = Path(args.input)
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    docs = [json.loads(line) for line in input_path.read_text().strip().split("\n")]
    print(f"Loaded {len(docs)} documents")

    # Get embedding dimension from a test call
    test_vec = get_embedding("test", args.model, args.ollama_url)
    dim = len(test_vec)
    print(f"Embedding dimension: {dim}")

    with open(output_path, "w") as f:
        for i in tqdm(range(0, len(docs), args.batch_size), desc="Embedding"):
            batch = docs[i:i + args.batch_size]
            texts = [doc["combined_text"][:2000] for doc in batch]  # Truncate long texts

            vectors = get_embeddings_batch(texts, args.model, args.ollama_url)

            for doc, vector in zip(batch, vectors):
                doc["embedding"] = vector
                f.write(json.dumps(doc) + "\n")

    print(f"✅ Embeddings saved to {output_path}")
    print(f"   Dimension: {dim}")
    print(f"\nNext step: python scripts/load_qdrant.py")


if __name__ == "__main__":
    main()
