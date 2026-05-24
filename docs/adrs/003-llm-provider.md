# ADR-003: LLM Provider Selection

**Status:** Accepted
**Date:** 2026-05-23
**Decision Makers:** [Your Name]

## Context

ChefAgent agents need an LLM for intent classification, dietary reasoning, meal plan generation, and conversational responses. Requirements:

- Free to use during development (high iteration volume)
- Supports tool/function calling for agent interactions
- Can be swapped for a cloud provider (Claude, GPT) in production
- Embedding generation for the RAG pipeline

## Decision

Use **Ollama** running locally for both chat completions and embeddings.

- **Chat model:** `llama3.2` (8B) — good balance of quality and speed on consumer hardware
- **Embedding model:** `nomic-embed-text` (768 dimensions) — strong retrieval performance, fast

## Rationale

- Zero cost — runs entirely on local hardware.
- Deeper learning: forces understanding of model behavior, token limits, and performance tuning.
- OpenAI-compatible API makes it trivial to swap in Claude or GPT later via Semantic Kernel's provider abstraction.
- Embedding model runs in the same Ollama instance — one service for both capabilities.
- Demonstrates provider-agnostic architecture design to prospective employers.

## Consequences

- Requires a machine with decent specs (16GB+ RAM recommended for llama3.2 8B).
- Slower inference than cloud APIs — acceptable for development.
- Quality may be lower than GPT-4o/Claude for complex reasoning — document this in eval results (Month 3) as a comparison point.
- Docker Compose includes Ollama as a service for consistent dev environments.

## Migration Path

The codebase is designed for easy provider swap:
1. Semantic Kernel supports Ollama, Azure OpenAI, and Anthropic connectors
2. Embedding generation is behind an HTTP interface — swap URL + model name
3. Qdrant is embedding-model-agnostic — re-index if switching embedding models
