# HelmRepoLite

A lightweight, self-hosted, [ChartMuseum](https://github.com/helm/chartmuseum)-compatible
Helm chart repository server written in .NET 10.

**Key properties**
- **Zero third-party runtime dependencies** — Kestrel, minimal APIs, FileSystemWatcher, and
  `System.Formats.Tar` all ship with the .NET SDK/runtime.
- **Three distribution models** — install as a `dotnet` global tool, run as a fully
  self-contained executable (no .NET required), or deploy as a container in Docker/Kubernetes.
- **Live filesystem sync** — copy, replace, or delete a `.tgz` in the storage directory and the
  index updates automatically; no restart required.
- **Browser UI** — navigate to `/` to browse packages, download charts, and delete versions.
- **ChartMuseum-compatible API** — `helm repo add`, `helm install`, `helm-push`, and
  `curl`-based uploads all work without modification.

---

## Installation

### Option A — .NET global tool (requires .NET 10 runtime)

```powershell
# Install from NuGet (once published)
dotnet tool install -g HelmRepoLite

# Or install from a local build
.\build.ps1 -Version 1.0.0
dotnet tool install -g HelmRepoLite --add-source .\artifacts\nuget
```

After installation the `helmrepolite` command is available everywhere in your shell.

### Option B — Self-contained executable (no .NET required)

Download the binary for your platform from the release page, or build it yourself:

```powershell
.\build.ps1 -Version 1.0.0
# Executable is at: .\artifacts\standalone\win-x64\helmrepolite.exe
```

Available platforms: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

---

## Quick start

```powershell
# Start the server — storage defaults to ./charts, HTTP port defaults to 8080
helmrepolite

# Open http://localhost:8080 in a browser to see the package listing

# Add the repo to Helm
helm repo add local http://localhost:8080
helm repo update
helm search repo local
```

Drop any `.tgz` Helm chart into `./charts` and the index updates within seconds.

---

## Configuration

Every flag is also readable from the `HELMREPOLITE_<UPPER_SNAKE_CASE>` environment variable,
e.g. `HELMREPOLITE_STORAGE_DIR=./my-charts`.

### Core

| Flag | Env var | Default | Description |
|------|---------|---------|-------------|
| `--port <int>` | `HELMREPOLITE_PORT` | `8080` | HTTP port |
| `--host <ip>` | `HELMREPOLITE_HOST` | `0.0.0.0` | Bind address |
| `--storage-dir <path>` | `HELMREPOLITE_STORAGE_DIR` | `./charts` | Directory that holds `.tgz` files and `index.yaml` |
| `--chart-url <url>` | `HELMREPOLITE_CHART_URL` | auto-detected | Base URL written into `index.yaml` for chart download links |
| `--debug` | `HELMREPOLITE_DEBUG` | off | Verbose request and operation logs |

### HTTPS

HTTPS requires `--https-port` and exactly one certificate source.
HTTP continues to serve on `--port` alongside HTTPS.

| Flag | Env var | Description |
|------|---------|-------------|
| `--https-port <int>` | `HELMREPOLITE_HTTPS_PORT` | HTTPS port |
| `--https-cert-file <path>` | `HELMREPOLITE_HTTPS_CERT_FILE` | Path to a PFX/PKCS#12 certificate file |
| `--https-cert-password <s>` | `HELMREPOLITE_HTTPS_CERT_PASSWORD` | Password for the PFX file (default: empty) |
| `--https-cert-thumbprint <s>` | `HELMREPOLITE_HTTPS_CERT_THUMBPRINT` | SHA-1 thumbprint — searches `CurrentUser\My` then `LocalMachine\My` |
| `--https-cert-subject <s>` | `HELMREPOLITE_HTTPS_CERT_SUBJECT` | Subject/CN — searches `CurrentUser\My` then `LocalMachine\My` |

```powershell
# HTTPS with a PFX file
helmrepolite --https-port 8443 --https-cert-file server.pfx --https-cert-password secret

# HTTPS with a Windows certificate store entry
helmrepolite --https-port 8443 --https-cert-thumbprint AB12CD34...
```

### Authentication

| Flag | Env var | Default | Description |
|------|---------|---------|-------------|
| `--basic-auth-user <s>` | `HELMREPOLITE_BASIC_AUTH_USER` | _disabled_ | Enables HTTP Basic auth |
| `--basic-auth-pass <s>` | `HELMREPOLITE_BASIC_AUTH_PASS` | | Password for Basic auth |
| `--require-auth-get` | `HELMREPOLITE_REQUIRE_AUTH_GET` | off | Require auth for `GET` routes too (default: anonymous reads) |

### API behaviour

| Flag | Env var | Default | Description |
|------|---------|---------|-------------|
| `--allow-overwrite` | `HELMREPOLITE_ALLOW_OVERWRITE` | off | Allow re-uploading an existing version without `?force=true` |
| `--disable-delete` | `HELMREPOLITE_DISABLE_DELETE` | off | `DELETE /api/charts/…` returns 405 |
| `--disable-api` | `HELMREPOLITE_DISABLE_API` | off | All `/api` routes return 404 (read-only mirror) |

### CI / shutdown

| Flag | Env var | Default | Description |
|------|---------|---------|-------------|
| `--enable-shutdown` | `HELMREPOLITE_ENABLE_SHUTDOWN` | off | Enables `POST /shutdown` to stop the process gracefully |

---

## Storage directory

The storage directory (`--storage-dir`, default `./charts`) is the single source of truth.

| Action | Result |
|--------|--------|
| Copy a `.tgz` into the directory | Indexed within seconds |
| Replace a `.tgz` in the directory | Re-indexed automatically |
| Delete a `.tgz` from the directory | Removed from the index |
| Delete `index.yaml` | Regenerated automatically from the in-memory index |

On startup the server performs a full rescan so any changes made while it was offline are picked
up immediately.

### Uploading via the API

```bash
# Raw binary upload
curl --data-binary "@mychart-0.1.0.tgz" http://localhost:8080/api/charts

# Multipart upload (chart + optional provenance)
curl -F "chart=@mychart-0.1.0.tgz" -F "prov=@mychart-0.1.0.tgz.prov" \
     http://localhost:8080/api/charts

# Overwrite an existing version
curl --data-binary "@mychart-0.1.0.tgz" "http://localhost:8080/api/charts?force=true"

# helm-push plugin
helm plugin install https://github.com/chartmuseum/helm-push
helm cm-push mychart-0.1.0.tgz local
```

---

## HTTP API reference

### Helm-facing routes

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/` | Browser UI — package listing |
| `GET` | `/index.yaml` | Helm repository index |
| `GET` | `/charts/{file}.tgz` | Chart package download |
| `GET` | `/charts/{file}.tgz.prov` | Provenance file download |
| `GET` | `/health/live` | Liveness probe — `{"status":"Healthy"}` |
| `GET` | `/health/ready` | Readiness probe — `{"status":"Healthy"}` once storage scan completes |
| `GET` | `/server/info` | ChartMuseum compatibility probe — `{"version":"…","storage":"local"}` |

### ChartMuseum-compatible API (`/api`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/charts` | All charts grouped by name |
| `GET` | `/api/charts/{name}` | All versions of a named chart |
| `GET` | `/api/charts/{name}/{version}` | One specific version |
| `POST` | `/api/charts` | Upload a chart (`?force=true` to overwrite) |
| `POST` | `/api/prov` | Upload a provenance file (`?file=<chart>.tgz`) |
| `DELETE` | `/api/charts/{name}/{version}` | Delete a chart version and its `.prov` |
| `POST` | `/api/resync` | Force a full storage re-scan |

### Operational

| Method | Path | Enabled when |
|--------|------|--------------|
| `POST` | `/shutdown` | `--enable-shutdown` is set |

---

## CI pipeline integration

### PowerShell / Windows

```powershell
# Start the server in the background with shutdown enabled
$proc = Start-Process helmrepolite `
    -ArgumentList "--storage-dir=./charts","--enable-shutdown","--port=8080" `
    -PassThru

# Wait for it to be ready
$timeout = [DateTime]::UtcNow.AddSeconds(15)
while ([DateTime]::UtcNow -lt $timeout) {
    try { Invoke-RestMethod http://localhost:8080/health/ready -ErrorAction Stop; break }
    catch { Start-Sleep -Milliseconds 200 }
}

# ... do your Helm work ...
helm package ./mychart
Copy-Item mychart-0.1.0.tgz ./charts/   # auto-indexed immediately
helm repo update local
helm install myapp local/mychart

# Shut down cleanly
Invoke-RestMethod -Method POST http://localhost:8080/shutdown
$proc.WaitForExit(10000)
```

### bash / Linux / macOS

```bash
helmrepolite --storage-dir=./charts --enable-shutdown --port=8080 &
SERVER_PID=$!

# Wait for ready
until curl -sf http://localhost:8080/health/ready > /dev/null; do sleep 0.2; done

# ... do your Helm work ...
cp mychart-0.1.0.tgz ./charts/
helm repo update local

# Shut down (SIGTERM is also handled gracefully)
curl -s -X POST http://localhost:8080/shutdown
wait $SERVER_PID
```

---

## Container (Docker)

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — to run `build.ps1`
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) with WSL 2 integration enabled
- WSL 2 (the bash build script must run inside WSL)

### 1. Build the Linux binary on Windows

The Dockerfile copies the pre-built self-contained binary from `artifacts/standalone/linux-x64/`.
Run `build.ps1` first to produce it:

```powershell
.\build.ps1 -Version 1.2.3 -Targets linux-x64
```

### 2. Build and push the image from WSL

```bash
# Navigate to the repo root in WSL (adjust path to match your Windows username)
cd /mnt/c/Users/<you>/source/HelmRepoLite

chmod +x docker/docker-build.sh

# Usage: ./docker/docker-build.sh <repository> <username> <password> [version]
./docker/docker-build.sh ghcr.io/myorg/helmrepolite myuser mytoken 1.2.3
```

The script will:
1. Verify the binary exists in `artifacts/standalone/linux-x64/`
2. `docker login` to the registry
3. `docker build` using `docker/Dockerfile` (build context = repo root)
4. `docker push` the tagged image

### 3. Run the container locally

```bash
docker run -d \
  --name helmrepolite \
  -p 8080:8080 \
  -v $(pwd)/charts:/charts \
  ghcr.io/myorg/helmrepolite:1.2.3

# Open http://localhost:8080
```

### Environment variables in Docker

Every flag is available as a `HELMREPOLITE_*` environment variable:

```bash
docker run -d \
  --name helmrepolite \
  -p 8080:8080 \
  -v $(pwd)/charts:/charts \
  -e HELMREPOLITE_BASIC_AUTH_USER=admin \
  -e HELMREPOLITE_BASIC_AUTH_PASS=secret \
  -e HELMREPOLITE_ALLOW_OVERWRITE=true \
  ghcr.io/myorg/helmrepolite:1.2.3
```

---

## Kubernetes (Helm chart)

The chart is in `helm/helmrepolite/`. It deploys a single-replica pod backed by a PVC, a
ClusterIP service, and an optional ingress-nginx Ingress.

### Prerequisites

- [Helm 3](https://helm.sh/docs/intro/install/)
- [kubectl](https://kubernetes.io/docs/tasks/tools/) configured for your cluster
- [ingress-nginx](https://kubernetes.github.io/ingress-nginx/deploy/) installed (if using ingress)
- A TLS secret in the target namespace (if using HTTPS — see below)

### Quick deploy (no ingress, port-forward access)

```bash
helm upgrade --install helmrepolite ./helm/helmrepolite \
  --set image.repository=ghcr.io/myorg/helmrepolite \
  --set image.tag=1.2.3 \
  --namespace helmrepo --create-namespace

# Access locally
kubectl port-forward svc/helmrepolite 8080:80 -n helmrepo
helm repo add local http://localhost:8080
```

### Deploy with ingress and TLS

```bash
# Create a TLS secret from a certificate (or use cert-manager to manage it automatically)
kubectl create secret tls helm-tls-secret \
  --cert=path/to/tls.crt \
  --key=path/to/tls.key \
  --namespace helmrepo

helm upgrade --install helmrepolite ./helm/helmrepolite \
  --namespace helmrepo --create-namespace \
  --set image.repository=ghcr.io/myorg/helmrepolite \
  --set image.tag=1.2.3 \
  --set ingress.enabled=true \
  --set ingress.host=helm.example.com \
  --set ingress.tls.enabled=true \
  --set ingress.tls.secretName=helm-tls-secret
```

After deployment, the server is reachable at `https://helm.example.com` and `index.yaml` will
contain `https://helm.example.com` as the base URL for all chart download links.

### Deploy with Basic auth

```bash
helm upgrade --install helmrepolite ./helm/helmrepolite \
  --namespace helmrepo --create-namespace \
  --set image.repository=ghcr.io/myorg/helmrepolite \
  --set image.tag=1.2.3 \
  --set ingress.enabled=true \
  --set ingress.host=helm.example.com \
  --set ingress.tls.enabled=true \
  --set ingress.tls.secretName=helm-tls-secret \
  --set server.auth.enabled=true \
  --set server.auth.username=admin \
  --set server.auth.password=secret

# Add the authenticated repo to Helm
helm repo add local https://helm.example.com \
  --username admin --password secret
```

### Deploy using a values file (recommended for production)

```yaml
# my-values.yaml
image:
  repository: ghcr.io/myorg/helmrepolite
  tag: "1.2.3"

ingress:
  enabled: true
  host: helm.example.com
  tls:
    enabled: true
    secretName: helm-tls-secret

server:
  auth:
    enabled: true
    existingSecret: helmrepolite-auth   # pre-created Secret with 'username' and 'password' keys
  allowOverwrite: true

persistence:
  size: 20Gi
  storageClass: standard
  # mountPath: /data           # optional: mount PVC here when storageDir is a subdirectory of the PVC

resources:
  requests:
    cpu: 50m
    memory: 64Mi
  limits:
    cpu: 500m
    memory: 256Mi
```

```bash
helm upgrade --install helmrepolite ./helm/helmrepolite \
  --namespace helmrepo --create-namespace \
  --values my-values.yaml
```

### Reverse proxy note

HelmRepoLite must be deployed at the **root of a dedicated hostname**
(e.g. `helm.example.com`) — not at a sub-path (e.g. `example.com/helm`).
The server has no path-prefix support; chart download URLs in `index.yaml` would break
at a sub-path. `helm install` will fail immediately with a `fail` if you configure
`ingress.path` to anything other than `/` without also setting `server.chartUrl` explicitly.

### Uploading charts to a Kubernetes deployment

```bash
# Option 1 — kubectl cp into the pod (no network upload needed)
POD=$(kubectl get pod -l app.kubernetes.io/name=helmrepolite \
      -n helmrepo -o jsonpath='{.items[0].metadata.name}')
kubectl cp mychart-0.1.0.tgz helmrepo/${POD}:/charts/mychart-0.1.0.tgz
# The server auto-indexes the file within seconds — no restart needed.

# Option 2 — HTTP upload
curl --data-binary "@mychart-0.1.0.tgz" https://helm.example.com/api/charts

# Option 3 — PowerShell
Invoke-RestMethod -Method POST -Uri https://helm.example.com/api/charts \
    -InFile mychart-0.1.0.tgz -ContentType "application/octet-stream"
```

### Upgrade and uninstall

```bash
# Upgrade image to a new version
helm upgrade helmrepolite ./helm/helmrepolite \
  --namespace helmrepo --reuse-values \
  --set image.tag=1.3.0

# Uninstall (PVC is retained by default — charts data is safe)
helm uninstall helmrepolite --namespace helmrepo
```

---

## Helm compatibility and gap analysis

### Helm version support

| Helm version | Status | Notes |
|---|---|---|
| Helm 3.x (any) | ✅ Full support | All standard commands work |
| Helm 4.x | ✅ Full support | HTTP repo protocol is unchanged in Helm 4 |
| Helm 2.x | ⚠️ Probably works | Untested; `apiVersion: v1` charts should index fine |

### Command support

| Command | Status | Notes |
|---|---|---|
| `helm repo add` / `update` / `remove` | ✅ | Core use case |
| `helm search repo` | ✅ | Uses local cached index |
| `helm install` / `upgrade` / `rollback` | ✅ | Downloads `.tgz` via URL in `index.yaml` |
| `helm pull` / `fetch` | ✅ | |
| `helm show chart/values/readme` | ✅ | Downloads `.tgz` locally |
| `helm dependency update` | ✅ | Reads index, downloads dependency charts |
| `helm cm-push` (helm-push plugin) | ✅ | `POST /api/charts` multipart |
| `helm package` | ✅ | Local only; output `.tgz` can be copied to storage dir |
| `helm verify` | ✅ | `.prov` files stored and served; Helm verifies client-side |
| `helm repo add --username --password` | ✅ | HTTP Basic auth supported |
| `helm push` (built-in, OCI) | ❌ | Requires OCI registry protocol — see below |
| `helm registry login` | ❌ | OCI only |

### Gap analysis

#### `GET /server/info` — ✅ implemented
The `helm-push` plugin and some dashboard tools probe this endpoint to confirm they are talking
to a ChartMuseum-compatible server. Returns
`{"version":"helmrepolite-x.y.z","storage":"local"}`.

#### OCI registry protocol — ❌ not implemented
Helm 3.8+ added a built-in `helm push` command that uses the
[OCI Distribution Spec](https://github.com/opencontainers/distribution-spec) (Docker Registry
v2 API), which is entirely separate from the ChartMuseum HTTP API.

```sh
# This will NOT work:
helm push mychart-0.1.0.tgz oci://localhost:5000/charts

# This WILL work (uses ChartMuseum API):
helm cm-push mychart-0.1.0.tgz local
```

**Does Helm 4 require OCI?** No. Helm 4 continues to support the HTTP repository protocol.
Every major public repository (Bitnami, cert-manager, ingress-nginx, etc.) uses it. OCI is an
_additional_ distribution method, not a replacement for the classic HTTP repo.

**Tradeoff if OCI were added:** Implementing the OCI Distribution Spec would require roughly as
many new endpoints as the current server has (blob uploads, manifest push/pull, tag listing,
`/v2/` compatibility check). It is well-defined but non-trivial, and a mature reference
implementation (`distribution/distribution`) exists as an alternative. Per the project rules
this warrants a discussion before committing to hand-coding it.

#### CORS headers — ❌ not implemented
Browser-based Helm tools (e.g.
[Helm Dashboard](https://github.com/komodorio/helm-dashboard)) make XHR requests directly to
the repository. Without `Access-Control-Allow-Origin` response headers those requests are
blocked by the browser. The Helm CLI itself is unaffected.

#### URL prefix / context path — ❌ not implemented
If the server sits behind a reverse proxy at a sub-path (e.g. `/helm/`), chart download URLs
in `index.yaml` will be incorrect. The `--chart-url` flag is a manual workaround for the base
URL but does not rewrite individual chart file paths.

### Non-goals (by design)

| Feature | Notes |
|---|---|
| Cloud storage backends (S3, GCS, Azure Blob) | Local filesystem only |
| Multi-tenancy / `--depth` | Single flat repository |
| Server-side provenance validation | `.prov` files are stored and served; Helm verifies them client-side |
| JWT / OAuth authentication | HTTP Basic auth only |
| Pagination on `/api/charts` | Full index always returned |

---

## Development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PowerShell 7+ (for `build.ps1`)

### Build, test, and publish

```powershell
# Run tests only
dotnet test

# Full build: tests + NuGet package + standalone executables for all platforms
.\build.ps1

# Stamp a specific version
.\build.ps1 -Version 1.2.3
```

Build output in `artifacts/`:

```
artifacts/
  nuget/
    HelmRepoLite.1.2.3.nupkg      <- dotnet tool install -g
  standalone/
    win-x64/helmrepolite.exe       <- self-contained, no .NET required
    win-arm64/helmrepolite.exe
    linux-x64/helmrepolite
    linux-arm64/helmrepolite
    osx-x64/helmrepolite
    osx-arm64/helmrepolite
```

### Run locally during development

```powershell
# From source with debug logging (F5 in Visual Studio also works)
dotnet run --project ./src/HelmRepoLite -- --debug
```

### Project structure

```
src/
  HelmRepoLite/
    Program.cs              Host setup and route registration
    ChartStore.cs           In-memory index + FileSystemWatcher
    ChartStoreHealthCheck.cs  ASP.NET Core health check backed by ChartStore.IsReady
    ChartInspector.cs       Reads a .tgz, extracts Chart.yaml, computes SHA-256
    MiniYaml.cs             Purpose-built YAML reader/writer for Chart.yaml / index.yaml
    IndexBuilder.cs         Builds the in-memory index.yaml document
    SimpleJson.cs           Reflection-free JSON writer (trim-safe alternative to System.Text.Json)
    CliParser.cs            Zero-dependency CLI argument parser
    ServerOptions.cs        Strongly-typed configuration record
    BasicAuthMiddleware.cs  HTTP Basic auth middleware

tests/
  HelmRepoLite.Tests/
    ChartInspectorTests.cs
    ChartStoreTests.cs
    MiniYamlTests.cs
    CliParserTests.cs
    TestChartBuilder.cs     Builds valid .tgz fixtures in-process
```

### Design constraints

- **No third-party runtime packages.** The zero-dependency property is a first-class feature.
  Before adding any `<PackageReference>`, read
  [`.github/copilot-instructions.md`](.github/copilot-instructions.md).
- `ChartStore` is the single source of truth for the in-memory index; all mutations go through
  its `SemaphoreSlim` mutex.
- `index.yaml` is always fully regenerated from the in-memory state — it is never read back at
  startup.
- `MiniYaml` is intentionally limited to the Chart.yaml subset. It handles block scalars,
  compact sequences, multi-line plain scalars, and quoted strings — not general YAML 1.2.

---

## License

MIT — see [LICENSE](LICENSE).
