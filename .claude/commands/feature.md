---
description: Add a new feature with the standard workflow (design first, then code, then docs, then tests).
---

Implement the feature the user just described, in this order. Don't skip steps.

1. **Read the relevant existing code first.** Use `CLAUDE.md`'s "Repo map" table to figure out which files are involved. Read all of them before proposing a change.
2. **Sketch the design in chat first.** Two or three sentences: where the change lives, what types it touches, what could break. Get the user to confirm before writing code if the change touches `ChartStore`, the route table, or the storage layout.
3. **Write the code.** Follow `.editorconfig` and the conventions in `CLAUDE.md` ("Style and conventions"). New CLI flags need to land in `CliParser`, `ServerOptions`, the `--help` text, the README CLI table, and `docs/requirements.md`.
4. **Write or update tests.** Every new public method on `ChartStore`, `ChartInspector`, `IndexBuilder`, `MiniYaml*` gets at least one test. New routes get a `ChartStore`-level test (we don't do route-level integration tests today; see `docs/design.md` "Testing strategy").
5. **Update docs.** If you changed user-visible behaviour, update `README.md`. If you changed an architectural decision, update `docs/design.md`. If you added or changed a requirement, update `docs/requirements.md`.
6. **Run `/check`.** Don't say "done" until it's green.

If the user's feature request would require adding a NuGet package to the shipping project, **stop and tell them.** Don't add it. The "no third-party packages" rule is in `CLAUDE.md` for a reason.
