# Claude Code memory: HelmRepoLite

This file is read by Claude Code at the start of every session. Keep it tight and current.

## What this project is

A lightweight ChartMuseum-compatible Helm chart repository server in .NET 10. Distributed as a `dotnet tool` global tool. Used by developers locally and by CI agents during a Cake build. See `docs/requirements.md` for the full "what" and `docs/design.md` for the "how".

## The hard rules (do not violate without an explicit conversation)

1. **No third-party runtime packages.** The shipping project (`src/HelmRepoLite`) has zero `PackageReference` entries. A `Directory.Build.props` target will fail the build if you add one. If you genuinely need one, set `<AllowThirdPartyPackages>true</AllowThirdPartyPackages>` in the project and explain why in the PR — and expect pushback. Test projects (`IsTestProject == true`) are exempt; xUnit and the test SDK are fine.
2. **Stay in-spec for Helm.** Anything we serve at `/index.yaml` and `/api/charts/...` must satisfy the Helm CLI and the ChartMuseum API contract. If you change the schema, run the manual smoke test in `docs/manual-test.md` against a real `helm` and a real cluster.
3. **The on-disk `.tgz` files are the source of truth.** `index.yaml` is a derived artifact, regenerated after every mutation and on startup. Do not invert this.
4. **All mutations go through `ChartStore` under the mutex.** Don't write to `_byFile` from outside `ChartStore`. Don't bypass `RebuildIndexLocked`.
5. **No PII or secrets in logs.** Don't log basic-auth headers, request bodies, or environment variables.

## Style and conventions

- File-scoped namespaces, `Nullable enable`, `LangVersion latest`, `TreatWarningsAsErrors true`.
- 4-space indentation in C#, 2-space in YAML/JSON/Markdown (see `.editorconfig`).
- Keep types in their own files; one public type per file with the matching name.
- Prefer `record` for immutable data, `sealed class` for services.
- For async code: pass `CancellationToken` through the call chain and `.ConfigureAwait(false)` on awaits inside library code (we don't have a sync context, but it documents intent).
- Comments explain *why*, not *what*. The code shows what; the comment is for the reader who's wondering whether the unusual choice was deliberate.

## How to run things

```bash
# Restore and build
dotnet build

# Run the server
dotnet run --project ./src/HelmRepoLite -- --port 8080 --storage-dir ./charts --debug

# Run the tests
dotnet test

# Pack the global tool (output in ./artifacts/)
dotnet pack ./src/HelmRepoLite -c Release
```

## Repo map (where to look first)

| If you need to... | Look at |
| --- | --- |
| Change CLI flags | `src/HelmRepoLite/CliParser.cs` and `ServerOptions.cs` (and update `README.md` and `docs/requirements.md`) |
| Add an HTTP route | `src/HelmRepoLite/Program.cs` |
| Change how `Chart.yaml` is parsed | `src/HelmRepoLite/MiniYaml.cs` (reader) and `src/HelmRepoLite/ChartInspector.cs` (consumer) |
| Change the `index.yaml` schema | `src/HelmRepoLite/IndexBuilder.cs` and `MiniYamlWriter.cs` |
| Change upload/delete behaviour | `src/HelmRepoLite/ChartStore.cs` |
| Add or change auth | `src/HelmRepoLite/BasicAuthMiddleware.cs` |

## Common tasks

### Adding a new field to `index.yaml`

1. Add the property to `ChartMetadata` in `ChartInspector.cs`.
2. Populate it in `ChartInspector.Inspect`.
3. Emit it in `IndexBuilder.BuildEntry`.
4. Add a test in `tests/HelmRepoLite.Tests/ChartInspectorTests.cs`.
5. If it changes wire format, mention it in `docs/design.md` under "Storage layout".

### Adding a new `/api` endpoint

1. Add the route in `Program.cs` inside the `if (!options.DisableApi)` block.
2. If it mutates state, route it through a new method on `ChartStore`.
3. Document it in `README.md`'s API table.
4. Add a `ChartStoreTests` test if it touches the store.

### Fixing a bug in YAML parsing

`MiniYaml` is intentionally narrow. Before adding a feature to it, consider whether the `Chart.yaml` you're parsing is actually valid Helm input. If it uses anchors, flow style, or block scalars, that's a chart problem, not our problem. If it's plain block-style YAML and we still mishandle it, add a test case in `MiniYamlTests.cs` first, then fix.

## Things to *not* do

- Don't add YamlDotNet or any other YAML library. We addressed the trade-off; see `docs/design.md` "Why a hand-rolled YAML reader/writer".
- Don't add `Microsoft.Extensions.Configuration.Yaml` either — it pulls a YAML lib transitively.
- Don't introduce a database or a cache layer. The whole point is "no external state".
- Don't add `WebApplicationFactory` integration tests unless the route logic grows to where unit tests on `ChartStore` aren't enough.
- Don't change the default port, default storage dir, or default anonymous-GET behaviour without a deprecation note in the README — these are part of the public contract.

## Pointers for Claude Code

- Use `dotnet test` to verify your changes; don't rely solely on `dotnet build`.
- When editing C# files, prefer the `str_replace`-equivalent edit over rewriting the whole file.
- If you find yourself wanting to add a NuGet package, stop and ask the human first.
- The `.claude/` directory has additional per-task hints (in `commands/` and `agents/`); read them before starting larger tasks.
