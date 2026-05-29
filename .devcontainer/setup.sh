#!/bin/bash
set -e

echo "=== ChefAgent Setup ==="

# 1. Start all services
echo "→ Starting Docker services..."
dotnet tool install csharpier --global
docker compose up -d --build
echo "✓ Services started"

# 2. Wait for Ollama
echo "→ Waiting for Ollama..."
until curl -s http://localhost:11434 > /dev/null; do sleep 2; done
echo "✓ Ollama ready"

# 3. Pull models
echo "→ Pulling Ollama models..."
docker compose exec ollama ollama pull nomic-embed-text
docker compose exec ollama ollama pull llama3.2
echo "✓ Models ready"

# 4. Load vectors
if [ -f "data/embeddings/recipe_vectors.jsonl" ]; then
  echo "→ Loading vectors into Qdrant..."
  sleep 10
  python3 scripts/pipeline/load_qdrant.py
  echo "✓ Vectors loaded"
else
  echo "⚠  Upload data/embeddings/recipe_vectors.jsonl then run: make reload-vectors"
fi

# 5. Python + frontend deps
pip install -r scripts/requirements.txt --quiet
cd src/frontend && npm install --silent && cd ../..

echo ""
echo "=== Ready ==="
echo "API:      http://localhost:5100"
echo "Health:   make health"
echo "Vectors:  make check-vectors"
echo "Frontend: make frontend"