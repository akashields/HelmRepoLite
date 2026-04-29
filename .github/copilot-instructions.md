# HelmRepoLite — Copilot Instructions

## Project purpose
A lightweight, self-hosted, ChartMuseum-compatible Helm chart repository server written in .NET.
Distributed as a .NET global tool (`dotnet tool install`).

## Hard rules

### Zero third-party runtime dependencies
`src/HelmRepoLite/HelmRepoLite.csproj` must never gain a `<PackageReference>` for a runtime
library. Everything needed (Kestrel, minimal APIs, FileSystemWatcher, GZip, HTTP Basic auth,
`System.Formats.Tar`) ships with the .NET SDK/runtime.

**Test infrastructure is exempt** — xUnit and the SDK test runner are fine in the test project
because they don't ship in the tool package.

### When to discuss adding a third-party package
Before suggesting any new dependency, raise it as an explicit question rather than just adding it.
A dependency may be worth discussing when hand-coding it would be:

- **Too risky** — correctness is critical and the failure modes of a bespoke implementation are
  hard to reason about (e.g. cryptography, TLS, complex protocol parsing)
- **Too complex** — the implementation would be disproportionately large relative to the value
  delivered (e.g. a full YAML 1.2 parser vs. the limited Chart.yaml subset we actually need)
- **Clearly not the best option** — a mature, well-audited library exists and the hand-coded
  alternative would be a meaningful maintenance burden going forward

When raising the question, always state the tradeoff explicitly:
_"This would add dependency X (Y KB, Z transitive deps). The alternative is [hand-coded approach].
The risk/complexity of hand-coding is [assessment]. Worth discussing?"_
## Code style
- Target framework: `net10.0`, C# 14, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is set — all warnings must be resolved, not silenced unless there is a clear justification
- No Moq, no FluentAssertions in tests — plain xUnit `Assert.*` only
- Minimal comments: only where the logic is non-obvious or a deliberate design decision needs recording

## Architecture notes
- `ChartStore` is the single source of truth for the in-memory index; all mutations go through its `SemaphoreSlim` mutex
- `MiniYaml` / `MiniYamlWriter` are purposely limited parsers for the Chart.yaml subset — not a general YAML library
- The storage directory (`--storage-dir`, default `./charts`) is the single watched folder; there is no separate "drop folder"
- `index.yaml` is always fully regenerated from the in-memory index — it is never read back at startup
