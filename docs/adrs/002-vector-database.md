# ADR-002: Vector Database Selection

**Status:** Accepted
**Date:** 2026-05-23
**Decision Makers:** [Your Name]

## Context

The Recipe Agent requires a vector database to store and retrieve recipe embeddings for semantic search. Requirements:

- Vector similarity search with filtering
- Scalable to 2M+ recipe documents
- Self-hosted (no cloud costs)
- Good developer experience and documentation

## Decision

Use **Qdrant** (self-hosted via Docker) as the vector database.

## Rationale

- Open-source and free to run locally — no cloud subscription needed.
- Supports filtering on payload fields (ingredient count, source, etc.) alongside vector search.
- gRPC API for fast search, REST API for debugging, built-in dashboard at :6333/dashboard.
- Strong .NET client library (`Qdrant.Client`).
- Growing adoption in the AI engineering community — recognizable on resumes.
- Easy to scale: single Docker container for dev, can cluster for production.

## Consequences

- No native hybrid search (vector + BM25 keyword) like Azure AI Search. Mitigated by: using high-quality embeddings so vector search alone is sufficient, and adding payload filtering for structured queries.
- Must manage Qdrant container ourselves (trivial with Docker Compose).
- Embedding generation is a separate step via Ollama (`nomic-embed-text`).

## Alternatives Considered

| Option | Pros | Cons |
|--------|------|------|
| Azure AI Search | Native hybrid search, semantic ranker | Paid, cloud dependency |
| Pinecone | Simple managed API | Paid, vendor lock-in |
| ChromaDB | Very simple API | Less mature, weaker at scale |
| pgvector | Uses existing Postgres skills | Manual similarity search, no built-in dashboard |
