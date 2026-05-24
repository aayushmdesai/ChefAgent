#!/usr/bin/env python3
"""
Test Qdrant Search
==================
Runs semantic search queries against the loaded recipe collection
to verify the RAG pipeline is working end-to-end.

Prerequisites:
    - Ollama running: ollama serve
    - Qdrant running: docker compose up -d qdrant
    - Recipes loaded: python scripts/load_qdrant.py
"""

import requests
from qdrant_client import QdrantClient


def embed_query(
    query: str,
    model: str = "nomic-embed-text",
    embed_url: str = "http://localhost:11434/api/embed",
) -> list[float]:
    response = requests.post(embed_url, json={"model": model, "input": query})
    response.raise_for_status()
    data = response.json()
    return data["embeddings"][0]


def search_recipes(
    query_vector: list[float],
    collection: str = "recipes",
    qdrant_url: str = "http://localhost:6333",
    limit: int = 3,
):
    client = QdrantClient(url=qdrant_url)
    results = client.query_points(
        collection_name=collection,
        query=query_vector,
        limit=limit,
    )
    return results.points


def main():
    test_queries = [
        # Exact ingredients
        "search_query: easy chicken dinner with garlic",
        # Vibe / situation
        "search_query: something warm and comforting for a cold night",
        # Should get low scores
        "search_query: how to fix a flat tire on a bicycle",
    ]

    for query in test_queries:
        print(f"\n{'='*60}")
        print(f"Query: {query}")
        print('='*60)

        query_vector = embed_query(query)
        results = search_recipes(query_vector)

        for i, result in enumerate(results, start=1):
            title = result.payload.get("title", "Unknown title")
            ingredients = result.payload.get("ingredients", [])
            print(f"\n  Result {i} (score: {result.score:.4f})")
            print(f"  Title: {title}")
            print(f"  Ingredients: {', '.join(ingredients[:5])}{'...' if len(ingredients) > 5 else ''}")


if __name__ == "__main__":
    main()