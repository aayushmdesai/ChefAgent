#!/bin/bash
set -e

echo "=== ChefAgent Setup ==="

# 1. Start all infrastructure (Qdrant + Redis + Ollama + API)
echo "→ Starting Docker services..."
docker compose up -d --build
echo "✓ Docker services started"

# 2. Wait for Ollama to be ready
echo "→ Waiting for Ollama..."
until curl -s http://localhost:11434 > /dev/null; do
  sleep 2
done
echo "✓ Ollama ready"

# 3. Pull models (skips if already pulled — stored in ollama-data volume)
echo "→ Pulling Ollama models (this takes a few minutes on first run)..."
docker compose exec ollama ollama pull nomic-embed-text
docker compose exec ollama ollama pull llama3.2
echo "✓ Models ready"

# 4. Python dependencies
echo "→ Installing Python dependencies..."
pip install -r scripts/requirements.txt --quiet
echo "✓ Python ready"

# 5. Load vectors into Qdrant (only if file exists)
if [ -f "data/embeddings/recipe_vectors.jsonl" ]; then
  echo "→ Loading vectors into Qdrant..."
  python3 scripts/load_qdrant.py
  echo "✓ Vectors loaded"
else
  echo "⚠  data/embeddings/recipe_vectors.jsonl not found"
  echo "   Upload the file then run: python3 scripts/load_qdrant.py"
fi

# 6. Frontend dependencies
echo "→ Installing frontend dependencies..."
cd src/frontend && npm install --silent && cd ../..
echo "✓ Frontend ready"

echo ""
echo "=== Setup complete ==="
echo ""
echo "API already running via Docker:  http://localhost:5100"
echo "Health check:                    curl http://localhost:5100/health"
echo ""
echo "To run frontend dev server:      cd src/frontend && npm run dev"
echo "To rebuild API after changes:    docker compose up --build api"
echo "To view logs:                    docker compose logs api -f"
echo "To reload vectors:               python3 scripts/load_qdrant.py"