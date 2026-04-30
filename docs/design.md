# Design

## Architecture overview

```
                                    +-----------------+
                                    |  HTTP requests  |
                                    +--------+--------+
                                             |
                                +------------v---------------+
                                |   Kestrel + minimal APIs   |
                                +------------+---------------+
                                             |
                          +------------------+------------------+
                          |                                     |
                +---------v---------+               +-----------v-----------+
                | BasicAuthMiddleware|               | ChartMuseum-compat /api|
                |   (optional)      |               |       routes          |
                +---------+---------+               +-----------+-----------+
                          |                                     |
                          +------------------+------------------+
                                             |
                                +------------v------------+
                                |       ChartStore        |
                                |  (singleton, mutex'd)   |
                                +------+------+-----+-----+
                                       |      |     |
              +------------------------+      |     +------------------+
              |                               |                        |
   +----------v---------+        +-----------v---------+    +---------v----------+
   |  ChartInspector    |        |   IndexBuilder /    |    |  FileSystemWatcher |
   |  (.tgz parser +    |        |   MiniYamlWriter    |    |  (storage dir)     |
   |   SHA-256 digest)  |        +---------------------+    +--------------------+
   +--------------------+
              |
   +----------v---------+
   |     MiniYaml       |
   |  (Chart.yaml read) |
   +--------------------+
```

## Component map

| Component | Responsibility | Key types |
| --- | --- | --- |
| `Program.cs` | Host bootstrap, route definitions, CLI plumbing | (top-level statements) |
| `CliParser` | Parse argv + env vars into `ServerOptions` | `CliParser.Parse` |
| `ServerOptions` | Immutable runtime config record | `ServerOptions` |
| `BasicAuthMiddleware` | Optional HTTP Basic auth, anonymous-GET aware | `BasicAuthMiddleware.InvokeAsync` |
| `ChartStore` | Single source of truth for the in-memory index; owns all mutations and the file watcher | `ChartStore`, `ChartStore.UploadAsync`, `ChartStore.DeleteAsync` |
| `ChartStoreHealthCheck` | ASP.NET Core `IHealthCheck` backed by `ChartStore.IsReady` | `ChartStoreHealthCheck.CheckHealthAsync` |
| `ChartInspector` | Open a `.tgz`, find `Chart.yaml`, parse it, compute SHA-256 | `ChartInspector.Inspect`, `ChartMetadata` |
| `IndexBuilder` | Build the index.yaml object graph from `ChartMetadata` | `IndexBuilder.Build` |
| `MiniYaml` | Minimal YAML *reader* sufficient for Chart.yaml | `MiniYaml.Parse` |
| `MiniYamlWriter` | Minimal YAML *writer* sufficient for index.yaml (defined in `MiniYaml.cs`) | `MiniYamlWriter.Write` |
| `SimpleJson` | Reflection-free JSON writer; trim-safe replacement for `System.Text.Json` on anonymous types | `SimpleJson.Write`, `SimpleJson.Err` |

## Key decisions

### Why a hand-rolled YAML reader/writer?

The "no third-party packages" rule is the hardest constraint. The two well-known YAML libraries on .NET (YamlDotNet, NetEscapades.Configuration.Yaml) both pull additional dependencies. Helm's YAML usage is narrow: `Chart.yaml` is a flat mapping with a few sequences of mappings, and `index.yaml` is a structure we fully control. A purpose-built reader/writer in ~300 lines covers it without exposing us to a generic YAML attack surface (anchors, tags, type coercion).

**Limits we accept:**
- We don't support anchors/aliases, flow-style `{}`/`[]`, or block scalar styles (`|`, `>`). If a real-world `Chart.yaml` uses one of these, `ChartInspector` throws and the package is rejected. `Chart.yaml` per the Helm spec doesn't need any of them.
- We treat all leaf values as strings. This sidesteps YAML 1.1's notorious `yes`/`no` boolean coercion. Where we need a boolean (`deprecated`), we check for the literal string `"true"`.

### Why a pre-rendered `index.yaml` byte cache?

`GET /index.yaml` is the hottest endpoint — every `helm repo update` hits it. Re-serializing on each request would create lock contention with mutations. Holding the bytes in a `volatile byte[]` field, swapped under the mutation lock, means GETs are completely lock-free.

### Why `SemaphoreSlim` instead of `lock`?

Mutations (`UploadAsync`, `DeleteAsync`) are async because they stream uploads to disk. `lock` doesn't compose with `await`. `SemaphoreSlim(1, 1)` gives us mutex semantics that work in async code.

### Why both a watched storage dir and an upload API?

These are different ergonomic fits:
- **Filesystem copy** is the natural thing in a Cake build: `helm package` writes a `.tgz` directly into the storage directory and `FileSystemWatcher` picks it up automatically. No HTTP plumbing, no auth, no curl-vs-Invoke-WebRequest cross-platform headaches.
- **Upload API** is what `helm cm-push` and most CI/CD platforms know about. Required for ChartMuseum compatibility.

Both paths converge on the same `ChartStore` mutation path (validate → write → re-index), so behaviour is consistent.

### Why `FileSystemWatcher` for the storage dir?

Out-of-band edits happen: a developer might `rm` an old version, an `rsync` job or Cake build might drop in a new `.tgz`. Watching the storage dir keeps the in-memory index honest without requiring a server restart. The watcher can miss events under heavy load or on network-backed storage (NFS, Azure Files) — we mitigate with `POST /api/resync` and a startup full-scan.

### Why a hand-rolled JSON writer (`SimpleJson`)?

When published with `PublishTrimmed=true`, `System.Text.Json`'s reflection-based serialiser is stripped. Anonymous types and `Dictionary<string,object?>` fail at runtime with `NotSupportedException: TypeInfoResolver of type '[]'`. The fix is `SimpleJson` — a ~100-line writer for the concrete types this server serialises (`null`, `bool`, `string`, `Dictionary<string,object?>`, `List<object?>`). No reflection, no attributes, no source-generator ceremony.

### Why is `index.yaml` written to disk *and* served from memory?

Two reasons:
1. Some operators scrape the directory with `rsync` / GitHub Pages / S3 sync to mirror the repo. They expect the file on disk.
2. It's a useful diagnostic: `cat charts/index.yaml` shows current state without an HTTP round trip.

The in-memory copy is the source of truth for HTTP responses; the on-disk copy is a side effect.

### Storage layout

```
<storage-dir>/
├── index.yaml              <- regenerated after every mutation
├── alpha-0.1.0.tgz
├── alpha-0.1.0.tgz.prov    <- optional, served at GET /charts/...
├── alpha-0.2.0.tgz
└── beta-1.0.0.tgz
```

Filename convention is `{name}-{version}.tgz`. We enforce this on uploads and trust it on scan; if a `.tgz` on disk doesn't match its inner `Chart.yaml`'s name/version, we keep it but use the inner values for the index entry. The download URL in `index.yaml` always uses the on-disk filename so links resolve.

### Concurrency model

- One singleton `ChartStore`.
- One `SemaphoreSlim(1,1)` serializes all mutations and all index rebuilds.
- Reads of `IndexBytes` are lock-free via volatile field swap.
- `Snapshot()` and the `/api/charts` reads briefly take the mutex to copy the dictionary; copies are returned to callers, so they can iterate without a lock.

This design caps write throughput at one upload at a time. That's intentional — we don't expect concurrent uploads in any realistic scenario.

### Error handling strategy

- **Bad uploads:** `ChartInspector` throws → endpoint returns 400 with the error message. Temp file cleaned up.
- **Duplicate uploads without `?force=true`:** `ChartStore` throws `InvalidOperationException` → endpoint returns 409.
- **Corrupted files in storage at startup:** logged at warning level, file is skipped, server still starts. Nothing fatal at boot.
- **Watcher exceptions:** caught and logged; the watcher keeps running.

We deliberately don't surface stack traces to clients. Status code + a one-line `{"error": "..."}` payload is plenty.

## Future work

These are deferred from v1 but the design accommodates them:

- **Multi-tenancy.** `ChartStore` would become `IDictionary<string, ChartStore>` keyed by tenant; the route prefix `/api/{tenant}` would dispatch. No core change.
- **Cloud storage.** Introduce an `IChartStorage` interface with `LocalChartStorage` and (eventually) `S3ChartStorage`. The watcher abstraction would be optional.
- **OCI registry.** A separate set of routes (`/v2/...`) that doesn't share the index-rebuild path. Out of scope for the chart-museum-replacement sweet spot.
- **Helm provenance verification.** We store `.prov` today but don't verify signatures. A `--verify-uploads` flag could shell out to GPG.
- **Search/filter.** ChartMuseum supports `?offset=` and `?limit=` for paginating `/api/charts`. Trivial to add when needed.

## Testing strategy

- **Unit:** `MiniYaml` round-trip, `ChartInspector` against a real `.tgz` built in-test, `CliParser` flag forms.
- **Integration:** `ChartStore` upload/delete/drop-folder lifecycle (in `tests/HelmRepoLite.Tests/ChartStoreTests.cs`).
- **Manual:** `dotnet run` + `helm install` against a kind cluster, documented in `docs/manual-test.md`.

We deliberately don't pull `WebApplicationFactory` or `Microsoft.AspNetCore.Mvc.Testing` in for HTTP-level integration tests; the per-route logic is thin enough that the underlying `ChartStore` tests cover the meaningful behaviour. If routes grow, that calculus changes.
