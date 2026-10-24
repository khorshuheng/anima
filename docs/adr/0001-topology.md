# ADR 0001: Separate projects with in-process wiring

Status: accepted

## Context

Multiple agents and services must evolve independently, but the local dev loop
should not require a broker, containers or service discovery.

## Decision

Each area gets `Abstractions`, an implementation library and a thin HTTP host.
Hosts wire implementations in-process by default. All cross-boundary calls go
through interfaces (`IMessageBus`, `IKnowledgeGraph`, `IBrowserDriver`,
`IDeployHookSink`). There is no agent runtime: external harnesses are clients.

## Consequences

- Fast local start: `make dev`, no infra.
- Remote topology is a composition-root change, not a rewrite.
- Libraries stay honest about their dependencies; hosts own platform specifics.
- Two process boundaries are not exercised by default, so integration tests for
  remote clients remain future work.
