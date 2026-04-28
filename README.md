# HelmRepoLite

A lightweight, self-hosted, ChartMuseum-compatible Helm chart repository server written in .NET 10.

**Goals:**
- Zero third-party runtime dependencies — only what ships with the .NET SDK / runtime.
- Distributable as a `dotnet tool` global tool, runnable on any developer machine or CI agent that has .NET installed.
- Compatible enough with the [ChartMuseum HTTP API](https://github.com/helm/chartmuseum) that `helm repo add`, `helm install`, and the `helm-push` plugin all just work.
- Two ways to publish: drop a `.tgz` into a watched folder (zero-API CI pattern), or `POST /api/charts` like ChartMuseum.

**Non-goals (today):** cloud storage backends, OCI registry, multi-tenancy/`--depth`, JWT auth, web UI.

---

## Quick start

### As a .NET global tool

```bash
# build the package locally
dotnet pack ./src/HelmRepoLite -c Release

# install globally from the local artifacts folder
dotnet tool install --global --add-source ./artifacts HelmRepoLite

# run it
helmrepolite --port 8080 --storage-dir ./charts --drop-dir ./drop
```

### From source

```bash
dotnet run --project ./src/HelmRepoLite -- --port 8080 --storage-dir ./charts
```

### Talking to it with Helm

```bash
helm repo add local http://localhost:8080
helm repo update
helm search repo local

# publish a chart by dropping the file (no API call needed)
helm package mychart/
mv mychart-0.1.0.tgz ./drop/

# or publish via the ChartMuseum upload API
curl --data-binary "@mychart-0.1.0.tgz" http://localhost:8080/api/charts

# or with the helm-push plugin
helm plugin install https://github.com/chartmuseum/helm-push
helm cm-push mychart-0.1.0.tgz local
```

---

## CLI reference

Every flag also reads from the `HELMREPOLITE_<UPPER_SNAKE>` environment variable.

| Flag | Default | Description |
| --- | --- | --- |
| `--port <int>` | `8080` | TCP port to bind |
| `--host <ip>` | `0.0.0.0` | Bind address |
| `--storage-dir <path>` | `./charts` | Where `.tgz` and `index.yaml` live |
| `--drop-dir <path>` | _disabled_ | Folder watched for new `.tgz`; auto-imported |
| `--chart-url <url>` | auto | Absolute base URL emitted in `index.yaml` |
| `--basic-auth-user <s>` | _disabled_ | Enables HTTP Basic auth |
| `--basic-auth-pass <s>` | | Password for Basic auth |
| `--require-auth-get` | off | Require auth for `GET` too (default: anonymous reads allowed) |
| `--allow-overwrite` | off | Allow re-uploading existing versions without `?force=true` |
| `--disable-delete` | off | `DELETE /api/charts/...` returns 405 |
| `--disable-api` | off | All `/api` routes return 404 (read-only mirror) |
| `--debug` | off | Verbose request logs |

---

## HTTP API

### Helm-facing routes (read)

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/index.yaml` | Repository index |
| `GET` | `/charts/{file}.tgz` | Chart package |
| `GET` | `/charts/{file}.tgz.prov` | Provenance file |
| `GET` | `/health` | Liveness probe — `{"status":"ok"}` |

### ChartMuseum-compatible API (`/api`)

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/charts` | All charts grouped by name |
| `GET` | `/api/charts/{name}` | All versions of a chart |
| `GET` | `/api/charts/{name}/{version}` | One specific version |
| `POST` | `/api/charts` | Upload a chart (raw body or `multipart/form-data` with field `chart` and optional `prov`); accepts `?force=true` |
| `POST` | `/api/prov` | Upload a `.prov` file; requires `?file=<chart>.tgz` |
| `DELETE` | `/api/charts/{name}/{version}` | Remove a chart version (and its `.prov`) |
| `POST` | `/api/resync` | Force a full storage re-scan |

---

## Two ways to publish

**Drop folder.** Configure `--drop-dir` and copy a `.tgz` into it. A file watcher imports the package, validates it, moves it into storage atomically, and rebuilds the index. This is the most natural pattern in Cake/CI scripts: `helm package` → `Move-Item` → done.

**HTTP upload.** `POST /api/charts` with the `.tgz` as the body. Works with `curl --data-binary`, with `helm cm-push`, and with multipart uploads from CI tooling.

---

## How it slots into a Cake CI build

Recommended pattern: build the project as part of an early CI step, start the server in the background, run subsequent steps that need a live Helm repo, then stop it.

```csharp
// Cake snippet (illustrative)
Task("StartHelmRepo")
    .Does(() =>
{
    DotNetBuild("./tools/HelmRepoLite/src/HelmRepoLite/HelmRepoLite.csproj",
        new DotNetBuildSettings { Configuration = "Release" });

    var process = StartAndReturnProcess("dotnet",
        new ProcessSettings {
            Arguments = $"./tools/HelmRepoLite/src/HelmRepoLite/bin/Release/net10.0/HelmRepoLite.dll" +
                        $" --port 8080 --storage-dir ./build/charts --drop-dir ./build/drop"
        });
    // stash process so a later task can kill it
});
```

See [`docs/cake-integration.md`](docs/cake-integration.md) for a complete example.

---

## Development

```bash
# Build everything
dotnet build

# Run the tests
dotnet test

# Run the server with debug logging
dotnet run --project ./src/HelmRepoLite -- --debug
```

The `Directory.Build.props` enforces the **no-third-party-packages** rule at compile time. If you genuinely need a package, set `<AllowThirdPartyPackages>true</AllowThirdPartyPackages>` in the offending csproj and explain the trade-off in the PR.

---

## Docs

- [`docs/requirements.md`](docs/requirements.md) — what we're building and why
- [`docs/design.md`](docs/design.md) — architecture, component map, key decisions
- [`docs/cake-integration.md`](docs/cake-integration.md) — running it inside a Cake CI pipeline
- [`CLAUDE.md`](CLAUDE.md) — instructions for Claude Code when contributing

---

## License

MIT — see [LICENSE](LICENSE).
