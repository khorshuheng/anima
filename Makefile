SHELL := /bin/bash
DOTNET ?= dotnet
CONFIG ?= Debug
SOLUTION := Anima.sln
CLI := $(DOTNET) run --project tools/Anima.Cli -c $(CONFIG) --

.DEFAULT_GOAL := help

.PHONY: help restore build test fmt fmt-check check contracts dev browser-check \
        playwright-install hooks-install clean \
        build-contracts test-contracts build-bus test-bus build-knowledge test-knowledge \
        build-automation test-automation build-deploy test-deploy build-agent test-agent \
        run-knowledge run-bus run-automation run-deploy run-agent

help:
	@echo "Anima monorepo"
	@echo ""
	@echo "  make build             build the full solution"
	@echo "  make test              run all tests"
	@echo "  make check             format check + build + test"
	@echo "  make fmt               apply dotnet format"
	@echo "  make contracts         recompile .proto -> contracts/descriptors/anima.pb"
	@echo "  make dev               run all five services locally"
	@echo "  make browser-check     show the Chrome executable Playwright will use"
	@echo ""
	@echo "  per component: make build-<name> / test-<name>"
	@echo "    contracts bus knowledge automation deploy agent"
	@echo ""
	@echo "  run: make run-<name>   (knowledge, bus, automation, deploy, agent)"
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

dev:
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

build-agent:
	$(DOTNET) build Anima.Agent.slnf -c $(CONFIG) --nologo
test-agent:
	$(DOTNET) test Anima.Agent.slnf -c $(CONFIG) --nologo

run-knowledge:
	$(DOTNET) run --project src/Anima.Knowledge.Service -c $(CONFIG)
run-bus:
	$(DOTNET) run --project src/Anima.Bus.Service -c $(CONFIG)
run-automation:
	$(DOTNET) run --project src/Anima.Automation.Service -c $(CONFIG)
run-deploy:
	$(DOTNET) run --project src/Anima.Deploy.Service -c $(CONFIG)
run-agent:
	$(DOTNET) run --project src/Anima.Agent.Service -c $(CONFIG)
