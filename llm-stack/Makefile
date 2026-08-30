# Convenience targets. Windows users: use the equivalent scripts\*.ps1 instead.
.DEFAULT_GOAL := help
SHELL := /bin/bash

.PHONY: help bootstrap up down restart logs health smoke bench ps config pull clean backup gpu alerts

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-12s\033[0m %s\n", $$1, $$2}'

bootstrap: ## One-time setup: check Docker/GPU, create .env, generate secrets
	@./scripts/bootstrap.sh

up: ## Start the stack and wait for the model to load
	@./scripts/up.sh

down: ## Stop the stack, keeping all volumes
	@docker compose --profile "*" down --remove-orphans

restart: ## Recreate only the inference container
	@docker compose up -d --force-recreate vllm

logs: ## Follow logs (make logs S=vllm for one service)
	@docker compose logs -f --tail 100 $(S)

health: ## Check every enabled component
	@./scripts/health.sh

smoke: ## End-to-end API verification
	@./scripts/smoke-test.sh

bench: ## Concurrency sweep (make bench C=1,4,8,16 N=16)
	@./scripts/benchmark.sh --concurrency $(or $(C),1,4,8,16) --requests $(or $(N),16)

ps: ## Show container status
	@docker compose ps

config: ## Render the fully-resolved compose file
	@docker compose config

pull: ## Pull the latest images
	@docker compose pull

backup: ## Back up stateful volumes to ./backups
	@./scripts/backup.sh

gpu: ## Live GPU utilisation from the exporter
	@curl -s localhost:9835/metrics | grep -E '^nvidia_smi_(utilization_gpu_ratio|memory_used_bytes|temperature_gpu|power_draw_watts)'

alerts: ## List currently firing alerts
	@curl -s localhost:9090/api/v1/alerts | python3 -c 'import json,sys; [print(f"{a[\"labels\"][\"severity\"]:9} {a[\"labels\"][\"alertname\"]:28} {a[\"state\"]}") for a in json.load(sys.stdin)["data"]["alerts"]] or print("none firing")'

clean: ## Stop and DELETE all volumes (destructive)
	@echo "This deletes models, metrics, dashboards and chat history."
	@read -p "Type PURGE to confirm: " a && [ "$$a" = "PURGE" ] && docker compose --profile "*" down -v --remove-orphans
