# Requirements

## Background

We need a Helm chart repository that we can stand up inside a CI pipeline (Cake build) and on developer workstations. Existing options (ChartMuseum, Harbor, JFrog) are heavyweight, require Go/containers, and bring more operational burden than the use case justifies. We want something a developer can `dotnet tool install` in one line and a CI agent can launch as part of an early build step.

## Functional requirements

### FR-1: Serve a valid Helm chart repository
- `GET /index.yaml` returns a Helm-compliant index (`apiVersion: v1`, `entries:` map, per-version metadata including `digest`, `urls`, `created`).
- `GET /charts/{name}-{version}.tgz` returns the package bytes with content type `application/gzip`.
- `GET /charts/{name}-{version}.tgz.prov` returns the provenance file when present.
- The repo passes `helm repo add`, `helm repo update`, `helm search repo`, `helm install`, and `helm pull`.

### FR-2: Two publishing paths
- **Drop folder:** A configured directory is watched; any `.tgz` placed in it is validated, moved into storage atomically, and reflected in the index. Files that fail validation stay in the drop folder for inspection.
- **HTTP upload:** `POST /api/charts` accepts either a raw `.tgz` body or a `multipart/form-data` payload with field `chart` (and optional `prov`). The `helm-push`/`helm cm-push` plugin works against this endpoint.

### FR-3: ChartMuseum-compatible API
The following endpoints behave as ChartMuseum documents them:
- `GET /api/charts` → `{name: [versions...]}` JSON
- `GET /api/charts/{name}` → array of versions
- `GET /api/charts/{name}/{version}` → one version
- `POST /api/charts` (with optional `?force=true`)
- `POST /api/prov` (with `?file=<chart>.tgz`)
- `DELETE /api/charts/{name}/{version}`

We do **not** implement ChartMuseum's multi-tenancy (`--depth`) in v1. Routes containing tenant segments return 404.

### FR-4: Authentication
- HTTP Basic auth, opt-in via `--basic-auth-user` + `--basic-auth-pass`.
- `--require-auth-get` toggles whether `GET`/`HEAD` are exempt from auth (default exempt — matches ChartMuseum's `--auth-anonymous-get` default behavior for the most common CI pattern of "anyone can read, only CI can publish").
- Credential comparison uses `CryptographicOperations.FixedTimeEquals`.

### FR-5: Single-binary distribution
- Ship as a `dotnet pack`-able global tool (`<PackAsTool>true</PackAsTool>`).
- Tool command is `helmrepolite`.
- Also runnable as `dotnet run --project src/HelmRepoLite -- ...` for development.
- Also runnable from a published DLL (`dotnet ./HelmRepoLite.dll`) inside Cake without installing the tool globally.

### FR-6: Deterministic, restart-safe
- On startup, the storage directory is fully scanned and the index rebuilt from the `.tgz` files present. **The on-disk `.tgz` files are the source of truth.** `index.yaml` is treated as a derived artifact.
- After every mutation (upload, delete, drop import) `index.yaml` is rewritten on disk and in the in-memory cache.

### FR-7: Operator visibility
- Structured single-line console logs at info / debug levels.
- A `GET /health` endpoint for liveness probes.
- A `GET /` welcome page with the URL to add to `helm repo add`.

## Non-functional requirements

### NFR-1: Zero third-party runtime dependencies
The shipping project (`src/HelmRepoLite`) must compile and run using only what is in the .NET 10 SDK and runtime: `Microsoft.AspNetCore.*`, `System.Formats.Tar`, `System.IO.Compression`, `System.Security.Cryptography`. **No NuGet `PackageReference` entries.** A `Directory.Build.props` target enforces this at build time. Test projects are exempt.

Why: every transitive dependency is a CVE surface, an upgrade chore, and another reason a CI agent's restore can fail. Helm chart repos are operationally simple — there is no good reason this code needs YamlDotNet, Serilog, or Newtonsoft.

### NFR-2: Cross-platform
Runs unmodified on Linux, macOS, Windows. No `P/Invoke`, no platform-specific paths. `FileSystemWatcher` works on all three, with the documented caveat that it can miss events under heavy load — we mitigate this with `POST /api/resync` and a startup full-scan.

### NFR-3: Performance envelope
This is not a thousand-tenant SaaS. The target envelope is:
- Tens of thousands of total chart versions.
- Hundreds of req/sec for `GET /index.yaml`.
- Single-digit concurrent uploads.

The current design serves `index.yaml` from a pre-rendered byte array, so reads do not contend with the mutation lock. If a deployment pushes past this envelope, the design notes (`docs/design.md`) call out where to optimise.

### NFR-4: No external state
No database, no cloud SDK, no message queue. The only persistent state is the storage directory on the local filesystem. The intent is that a developer can `rm -rf ./charts && restart` and be back to a clean state.

### NFR-5: Friendly to ephemeral CI
- Starts in <2 seconds on cold disk.
- Shuts down cleanly on SIGTERM / Ctrl+C.
- Storage directory is created if missing.

## Out of scope (v1)

- OCI registry support (`helm push oci://...`).
- Cloud storage backends (S3, GCS, Azure Blob).
- Multi-tenancy (`--depth`).
- JWT/OAuth.
- Web UI for browsing charts.
- TLS termination — use a reverse proxy.
- Provenance signature verification (we only store and serve `.prov` files).

These are deliberately deferred. If a future need surfaces, design notes for each are tracked in `docs/design.md` under "Future work".

## Acceptance criteria

A v1 release is acceptable when:
1. `dotnet test` passes on Linux, macOS, and Windows.
2. A round trip works end-to-end: package a chart with `helm package`, drop it into the watch folder, run `helm repo update`, `helm install` it into a kind cluster.
3. `helm cm-push` against `POST /api/charts` succeeds for a chart that doesn't yet exist, fails with 409 if it does, and succeeds with `?force=true`.
4. The build output for `dotnet pack -c Release` produces a `.nupkg` that installs cleanly via `dotnet tool install`.
5. The "no third-party packages" build-time check fires if anyone adds a `PackageReference` to the shipping project.
