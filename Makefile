# Standard entry points for humans, CI and AI tools (CONVENTIONS 3a).
# No local .NET SDK / make? Run any target via ./scripts/dev.sh make <target> (or scripts/dev.ps1).
SLN := Brightline.slnx
CONFIG ?= Release
DOTNET ?= dotnet

.PHONY: screenshots setup build test test-unit test-integration lint format check dev down seed eval migrate create-admin e2e clean

setup: ## Restore packages and local tools
	$(DOTNET) tool restore
	$(DOTNET) restore $(SLN)

build: ## Build with warnings as errors
	$(DOTNET) build $(SLN) -c $(CONFIG) --no-restore

test: ## Run all automated tests (unit, integration, component)
	$(DOTNET) test --solution $(SLN) -c $(CONFIG) --no-build -- --filter-not-trait "Category=E2E" --filter-not-trait "Category=Eval" --ignore-exit-code 8

test-unit:
	$(DOTNET) test --project tests/Brightline.UnitTests -c $(CONFIG)

lint: ## Verify formatting and analyzers
	$(DOTNET) format $(SLN) --verify-no-changes --no-restore

format: ## Apply formatting fixes
	$(DOTNET) format $(SLN) --no-restore

check: build lint test ## Must pass before every commit

dev: ## Start app, legacy API, Postgres and Aspire Dashboard
	docker compose up --build

down:
	docker compose down

seed: ## Load help-centre documents and legacy sample data
	docker compose run --rm legacy-api --migrate --seed
	docker compose run --rm web --seed

eval: ## Run the evaluation suite, writes eval/results.md and eval/results.json
	$(DOTNET) run --project tests/Brightline.Eval -c $(CONFIG)

E2E_BIN := tests/Brightline.E2ETests/bin/$(CONFIG)/net10.0

e2e: ## Playwright end-to-end test: chat -> verify -> return with confirmation -> staff sees ticket (installs Chromium once)
	$(DOTNET) build tests/Brightline.E2ETests -c $(CONFIG)
	$(E2E_BIN)/.playwright/node/linux-x64/node $(E2E_BIN)/.playwright/package/cli.js install --with-deps chromium
	$(DOTNET) test --project tests/Brightline.E2ETests -c $(CONFIG) --no-build

screenshots: ## Re-generate docs/images/*.png from the end-to-end story
	E2E_SCREENSHOTS=1 $(MAKE) e2e

migrate: ## Apply EF migrations for both databases
	docker compose run --rm web --migrate
	docker compose run --rm legacy-api --migrate

create-admin: ## Create the first admin: make create-admin EMAIL=you@example.com
	docker compose run --rm web --create-admin $(EMAIL)

clean:
	$(DOTNET) clean $(SLN)
