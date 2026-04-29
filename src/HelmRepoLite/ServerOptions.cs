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

    /// <summary>Root directory containing .tgz chart packages and the generated index.yaml.
    /// Drop .tgz files directly into this folder to have them auto-indexed.</summary>
    public string StorageDir { get; init; } = "./charts";

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

    /// <summary>
    /// If true, <c>POST /shutdown</c> is mapped and a successful call gracefully stops the
    /// process. Off by default so the endpoint cannot be hit accidentally in production.
    /// Intended for CI pipelines that need to stop the server after their work is done.
    /// </summary>
    public bool EnableShutdown { get; init; }

    // ---- HTTPS ---------------------------------------------------------------

    /// <summary>
    /// HTTPS port. 0 disables HTTPS (default). Requires one of the cert options below.
    /// HTTP continues to serve on <see cref="Port"/> alongside HTTPS.
    /// </summary>
    public int HttpsPort { get; init; }

    /// <summary>Path to a PFX/PKCS#12 certificate file for HTTPS.</summary>
    public string HttpsCertFile { get; init; } = "";

    /// <summary>Password for the PFX certificate file (empty = no password).</summary>
    public string HttpsCertPassword { get; init; } = "";

    /// <summary>
    /// SHA-1 thumbprint of a certificate in the Windows certificate store.
    /// Searches CurrentUser\My then LocalMachine\My.
    /// </summary>
    public string HttpsCertThumbprint { get; init; } = "";

    /// <summary>
    /// Subject name (or CN) of a certificate in the Windows certificate store.
    /// Searches CurrentUser\My then LocalMachine\My.
    /// </summary>
    public string HttpsCertSubject { get; init; } = "";
}
