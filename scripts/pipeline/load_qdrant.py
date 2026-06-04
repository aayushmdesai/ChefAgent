#!/usr/bin/env python3
"""
Qdrant Index Loader
===================
Creates a Qdrant collection and uploads recipe documents with embeddings.

Usage:
    python scripts/pipeline/load_qdrant.py \
        --input data/embeddings/recipe_vectors.jsonl \
        --collection recipes

    # Cloud:
    python scripts/pipeline/load_qdrant.py \
        --qdrant-url https://your-cluster.cloud.qdrant.io:6334 \
        --api-key YOUR_KEY \
        --collection recipes

Dependencies:
    pip install qdrant-client tqdm
"""

import argparse
import json
import os
from pathlib import Path
from tqdm import tqdm
from urllib.parse import urlparse


def main():
    parser = argparse.ArgumentParser(
        description="Load recipe vectors into Qdrant")
    parser.add_argument("--input", type=str,
                        default="data/embeddings/recipe_vectors.jsonl")
    parser.add_argument("--collection", type=str, default="recipes")
    parser.add_argument("--qdrant-url", type=str,
                        default="http://localhost:6333")
    parser.add_argument("--api-key", type=str,
                        default=os.getenv("QDRANT_API_KEY"))
    parser.add_argument("--batch-size", type=int, default=200)
    args = parser.parse_args()

    from qdrant_client import QdrantClient
    from qdrant_client.models import (
        Distance,
        VectorParams,
        PointStruct,
    )

    parsed = urlparse(args.qdrant_url)
    client = QdrantClient(
        host=parsed.hostname,
        port=parsed.port or 6333,
        https=parsed.scheme == "https",
        api_key=args.api_key,
        check_compatibility=False,
        prefer_grpc=False
    )

    # Load documents
    input_path = Path(args.input)
    docs = [json.loads(line)
            for line in input_path.read_text().strip().split("\n")]
    print(f"Loaded {len(docs)} documents with embeddings")

    # Detect embedding dimension from first doc
    dim = len(docs[0]["embedding"])
    print(f"Embedding dimension: {dim}")

    # Create or recreate collection
    collections = [c.name for c in client.get_collections().collections]
    if args.collection in collections:
        print(f"Collection '{args.collection}' exists — recreating...")
        client.delete_collection(args.collection)

    client.create_collection(
        collection_name=args.collection,
        vectors_config=VectorParams(size=dim, distance=Distance.COSINE),
    )
    print(f"✅ Collection '{args.collection}' created (dim={dim}, cosine)")

    # Upload in batches
    total = 0
    for i in tqdm(range(0, len(docs), args.batch_size), desc="Uploading"):
        batch = docs[i:i + args.batch_size]
        points = []
        for j, doc in enumerate(batch):
            payload = {
                "doc_id": doc["id"],
                "title": doc["title"],
                "ingredients": doc.get("ingredients", []),
                "ingredients_text": doc.get("ingredients_text", ""),
                "directions": doc.get("directions", []),
                "directions_text": doc.get("directions_text", ""),
                "ingredient_count": doc.get("ingredient_count", 0),
                "step_count": doc.get("step_count", 0),
            }
            points.append(PointStruct(
                id=i + j,
                vector=doc["embedding"],
                payload=payload,
            ))

        client.upsert(collection_name=args.collection, points=points)
        total += len(points)

    # Verify
    info = client.get_collection(args.collection)
    print(f"\n🎉 Collection '{args.collection}' is ready!")
    print(f"   Points: {info.points_count}")
    print(
        f"   Vectors: dim={info.config.params.vectors.size}, distance={info.config.params.vectors.distance}")
    print(f"   Qdrant dashboard: {args.qdrant_url}/dashboard")


if __name__ == "__main__":
    main()
