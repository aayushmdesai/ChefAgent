.PHONY: up down build logs reload-vectors frontend pull-models health

up:
	docker compose up -d

down:
	docker compose down

build:
	docker compose up --build api

logs:
	docker compose logs api -f

reload-vectors:
	python3 scripts/pipeline/load_qdrant.py

frontend:
	cd src/frontend && npm run dev

pull-models:
	docker compose exec ollama ollama pull nomic-embed-text
	docker compose exec ollama ollama pull llama3.2

health:
	curl -sf http://localhost:5100/health && echo " ✓ API healthy" || echo " ✗ API not reachable"

# Full rebuild — use when Dockerfile changed or starting fresh
fresh:
	docker compose down -v
	docker compose up -d --build
	sleep 10
	docker compose exec ollama ollama pull nomic-embed-text
	docker compose exec ollama ollama pull llama3.2
	python3 scripts/pipeline/load_qdrant.py
	make health
	make check-vectors

