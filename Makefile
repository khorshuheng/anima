SHELL := /bin/bash
DOTNET ?= dotnet
CONFIG ?= Debug
SOLUTION := Anima.sln
CLI := $(DOTNET) run --project tools/Anima.Cli -c $(CONFIG) --

.DEFAULT_GOAL := help

.PHONY: help restore build test fmt fmt-check check contracts dev browser-check \
        playwright-install hooks-install clean ui-install ui-build ui-dev graph-sync graph-status \
        build-contracts test-contracts build-bus test-bus build-knowledge test-knowledge \
        build-automation test-automation build-deploy test-deploy build-workflow test-workflow \
        build-dashboard test-dashboard \
        run-knowledge run-bus run-automation run-deploy run-workflow run-dashboard

help:
	@echo "Anima monorepo"
	@echo ""
	@echo "  make build             build the full solution"
	@echo "  make test              run all tests"
	@echo "  make check             format check + build + test"
	@echo "  make fmt               apply dotnet format"
	@echo "  make contracts         recompile .proto -> contracts/descriptors/anima.pb"
	@echo "  make dev               run all six services locally"
	@echo "  make browser-check     show the Chrome executable Playwright will use"
	@echo "  make ui-install        install React UI dependencies"
	@echo "  make ui-build          build ui/dist, served by the dashboard at /"
	@echo "  make ui-dev            run the Vite dev server (proxy to :7416)"
	@echo ""
	@echo "  per component: make build-<name> / test-<name>"
	@echo "    contracts bus knowledge automation deploy workflow dashboard"
	@echo ""
	@echo "  run: make run-<name>   (knowledge, bus, automation, deploy, workflow, dashboard)"
	@echo "  optional: make playwright-install BROWSERS='firefox webkit'"
	@echo "  hooks: make hooks-install REPO=/path/to/repo"

restore:
	$(DOTNET) restore $(SOLUTION)

build:
	$(DOTNET) build $(SOLUTION) -c $(CONFIG) --nologo

test:
	$(DOTNET) test $(SOLUTION) -c $(CONFIG) --nologo

fmt:
	$(DOTNET) format $(SOLUTION)

fmt-check:
	$(DOTNET) format $(SOLUTION) --verify-no-changes --no-restore

check: build test fmt-check

contracts:
	$(CLI) contracts compile

dev: ui-build
	./scripts/dev.sh

browser-check:
	$(CLI) browser check

playwright-install:
	@if [ -z "$(BROWSERS)" ]; then \
	  echo "usage: make playwright-install BROWSERS='firefox webkit'"; \
	  echo "Chrome uses the installed executable and needs no download."; \
	  exit 2; \
	fi
	./scripts/install-playwright.sh $(BROWSERS)

hooks-install:
	./scripts/install-local-hooks.sh $(REPO)

ui-install:
	npm --prefix ui install

ui-build:
	@if [ ! -d ui/node_modules ]; then npm --prefix ui install --no-audit --no-fund; fi
	npm --prefix ui run build

ui-dev:
	npm --prefix ui run dev

graph-sync:
	codegraph sync

graph-status:
	codegraph status

clean:
	$(DOTNET) clean $(SOLUTION) -c $(CONFIG)

build-contracts:
	$(DOTNET) build Anima.Contracts.slnf -c $(CONFIG) --nologo
test-contracts:
	$(DOTNET) test Anima.Contracts.slnf -c $(CONFIG) --nologo

build-bus:
	$(DOTNET) build Anima.Bus.slnf -c $(CONFIG) --nologo
test-bus:
	$(DOTNET) test Anima.Bus.slnf -c $(CONFIG) --nologo

build-knowledge:
	$(DOTNET) build Anima.Knowledge.slnf -c $(CONFIG) --nologo
test-knowledge:
	$(DOTNET) test Anima.Knowledge.slnf -c $(CONFIG) --nologo

build-automation:
	$(DOTNET) build Anima.Automation.slnf -c $(CONFIG) --nologo
test-automation:
	$(DOTNET) test Anima.Automation.slnf -c $(CONFIG) --nologo

build-deploy:
	$(DOTNET) build Anima.Deploy.slnf -c $(CONFIG) --nologo
test-deploy:
	$(DOTNET) test Anima.Deploy.slnf -c $(CONFIG) --nologo

build-workflow:
	$(DOTNET) build Anima.Workflow.slnf -c $(CONFIG) --nologo
test-workflow:
	$(DOTNET) test Anima.Workflow.slnf -c $(CONFIG) --nologo

run-knowledge:
	$(DOTNET) run --project src/Anima.Knowledge.Service -c $(CONFIG)
run-bus:
	$(DOTNET) run --project src/Anima.Bus.Service -c $(CONFIG)
run-automation:
	$(DOTNET) run --project src/Anima.Automation.Service -c $(CONFIG)
run-deploy:
	$(DOTNET) run --project src/Anima.Deploy.Service -c $(CONFIG)
run-workflow:
	$(DOTNET) run --project src/Anima.Workflow.Service -c $(CONFIG)

build-dashboard:
	$(DOTNET) build Anima.Dashboard.slnf -c $(CONFIG) --nologo
test-dashboard:
	$(DOTNET) test Anima.Dashboard.slnf -c $(CONFIG) --nologo

run-dashboard:
	$(DOTNET) run --project src/Anima.Dashboard.Service -c $(CONFIG)
