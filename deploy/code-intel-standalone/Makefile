# Convenience targets. Windows: the same scripts exist as scripts\*.ps1.
.DEFAULT_GOAL := help
SHELL := /bin/bash

.PHONY: help up down restart logs health smoke ps config pull build backup clean user

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-10s\033[0m %s\n", $$1, $$2}'

up: ## Start everything (cp env-samples/<one>.env .env first)
	@docker compose up -d --build

down: ## Stop everything, keeping all data
	@docker compose --profile "*" down --remove-orphans

restart: ## Recreate the chat model's container (after a MODEL change in .env)
	@docker compose up -d --force-recreate model-init llamacpp litellm

logs: ## Follow logs (make logs S=argus for one service)
	@docker compose logs -f --tail 100 $(S)

health: ## Is every part up and answering?
	@./scripts/health.sh

smoke: ## Sign in and get a real answer from the model, end to end
	@./scripts/smoke-test.sh

ps: ## Container status
	@docker compose ps

config: ## The fully-resolved compose file
	@docker compose config

pull: ## Pull newer images for the model engine and the gateway
	@docker compose pull model-init llamacpp llamacpp-embed litellm postgres

build: ## Rebuild the app image (backend + frontend) from this checkout
	@docker compose build argus

backup: ## Back up people, conversations, index, packs, gateway database and .env
	@./scripts/backup.sh

user: ## Account admin from the shell, e.g. make user A="passwd admin"
	@docker exec -it argus argus user $(A) --config /etc/argus/config.yaml

clean: ## Stop and DELETE all data volumes (destructive)
	@echo "This deletes every account, conversation, key, budget and the code index."
	@read -p "Type PURGE to confirm: " a && [ "$$a" = "PURGE" ] && docker compose --profile "*" down -v --remove-orphans
