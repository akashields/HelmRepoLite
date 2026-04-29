<#
.SYNOPSIS
    Build, test, and publish HelmRepoLite.

.PARAMETER Version
    Semantic version to stamp on all outputs. Defaults to 1.0.0.

.PARAMETER Targets
    One or more build targets. Defaults to tool, win-x64, and linux-x64.

    tool        — NuGet global tool package (.nupkg)
    win-x64     — Self-contained Windows x64 executable
    win-arm64   — Self-contained Windows ARM64 executable
    linux-x64   — Self-contained Linux x64 executable
    linux-arm64 — Self-contained Linux ARM64 executable
    osx-x64     — Self-contained macOS x64 executable
    osx-arm64   — Self-contained macOS ARM64 executable (Apple Silicon)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 1.2.3
    .\build.ps1 -Version 1.2.3 -Targets tool, win-x64
    .\build.ps1 -Targets win-x64, linux-x64, linux-arm64
    .\build.ps1 -Targets tool, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64

.OUTPUTS
    artifacts\
      nuget\          HelmRepoLite.<version>.nupkg  (dotnet global tool)
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

    [ValidateSet("tool", "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string[]]$Targets = @("tool", "win-x64", "linux-x64")
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

# ---- Self-contained single-file executables ---------------------------------
# PublishTrimmed removes unused BCL code (tree-shaking).
# SuppressTrimAnalysisWarnings silences analyzer warnings for anonymous-type
# JSON serialization which works correctly at runtime despite the warnings.
$StandaloneTargets = $Targets | Where-Object { $_ -ne "tool" }

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

# ---- Summary ----------------------------------------------------------------
Write-Host "`nBuild complete." -ForegroundColor Green
Write-Host ""
if ($Targets -contains "tool") {
    Write-Host "  Global tool package : $Artifacts\nuget\HelmRepoLite.$Version.nupkg"
}
foreach ($Rid in $StandaloneTargets) {
    $Exe = if ($Rid.StartsWith("win")) { "helmrepolite.exe" } else { "helmrepolite" }
    Write-Host "  Standalone ($Rid) : $Artifacts\standalone\$Rid\$Exe"
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

