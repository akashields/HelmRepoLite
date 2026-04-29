<#
.SYNOPSIS
    Build, test, and publish HelmRepoLite.

.PARAMETER Version
    Semantic version to stamp on all outputs. Defaults to 1.0.0.

.PARAMETER Targets
    One or more build targets. Defaults to tool, helm-chart, win-x64, and linux-x64.

    tool        — NuGet global tool package (.nupkg)
    helm-chart  — Helm chart package (.tgz) — requires helm CLI on PATH
    win-x64     — Self-contained Windows x64 executable
    win-arm64   — Self-contained Windows ARM64 executable
    linux-x64   — Self-contained Linux x64 executable
    linux-arm64 — Self-contained Linux ARM64 executable
    osx-x64     — Self-contained macOS x64 executable
    osx-arm64   — Self-contained macOS ARM64 executable (Apple Silicon)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 1.2.3
    .\build.ps1 -Version 1.2.3 -Targets tool, helm-chart, win-x64
    .\build.ps1 -Targets linux-x64                                    # binary only
    .\build.ps1 -Targets tool, helm-chart, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64

.OUTPUTS
    artifacts\
      nuget\          HelmRepoLite.<version>.nupkg  (dotnet global tool)
      helm\           helmrepolite-<version>.tgz    (Helm chart)
      docker\         Dockerfile                    (portable container build — copy to WSL and run docker-build.sh)
                      docker-build.sh
                      helmrepolite
      standalone\
        win-x64\      helmrepolite.exe              (self-contained, no .NET required)
        win-arm64\    helmrepolite.exe
        linux-x64\    helmrepolite
        linux-arm64\  helmrepolite
        osx-x64\      helmrepolite
        osx-arm64\    helmrepolite
#>
#Requires -Version 7.0
param(
    [string]$Version = "1.0.0",

    [ValidateSet("tool", "helm-chart", "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string[]]$Targets = @("tool", "helm-chart", "win-x64", "linux-x64")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root        = $PSScriptRoot
$SrcProject  = Join-Path $Root "src\HelmRepoLite\HelmRepoLite.csproj"
$TestProject = Join-Path $Root "tests\HelmRepoLite.Tests\HelmRepoLite.Tests.csproj"
$Artifacts   = Join-Path $Root "artifacts"

function Write-Step([string]$Label) {
    Write-Host "`n--- $Label ---" -ForegroundColor Cyan
}

function Assert-Success([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$Step failed (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

Write-Host "HelmRepoLite v$Version  [targets: $($Targets -join ', ')]" -ForegroundColor Green

# Clean previous artifacts
if (Test-Path $Artifacts) {
    Remove-Item $Artifacts -Recurse -Force
}

# ---- Tests ------------------------------------------------------------------
Write-Step "Tests"
dotnet test $TestProject --configuration Release
Assert-Success "Tests"

# ---- NuGet global tool package ----------------------------------------------
if ($Targets -contains "tool") {
    Write-Step ".NET global tool package"
    dotnet pack $SrcProject `
        --configuration Release `
        -p:Version=$Version `
        --output (Join-Path $Artifacts "nuget")
    Assert-Success "dotnet pack"
}

# ---- Helm chart package -----------------------------------------------------
if ($Targets -contains "helm-chart") {
    Write-Step "Helm chart package"
    if (-not (Get-Command helm -ErrorAction SilentlyContinue)) {
        Write-Error "helm CLI not found on PATH. Install Helm 3 (https://helm.sh/docs/intro/install/) and re-run."
        exit 1
    }

    # Run helm-docs to regenerate README.md from annotated values.yaml before packaging.
    # Install it automatically via 'go install' if it is not already on PATH.
    if (-not (Get-Command helm-docs -ErrorAction SilentlyContinue)) {
        Write-Host "  helm-docs not found — installing via 'go install'..." -ForegroundColor Yellow
        if (Get-Command go -ErrorAction SilentlyContinue) {
            go install github.com/norwoodj/helm-docs/cmd/helm-docs@latest
            Assert-Success "go install helm-docs"
        } else {
            Write-Warning "  'go' not found on PATH; skipping helm-docs. README.md in the chart may be stale."
            Write-Warning "  Install helm-docs manually: https://github.com/norwoodj/helm-docs/releases"
        }
    }
    if (Get-Command helm-docs -ErrorAction SilentlyContinue) {
        Write-Host "  Generating chart README.md via helm-docs..." -ForegroundColor DarkGray
        Push-Location $Root
        helm-docs --chart-search-root helm --log-level warning
        $helmDocsExit = $LASTEXITCODE
        Pop-Location
        if ($helmDocsExit -ne 0) { Write-Error "helm-docs failed (exit $helmDocsExit)"; exit $helmDocsExit }
    }

    $HelmOut = Join-Path $Artifacts "helm"
    New-Item -ItemType Directory -Path $HelmOut -Force | Out-Null
    helm package (Join-Path $Root "helm\helmrepolite") `
        --version $Version `
        --app-version $Version `
        --destination $HelmOut
    Assert-Success "helm package"
}

# ---- Self-contained single-file executables ---------------------------------
# PublishTrimmed removes unused BCL code (tree-shaking).
# SuppressTrimAnalysisWarnings silences analyzer warnings for anonymous-type
# JSON serialization which works correctly at runtime despite the warnings.
$StandaloneTargets = $Targets | Where-Object { $_ -notin @("tool", "helm-chart") }

foreach ($Rid in $StandaloneTargets) {
    Write-Step "Publish standalone: $Rid"
    dotnet publish $SrcProject `
        --configuration Release `
        --runtime $Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=true `
        -p:SuppressTrimAnalysisWarnings=true `
        -p:Version=$Version `
        --output (Join-Path $Artifacts "standalone\$Rid")
    Assert-Success "Publish $Rid"
}

# ---- Docker artifacts -------------------------------------------------------
# When linux-x64 is built, bundle the binary, a self-contained Dockerfile, and
# the build script into artifacts/docker/ so the container build is fully
# portable — distribute that directory and run docker-build.sh from WSL.
if ($StandaloneTargets -contains "linux-x64") {
    Write-Step "Docker artifacts"
    $DockerOut = Join-Path $Artifacts "docker"
    New-Item -ItemType Directory -Path $DockerOut -Force | Out-Null

    # Binary
    Copy-Item (Join-Path $Artifacts "standalone\linux-x64\helmrepolite") $DockerOut

    # Build script — copy with LF line endings so it runs in WSL/Linux even when
    # git core.autocrlf=true has checked out the source file with CRLF.
    $ShSrc = Get-Content -Raw (Join-Path $Root "docker\docker-build.sh")
    $ShSrc = $ShSrc -replace "`r`n", "`n" -replace "`r", "`n"
    [System.IO.File]::WriteAllText((Join-Path $DockerOut "docker-build.sh"), $ShSrc)

    # Self-contained Dockerfile: binary sits alongside it so COPY helmrepolite just works
    Set-Content -Path (Join-Path $DockerOut "Dockerfile") -Encoding UTF8 -Value @'
# HelmRepoLite - Linux container image
# Generated by build.ps1. Run ./docker-build.sh to build and push.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

ARG UID=1001
ARG GID=1001

WORKDIR /app

COPY helmrepolite ./helmrepolite
RUN chmod +x ./helmrepolite

RUN groupadd --gid ${GID} helmrepo \
 && useradd  --uid ${UID} --gid ${GID} --no-create-home helmrepo

RUN mkdir -p /charts && chown helmrepo:helmrepo /charts

USER helmrepo

EXPOSE 8080
VOLUME ["/charts"]

ENTRYPOINT ["./helmrepolite"]
CMD ["--storage-dir=/charts", "--port=8080", "--host=0.0.0.0"]
'@
}

# ---- Summary ----------------------------------------------------------------
Write-Host "`nBuild complete." -ForegroundColor Green
Write-Host ""
if ($Targets -contains "tool") {
    Write-Host "  Global tool package : $Artifacts\nuget\HelmRepoLite.$Version.nupkg"
}
if ($Targets -contains "helm-chart") {
    Write-Host "  Helm chart          : $Artifacts\helm\helmrepolite-$Version.tgz"
}
foreach ($Rid in $StandaloneTargets) {
    $Exe = if ($Rid.StartsWith("win")) { "helmrepolite.exe" } else { "helmrepolite" }
    Write-Host "  Standalone ($Rid) : $Artifacts\standalone\$Rid\$Exe"
}
if ($StandaloneTargets -contains "linux-x64") {
    Write-Host "  Docker artifacts    : $Artifacts\docker\"
    Write-Host "    (copy to WSL and run: chmod +x docker-build.sh && ./docker-build.sh <registry> <user> <pass> $Version)"
}
Write-Host ""
if ($Targets -contains "tool") {
    Write-Host "Install the global tool from local package:" -ForegroundColor Yellow
    Write-Host "  dotnet tool install -g HelmRepoLite --add-source `"$(Join-Path $Artifacts 'nuget')`""
    Write-Host ""
}
if ($StandaloneTargets) {
    Write-Host "Run the standalone exe directly (no .NET required):" -ForegroundColor Yellow
    $FirstRid = $StandaloneTargets | Select-Object -First 1
    $FirstExe = if ($FirstRid.StartsWith("win")) { "helmrepolite.exe" } else { "helmrepolite" }
    Write-Host "  $Artifacts\standalone\$FirstRid\$FirstExe --storage-dir=./charts"
}

