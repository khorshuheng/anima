# Contracts

Contracts are protobuf schemas owned by the repo, but they are **not compiled
into C#**. The runtime loads `contracts/descriptors/anima.pb` and validates
messages against those descriptors, so an agent can add or change a contract by
editing a `.proto` and running:

```bash
make contracts
# or, on any OS:
dotnet run --project tools/Anima.Cli -- contracts compile
```

No project rebuild is required. Services pick the new descriptor set up on the
next publish or immediately when `WatchForChanges` is enabled (services use it).

## Layout

```
contracts/proto/anima/<area>/v1/*.proto   schema sources (user-editable)
contracts/descriptors/anima.pb            generated descriptor set (committed)
```

Area packages: `common`, `bus`, `knowledge`, `automation`, `deploy`, `agent`
(external agent messages), `workflow` (role tasks, results and events).

## Wire format

Payloads on the bus are the JSON projection of the protobuf schema:

- field names use protobuf JSON names (`camelCase`, or `json_name`)
- `int64`/`uint64` are strings; `bytes` are base64
- enums are names; nested messages are objects; maps are objects
- `google.protobuf.Timestamp` / `Duration` use their canonical string forms
- unknown fields are rejected, so a typo fails at the bus boundary

This keeps agent-authored JSON type-checked without generated DTOs. Binary
protobuf encoding is deliberately out of scope for the scaffold; the seam is
`IMessageCodec` in `src/Anima.Contracts`.

## Checking a payload

```bash
dotnet run --project tools/Anima.Cli -- contracts list
dotnet run --project tools/Anima.Cli -- contracts describe anima.bus.v1.TaskAssignment
dotnet run --project tools/Anima.Cli -- contracts validate anima.bus.v1.TaskAssignment payload.json
```

The bus service exposes the same checks at `POST /api/bus/validate`.
