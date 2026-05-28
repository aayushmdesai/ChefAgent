.PHONY: up down build logs reload-vectors frontend pull-models health check-vectors fresh

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
	cd src/frontend && npm install --silent && npm run dev

pull-models:
	docker compose exec ollama ollama pull nomic-embed-text
	docker compose exec ollama ollama pull llama3.2

health:
	curl -sf http://localhost:5100/health && echo " ✓ API healthy" || echo " ✗ API not reachable"

check-vectors:
	curl -s http://localhost:6333/collections/recipes | jq '{status: .result.status, points: .result.points_count}'

fresh:
	docker compose down -v
	docker compose up -d --build
	sleep 10
	docker compose exec ollama ollama pull nomic-embed-text
	docker compose exec ollama ollama pull llama3.2
	python3 scripts/pipeline/load_qdrant.py
	make health
	make check-vectors

