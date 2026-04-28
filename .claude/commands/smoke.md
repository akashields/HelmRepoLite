---
description: Run the end-to-end smoke test against a real helm CLI on this machine.
---

Run the manual smoke test described in `docs/manual-test.md`. The user must have `helm` installed locally; check that first with `helm version --short`. If `helm` is missing, stop and tell the user how to install it for their OS.

Steps in order, with verification at each:

1. Pick a free port (default 8080; check it's free with `ss -ltn | grep :8080` or platform equivalent).
2. Launch HelmRepoLite in the background with debug logging, writing logs to `/tmp/helmrepolite-smoke.log`. Use `nohup`/`Start-Process` so it survives this shell.
3. Wait up to 10s for `GET /health` to return 200.
4. `helm create /tmp/smoke-demo`, then `helm package /tmp/smoke-demo --destination /tmp/smoke-drop` (which the server is watching).
5. Verify `/tmp/charts/smoke-demo-0.1.0.tgz` appears within 5s.
6. `helm repo add smoke http://localhost:<port>` and `helm repo update smoke`.
7. `helm search repo smoke` should list `smoke/smoke-demo`.
8. Repackage at version 0.1.1, push via `curl --data-binary @smoke-demo-0.1.1.tgz http://localhost:<port>/api/charts`. Verify it appears.
9. `curl -X DELETE http://localhost:<port>/api/charts/smoke-demo/0.1.0`. Verify it disappears from the index.
10. **Cleanup always runs**, even on failure: kill the server process, `helm repo remove smoke`, delete `/tmp/smoke-demo /tmp/smoke-drop /tmp/charts /tmp/helmrepolite-smoke.log`.

If any step fails, dump the last 50 lines of the server log and stop.
