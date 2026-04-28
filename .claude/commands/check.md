---
description: Run the full local quality gate (build + test + tool pack verification).
---

You are about to run the same checks that CI runs. Do them in order, stopping at the first failure and reporting clearly.

1. **Restore + build** — `dotnet build -c Release`. This also fires the `ForbidThirdPartyPackages` target. If it fails citing PackageReference, that's the rule, not a bug — push back on whoever added the package.
2. **Tests** — `dotnet test -c Release --no-build`. Report any failures with the test name and the assertion message.
3. **Pack the tool** — `dotnet pack ./src/HelmRepoLite -c Release --no-build`. Confirm a `.nupkg` ends up in `./artifacts/`.
4. **Smoke-install** (optional, only if step 3 succeeded and we're on a machine with `dotnet` tools available): `dotnet tool install --global --add-source ./artifacts HelmRepoLite --version <version-from-csproj>` then `helmrepolite --version`. Uninstall after with `dotnet tool uninstall --global HelmRepoLite`.

Output a one-line summary at the end: `✓ all checks passed` or `✗ failed at step N: <reason>`.
