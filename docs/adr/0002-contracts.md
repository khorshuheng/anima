# ADR 0002: Protobuf IDL, descriptor-driven JSON payloads

Status: accepted

## Context

Agents must add message contracts without rebuilding the repository. Google's
C# protobuf runtime exposes descriptors but has no dynamic message type, so
generated types are not an option for user-defined contracts.

## Decision

- `.proto` files are the source of truth.
- `protoc` emits `contracts/descriptors/anima.pb` (`make contracts`).
- `Anima.Contracts` loads descriptor sets at runtime and validates/normalizes JSON
  against them (`DescriptorJsonCodec`).
- Payload content type is `application/x-protobuf+json`.

## Consequences

- Adding a contract is a `.proto` edit plus `make contracts`; no C# rebuild.
- Typed validation and evolution rules come from the descriptors.
- Binary protobuf encoding is deferred; `IMessageCodec` is the seam.
- Well-known types use canonical JSON forms (Timestamp/Duration strings,
  Struct/Value as JSON, wrappers as scalars).
