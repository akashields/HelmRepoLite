# Running HelmRepoLite inside a Cake build

A common pattern: spin up a Helm repo at the start of a Cake build, publish charts to it during the build, run downstream tests against it, then tear it down. This guide shows one way to do that.

## Repo layout assumptions

```
<repo-root>/
├── build.cake
├── tools/                       <- the existing tools source dir
│   └── HelmRepoLite/            <- this project, vendored as a submodule or subtree
│       └── src/HelmRepoLite/HelmRepoLite.csproj
├── charts/                      <- chart sources
│   └── mychart/
└── build/                       <- build output (gitignored)
```

## Cake snippet

```csharp
#addin nuget:?package=Cake.FileHelpers&version=7.0.0

var configuration = Argument("configuration", "Release");
var helmRepoDir   = MakeAbsolute(Directory("./build/helm-repo"));
var helmDropDir   = MakeAbsolute(Directory("./build/helm-drop"));
var helmRepoUrl   = "http://localhost:8080";
IProcess helmRepoProcess = null;

Task("BuildHelmRepoLite")
    .Does(() =>
{
    DotNetBuild("./tools/HelmRepoLite/src/HelmRepoLite/HelmRepoLite.csproj",
        new DotNetBuildSettings { Configuration = configuration, NoRestore = false });
});

Task("StartHelmRepo")
    .IsDependentOn("BuildHelmRepoLite")
    .Does(() =>
{
    EnsureDirectoryExists(helmRepoDir);
    EnsureDirectoryExists(helmDropDir);

    var dll = $"./tools/HelmRepoLite/src/HelmRepoLite/bin/{configuration}/net10.0/HelmRepoLite.dll";
    helmRepoProcess = StartAndReturnProcess("dotnet",
        new ProcessSettings
        {
            Arguments = $"\"{dll}\" --port 8080 " +
                        $"--storage-dir \"{helmRepoDir}\" " +
                        $"--drop-dir \"{helmDropDir}\" " +
                        $"--chart-url {helmRepoUrl}",
            RedirectStandardOutput = false,
        });

    // Wait until /health responds.
    var deadline = DateTime.UtcNow.AddSeconds(15);
    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) })
    {
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = http.GetAsync($"{helmRepoUrl}/health").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) { Information("HelmRepoLite is up at {0}", helmRepoUrl); return; }
            }
            catch { /* not yet */ }
            System.Threading.Thread.Sleep(250);
        }
    }
    throw new Exception("HelmRepoLite did not become ready within 15s");
});

Task("PackageAndPublishCharts")
    .IsDependentOn("StartHelmRepo")
    .Does(() =>
{
    foreach (var chartDir in GetDirectories("./charts/*"))
    {
        // Two equivalent options:

        // Option A: package locally and drop the file into the watched folder.
        StartProcess("helm",
            new ProcessSettings { Arguments = $"package \"{chartDir}\" --destination \"{helmDropDir}\"" });

        // Option B: use the upload API (requires the helm-push plugin or curl).
        // StartProcess("helm", new ProcessSettings { Arguments = $"package \"{chartDir}\" --destination ./build" });
        // StartProcess("helm", new ProcessSettings { Arguments = $"cm-push ./build/{chartDir.GetDirectoryName()}-*.tgz {helmRepoUrl}" });
    }

    // Give the file watcher a moment, then tell helm to refresh.
    System.Threading.Thread.Sleep(750);
    StartProcess("helm", new ProcessSettings { Arguments = $"repo add ci {helmRepoUrl}" });
    StartProcess("helm", new ProcessSettings { Arguments = "repo update ci" });
});

Task("RunIntegrationTests")
    .IsDependentOn("PackageAndPublishCharts")
    .Does(() =>
{
    // ... whatever your tests do that need the live repo
});

Task("StopHelmRepo")
    .Does(() =>
{
    if (helmRepoProcess is not null)
    {
        helmRepoProcess.Kill();
        helmRepoProcess.WaitForExit();
        helmRepoProcess.Dispose();
        helmRepoProcess = null;
    }
});

Task("Default")
    .IsDependentOn("RunIntegrationTests")
    .Finally(() => RunTarget("StopHelmRepo"));

RunTarget(Argument("target", "Default"));
```

## Notes

- `--chart-url` is important in CI: without it, the `urls:` entries in `index.yaml` use the bind hostname, which may not be `localhost` in container-based agents. Set it explicitly to the URL Helm clients will see.
- The `Finally` block guarantees cleanup even if a test fails.
- For a pure-CI agent that already has .NET installed, you don't need `dotnet tool install` — building and `dotnet`-ing the DLL is faster and avoids touching the global tools cache.
- For developer machines, `dotnet tool install --global HelmRepoLite` and just running `helmrepolite` is more ergonomic.
