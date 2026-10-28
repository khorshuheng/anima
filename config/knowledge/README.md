# Repo vocabulary packs

Every `*.json` here is loaded as a knowledge vocabulary pack at service startup,
after the embedded core pack. See `docs/deployment.md` and ADR 0007.

- Order: `config/knowledge/*.json`, then `~/.config/anima/knowledge/*.json`, then
  `Anima__Knowledge__Packs` entries.
- A pack may only add names under its own prefix (`acme:invoice`) and may widen an
  existing edge type's endpoint pairs. It cannot redefine a core entry.
- Validate a pack before committing it:

  ```bash
  anima knowledge vocabulary validate config/knowledge/acme.json
  ```

This file is not a pack; only `*.json` is loaded.
