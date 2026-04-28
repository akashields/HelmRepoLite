namespace HelmRepoLite;

/// <summary>
/// Runtime configuration for the server. Populated from CLI args and/or env vars.
/// Field names mirror ChartMuseum where reasonable to keep operator muscle memory.
/// </summary>
public sealed record ServerOptions
{
    /// <summary>TCP port to listen on. Default 8080 to match ChartMuseum.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Hostname/IP to bind. Default 0.0.0.0 (all interfaces).</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>Root directory containing .tgz chart packages and the generated index.yaml.</summary>
    public string StorageDir { get; init; } = "./charts";

    /// <summary>
    /// Optional drop folder. Files placed here are auto-imported into StorageDir
    /// (atomic rename), then the index is rebuilt. Empty string disables.
    /// </summary>
    public string DropDir { get; init; } = "";

    /// <summary>Absolute base URL used for chart download URLs in index.yaml. Auto-detected if empty.</summary>
    public string ChartUrl { get; init; } = "";

    /// <summary>Username for HTTP Basic auth. Empty disables auth entirely.</summary>
    public string BasicAuthUser { get; init; } = "";

    /// <summary>Password for HTTP Basic auth.</summary>
    public string BasicAuthPass { get; init; } = "";

    /// <summary>If true, GET routes (index.yaml, /charts/*) are unauthenticated even when basic auth is set.</summary>
    public bool AnonymousGet { get; init; } = true;

    /// <summary>If true, POST /api/charts may overwrite an existing version without ?force=true.</summary>
    public bool AllowOverwrite { get; init; }

    /// <summary>If true, DELETE /api/charts/{name}/{version} returns 405.</summary>
    public bool DisableDelete { get; init; }

    /// <summary>If true, all /api routes return 404.</summary>
    public bool DisableApi { get; init; }

    /// <summary>If true, emit verbose request/operation logs.</summary>
    public bool Debug { get; init; }
}
