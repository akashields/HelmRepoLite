# Manual end-to-end test

A 5-minute smoke test you can run against any local Kubernetes (kind, k3d, minikube, Docker Desktop, etc.).

## 1. Start the server

```bash
mkdir -p /tmp/helm-repo
dotnet run --project ./src/HelmRepoLite -- \
    --port 8080 \
    --storage-dir /tmp/helm-repo \
    --debug
```

In another terminal:

## 2. Create a chart and drop it in

```bash
helm create demo
helm package demo --destination /tmp/helm-repo
ls /tmp/helm-repo                      # should now contain demo-0.1.0.tgz + index.yaml
```

## 3. Add the repo to Helm and install

```bash
helm repo add local http://localhost:8080
helm repo update
helm search repo local                  # should list demo

kubectl create namespace helm-smoke
helm install smoke local/demo --namespace helm-smoke
kubectl get pods -n helm-smoke
```

## 4. Try the upload API

```bash
# bump version, repackage, push via API
sed -i 's/^version: 0.1.0$/version: 0.1.1/' demo/Chart.yaml
helm package demo --destination /tmp
curl -i --data-binary "@/tmp/demo-0.1.1.tgz" http://localhost:8080/api/charts
helm repo update
helm search repo local --versions       # should show both 0.1.0 and 0.1.1
```

## 5. Try `helm cm-push`

```bash
helm plugin install https://github.com/chartmuseum/helm-push
sed -i 's/^version: 0.1.1$/version: 0.1.2/' demo/Chart.yaml
helm package demo --destination /tmp
helm cm-push /tmp/demo-0.1.2.tgz local
helm repo update
helm search repo local --versions
```

## 6. Delete a version

```bash
curl -i -X DELETE http://localhost:8080/api/charts/demo/0.1.0
helm repo update
helm search repo local --versions       # 0.1.0 is gone
```

## 7. Tear down

```bash
helm uninstall smoke --namespace helm-smoke
kubectl delete namespace helm-smoke
helm repo remove local
# Ctrl+C the server
rm -rf /tmp/helm-repo
```

## Expected log output

A typical successful upload looks like:

```
14:23:01  info: HelmRepoLite[0] HelmRepoLite listening on http://localhost:8080
14:23:01  info: HelmRepoLite[0]   Storage: /tmp/helm-repo
14:23:01  info: HelmRepoLite[0] Indexed 0 chart packages from /tmp/helm-repo
14:23:18  info: HelmRepoLite[0] Detected change in /tmp/helm-repo; rescanning
```

## Troubleshooting

- **`helm repo update` fails with "404 Not Found"** — double-check the URL has no trailing slash issues. Try `curl http://localhost:8080/index.yaml` first.
- **`helm install` fails with a download error** — check `--chart-url`. The URL Helm sees in `index.yaml` must be reachable from where Helm runs.
- **Copied chart not indexed** — `FileSystemWatcher` can be flaky on network shares or in containers. Use a local path. `POST /api/resync` (or the Resync button in the UI) forces a re-scan.
- **Upload succeeds but next upload of same version returns 409** — this is the documented behaviour. Pass `?force=true` or start the server with `--allow-overwrite`.
