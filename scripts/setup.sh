#!/bin/bash
# ============================================================
# ChefAgent — Setup Script
# ============================================================
# Run this after cloning the repo to set up the full pipeline.
# Prerequisites:
#   - Docker Desktop running
#   - Ollama installed (brew install ollama on macOS)
#   - Python 3.10+ installed
#   - .NET 8 SDK installed
#
# Usage:
#   chmod +x setup.sh
#   ./setup.sh
#
# What this does:
#   1. Start infrastructure (Qdrant + Redis via Docker)
#   2. Pull Ollama models
#   3. Set up Python environment
#   4. Download + process 10K recipes
#   5. Generate embeddings (via Ollama locally)
#   6. Load vectors into Qdrant
#   7. Verify everything works
# ============================================================

set -e  # Exit on any error

# ── Colors ────────────────────────────────────────────────────
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

log()    { echo -e "${GREEN}✅ $1${NC}"; }
warn()   { echo -e "${YELLOW}⚠️  $1${NC}"; }
error()  { echo -e "${RED}❌ $1${NC}"; exit 1; }
section(){ echo -e "\n${YELLOW}── $1 ──────────────────────────────────────${NC}"; }

# ── Check prerequisites ───────────────────────────────────────
section "Checking prerequisites"

command -v docker >/dev/null 2>&1 || error "Docker not found. Install Docker Desktop."
command -v ollama >/dev/null 2>&1 || error "Ollama not found. Run: brew install ollama"
command -v python3 >/dev/null 2>&1 || error "Python 3 not found."
command -v dotnet >/dev/null 2>&1 || error ".NET SDK not found. Install from https://dot.net"

log "All prerequisites found"

# ── Step 1: Infrastructure ────────────────────────────────────
section "Step 1: Starting infrastructure (Qdrant + Redis)"

docker compose up -d qdrant redis

# Wait for Qdrant to be ready
echo "Waiting for Qdrant..."
for i in {1..30}; do
    if curl -s http://localhost:6333/health > /dev/null 2>&1; then
        log "Qdrant is ready"
        break
    fi
    sleep 1
done

# ── Step 2: Ollama models ─────────────────────────────────────
section "Step 2: Pulling Ollama models"

# Start Ollama in background if not running
if ! curl -s http://localhost:11434/api/tags > /dev/null 2>&1; then
    warn "Ollama not running — starting it..."
    ollama serve &
    sleep 3
fi

echo "Pulling nomic-embed-text (274MB)..."
ollama pull nomic-embed-text

echo "Pulling llama3.2 (2GB)..."
ollama pull llama3.2

log "Models ready"

# ── Step 3: Python environment ────────────────────────────────
section "Step 3: Setting up Python environment"

if [ ! -d ".venv" ]; then
    python3 -m venv .venv
    log "Created .venv"
fi

source .venv/bin/activate
pip install -q -r scripts/requirements.txt
log "Python dependencies installed"

# ── Step 4: Data pipeline ─────────────────────────────────────
section "Step 4: Preparing recipe data"

if [ -f "data/processed/recipes.jsonl" ]; then
    warn "recipes.jsonl already exists — skipping download"
    warn "Delete data/processed/recipes.jsonl to re-download"
else
    python3 scripts/pipeline/prepare_recipes.py --limit 10000
    log "Recipes prepared"
fi

# ── Step 5: Generate embeddings ───────────────────────────────
section "Step 5: Generating embeddings"

if [ -f "data/embeddings/recipe_vectors.jsonl" ]; then
    warn "recipe_vectors.jsonl already exists — skipping embedding"
    warn "Delete data/embeddings/recipe_vectors.jsonl to re-embed"
else
    echo "This will take a while on CPU (consider using Colab for speed)"
    echo "See chefagent_embeddings.ipynb for GPU embedding on Google Colab"
    python3 scripts/pipeline/generate_embeddings.py
    log "Embeddings generated"
fi

# ── Step 6: Load into Qdrant ──────────────────────────────────
section "Step 6: Loading vectors into Qdrant"

python3 scripts/pipeline/load_qdrant.py
log "Vectors loaded into Qdrant"

# ── Step 7: Verify ────────────────────────────────────────────
section "Step 7: Verifying setup"

# Quick search test
python3 scripts/eval/test_loaded_qdrant.py
log "Vector search working"

# ── Done ──────────────────────────────────────────────────────
section "Setup complete"

echo ""
echo "Run the API:"
echo "  cd src/api && dotnet run"
echo ""
echo "Test the endpoints:"
echo "  curl -X POST http://localhost:5000/recipes/search \\"
echo "    -H 'Content-Type: application/json' \\"
echo "    -d '{\"query\": \"quick chicken dinner\", \"maxResults\": 5}'"
echo ""
echo "  curl -X POST http://localhost:5000/chat \\"
echo "    -H 'Content-Type: application/json' \\"
echo "    -d '{\"message\": \"find me a vegan pasta dinner\"}'"
echo ""
echo "Run eval tests:"
echo "  source .venv/bin/activate"
echo "  python3 scripts/eval/test_search_quality.py"
echo "  python3 scripts/eval/test_diet_agent.py"