using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HelmRepoLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ---- Parse CLI -------------------------------------------------------------

var (options, exitCode, message) = CliParser.Parse(args);
if (message is not null) Console.WriteLine(message);
if (exitCode is int ec) return ec;

// ---- Validate HTTPS config early so errors surface before the host starts --

X509Certificate2? httpsCert = null;
if (options.HttpsPort > 0)
{
    httpsCert = LoadHttpsCertificate(options);
    if (httpsCert is null)
    {
        Console.Error.WriteLine(
            "Error: --https-port requires a certificate. " +
            "Provide --https-cert-file, --https-cert-thumbprint, or --https-cert-subject.");
        return 1;
    }
}

// ---- Build host ------------------------------------------------------------

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(options.Debug ? LogLevel.Debug : LogLevel.Information);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ChartStore>();

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(options.Port);
    if (httpsCert is not null)
        k.ListenAnyIP(options.HttpsPort, o => o.UseHttps(httpsCert));
    // Allow large chart uploads. Helm charts are usually <1 MB but charts with bundled images can be larger.
    k.Limits.MaxRequestBodySize = 256 * 1024 * 1024;
});

var app = builder.Build();

// Bind only to the requested host if not 0.0.0.0
if (!string.Equals(options.Host, "0.0.0.0", StringComparison.Ordinal))
{
    // Kestrel was already told to listen on any IP; if a specific host is desired,
    // use Url filtering. For simplicity we just log a notice.
    app.Logger.LogInformation("Binding host {Host} requested; Kestrel listens on all interfaces. Use a firewall or a reverse proxy to restrict.", options.Host);
}

// ---- Initialise store ------------------------------------------------------

var baseUrl = string.IsNullOrEmpty(options.ChartUrl)
    ? $"http://{(options.Host == "0.0.0.0" ? "localhost" : options.Host)}:{options.Port}"
    : options.ChartUrl.TrimEnd('/');

var store = app.Services.GetRequiredService<ChartStore>();
await store.InitializeAsync(baseUrl, app.Lifetime.ApplicationStopping).ConfigureAwait(false);

// ---- Middleware ------------------------------------------------------------

app.UseMiddleware<BasicAuthMiddleware>();

// ---- Helm repository routes (read) -----------------------------------------

// GET /index.yaml -> the canonical Helm repo index
app.MapGet("/index.yaml", (ChartStore s) =>
    Results.File(s.IndexBytes, "application/x-yaml"));

// GET /charts/{file}.tgz -> the chart package
app.MapGet("/charts/{fileName}", (string fileName, ChartStore s) =>
{
    if (!IsSafeName(fileName)) return Results.NotFound();
    var path = s.GetTgzPath(fileName);
    if (!File.Exists(path)) return Results.NotFound();
    var contentType = fileName.EndsWith(".prov", StringComparison.Ordinal)
        ? "text/plain"
        : "application/gzip";
    return Results.File(path, contentType, fileName);
});

// GET /charts/{name}/{version}/readme -> rendered HTML README page for browser viewing
app.MapGet("/charts/{name}/{version}/readme", (string name, string version, ChartStore s) =>
{
    if (!IsSafeName(name) || !IsSafeName(version)) return Results.NotFound();
    var meta = s.FindVersion(name, version);
    if (meta is null) return Results.NotFound();
    var markdown = ChartInspector.ReadReadme(s.GetTgzPath(meta.FileName)) ?? GenerateFallbackReadme(meta);
    return Results.Content(ReadmePage(meta, markdown), "text/html; charset=utf-8");
});

// GET /health -> simple liveness check
app.MapGet("/health", () => Results.Json(new { status = "ok" }));

// GET /server/info -> ChartMuseum compatibility probe used by helm-push and dashboard tools
app.MapGet("/server/info", () => Results.Json(new
{
    version = $"helmrepolite-{typeof(ServerOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}",
    storage = "local",
}));

// ---- ChartMuseum-compatible /api routes (read+write) -----------------------

if (!options.DisableApi)
{
    var api = app.MapGroup("/api");

    // GET /api/charts -> map of chartName -> array of versions
    api.MapGet("/charts", (ChartStore s) =>
    {
        var map = s.Snapshot()
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Created).Select(ToApiEntry).ToList(), StringComparer.Ordinal);
        return Results.Json(map);
    });

    // GET /api/charts/{name} -> array of versions for that chart
    api.MapGet("/charts/{name}", (string name, ChartStore s) =>
    {
        var versions = s.ListVersions(name);
        if (versions.Count == 0) return Results.NotFound(new { error = "chart not found" });
        return Results.Json(versions.Select(ToApiEntry).ToList());
    });

    // GET /api/charts/{name}/{version} -> a specific version
    api.MapGet("/charts/{name}/{version}", (string name, string version, ChartStore s) =>
    {
        var meta = s.FindVersion(name, version);
        return meta is null
            ? Results.NotFound(new { error = "chart version not found" })
            : Results.Json(ToApiEntry(meta));
    });

    // GET /api/charts/{name}/{version}/readme -> README.md from the chart package, or a generated fallback
    api.MapGet("/charts/{name}/{version}/readme", (string name, string version, ChartStore s) =>
    {
        var meta = s.FindVersion(name, version);
        if (meta is null) return Results.NotFound();
        var readme = ChartInspector.ReadReadme(s.GetTgzPath(meta.FileName)) ?? GenerateFallbackReadme(meta);
        return Results.Content(readme, "text/markdown; charset=utf-8");
    });

    // POST /api/charts -> upload a new chart (raw body OR multipart with field "chart")
    api.MapPost("/charts", async (HttpRequest req, ChartStore s, CancellationToken ct) =>
    {
        var force = string.Equals(req.Query["force"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (req.HasFormContentType)
            {
                var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
                ChartMetadata? lastChart = null;
                var chartFile = form.Files["chart"];
                var provFile = form.Files["prov"];

                if (chartFile is not null)
                {
                    await using var s1 = chartFile.OpenReadStream();
                    lastChart = await s.UploadAsync(s1, force, ct).ConfigureAwait(false);
                }
                if (provFile is not null && lastChart is not null)
                {
                    await using var s2 = provFile.OpenReadStream();
                    await s.UploadProvenanceAsync(lastChart.FileName, s2, ct).ConfigureAwait(false);
                }
                if (chartFile is null && provFile is null)
                    return Results.BadRequest(new { error = "no 'chart' or 'prov' field in form" });

                return Results.Json(new { saved = true });
            }
            else
            {
                await s.UploadAsync(req.Body, force, ct).ConfigureAwait(false);
                return Results.Json(new { saved = true });
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    // POST /api/prov -> upload a provenance file (raw body)
    api.MapPost("/prov", async (HttpRequest req, ChartStore s, CancellationToken ct) =>
    {
        var fileName = req.Query["file"].ToString();
        if (string.IsNullOrEmpty(fileName) || !IsSafeName(fileName) || !fileName.EndsWith(".tgz", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "missing or invalid ?file=<chart>.tgz query parameter" });
        await s.UploadProvenanceAsync(fileName, req.Body, ct).ConfigureAwait(false);
        return Results.Json(new { saved = true });
    });

    // DELETE /api/charts/{name}/{version}
    if (!options.DisableDelete)
    {
        api.MapDelete("/charts/{name}/{version}", async (string name, string version, ChartStore s, CancellationToken ct) =>
        {
            var ok = await s.DeleteAsync(name, version, ct).ConfigureAwait(false);
            return ok
                ? Results.Json(new { deleted = true })
                : Results.NotFound(new { error = "chart version not found" });
        });
    }
    else
    {
        api.MapDelete("/charts/{name}/{version}", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

    // POST /api/resync -> force a full storage rescan (handy after manual edits)
    api.MapPost("/resync", async (ChartStore s, CancellationToken ct) =>
    {
        await s.ResyncAsync(ct).ConfigureAwait(false);
        return Results.Json(new { resynced = true });
    });
}

// ---- Shutdown endpoint (opt-in, for CI pipelines) -------------------------

if (options.EnableShutdown)
{
    app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
    {
        app.Logger.LogInformation("Shutdown requested via POST /shutdown — stopping");
        // Schedule StopApplication after the response is sent.
        _ = Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            lifetime.StopApplication();
        });
        return Results.Ok(new { shuttingDown = true });
    });
}

// ---- Welcome page ----------------------------------------------------------

app.MapGet("/", (ChartStore s) => Results.Content(WelcomePage(options, baseUrl, s.Snapshot()), "text/html; charset=utf-8"));

app.Logger.LogInformation("HelmRepoLite listening on {Url}", baseUrl);
app.Logger.LogInformation("  Storage: {Dir}", Path.GetFullPath(options.StorageDir));
if (httpsCert is not null)
    app.Logger.LogInformation("  HTTPS:   port {Port} · subject: {Subject}", options.HttpsPort, httpsCert.Subject);
if (!string.IsNullOrEmpty(options.BasicAuthUser))
    app.Logger.LogInformation("  Basic auth: enabled (anonymous-get={Anon})", options.AnonymousGet);
if (options.EnableShutdown)
    app.Logger.LogInformation("  Shutdown endpoint: enabled (POST /shutdown)");

await app.RunAsync().ConfigureAwait(false);
return 0;

// ---- helpers ---------------------------------------------------------------

static X509Certificate2? LoadHttpsCertificate(ServerOptions opts)
{
    if (!string.IsNullOrEmpty(opts.HttpsCertFile))
    {
        if (!File.Exists(opts.HttpsCertFile))
            throw new FileNotFoundException($"HTTPS certificate file not found: {opts.HttpsCertFile}");
        return string.IsNullOrEmpty(opts.HttpsCertPassword)
            ? X509CertificateLoader.LoadCertificateFromFile(opts.HttpsCertFile)
            : X509CertificateLoader.LoadPkcs12FromFile(opts.HttpsCertFile, opts.HttpsCertPassword);
    }
    if (!string.IsNullOrEmpty(opts.HttpsCertThumbprint))
        return FindCertInStore(X509FindType.FindByThumbprint, opts.HttpsCertThumbprint);
    if (!string.IsNullOrEmpty(opts.HttpsCertSubject))
        return FindCertInStore(X509FindType.FindBySubjectName, opts.HttpsCertSubject);
    return null;
}

static X509Certificate2? FindCertInStore(X509FindType findType, string value)
{
    // Search CurrentUser first (dev certs live here), then LocalMachine (server certs).
    foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        var results = store.Certificates.Find(findType, value, validOnly: false);
        if (results.Count > 0) return results[0];
    }
    return null;
}

static bool IsSafeName(string name)
{
    if (string.IsNullOrEmpty(name)) return false;
    foreach (var c in name)
    {
        if (c is '/' or '\\' or '\0') return false;
    }
    return name is not ".." and not ".";
}

static Dictionary<string, object?> ToApiEntry(ChartMetadata c)
{
    var d = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["name"] = c.Name,
        ["version"] = c.Version,
        ["digest"] = c.Digest,
        ["created"] = c.Created.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        ["urls"] = new[] { $"charts/{c.FileName}" },
    };
    if (!string.IsNullOrEmpty(c.ApiVersion)) d["apiVersion"] = c.ApiVersion;
    if (!string.IsNullOrEmpty(c.AppVersion)) d["appVersion"] = c.AppVersion;
    if (!string.IsNullOrEmpty(c.Type)) d["type"] = c.Type;
    if (!string.IsNullOrEmpty(c.Description)) d["description"] = c.Description;
    return d;
}

static string ReadmePage(ChartMetadata meta, string markdown)
{
    var title = System.Net.WebUtility.HtmlEncode($"{meta.Name} {meta.Version}");
    var body = MarkdownRenderer.ToHtml(markdown);
    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>{{title}} — README</title>
  <style>
    *, *::before, *::after { box-sizing: border-box; }
    body { font-family: ui-sans-serif, system-ui, sans-serif; margin: 1.5rem 2rem; color: #222; line-height: 1.6; }
    nav { font-family: ui-monospace, monospace; font-size: 0.85em; margin-bottom: 1.5rem; }
    nav a { color: #0a66c2; text-decoration: none; }
    nav a:hover { text-decoration: underline; }
    h1,h2,h3,h4,h5,h6 { margin-top: 1.5rem; margin-bottom: 0.4rem; line-height: 1.3; }
    h1 { font-size: 1.8rem; border-bottom: 2px solid #eee; padding-bottom: 0.3rem; }
    h2 { font-size: 1.3rem; border-bottom: 1px solid #eee; padding-bottom: 0.2rem; }
    p { margin: 0.7rem 0; }
    a { color: #0a66c2; }
    code { font-family: ui-monospace, monospace; background: #f0f0f0; padding: 0.1em 0.3em; border-radius: 3px; font-size: 0.88em; }
    pre { background: #f6f8fa; border: 1px solid #e0e0e0; border-radius: 6px; padding: 1rem; overflow-x: auto; }
    pre code { background: none; padding: 0; font-size: 0.85em; }
    table { border-collapse: collapse; width: 100%; margin: 1rem 0; font-size: 0.9em; table-layout: fixed; }
    th { background: #f6f8fa; text-align: left; padding: 0.45rem 0.7rem; border: 1px solid #ddd; font-weight: 600; overflow-wrap: break-word; }
    td { padding: 0.4rem 0.7rem; border: 1px solid #ddd; vertical-align: top; overflow-wrap: break-word; word-break: break-word; }
    th:nth-child(1), td:nth-child(1) { width: 24%; }
    th:nth-child(2), td:nth-child(2) { width: 7%; }
    th:nth-child(3), td:nth-child(3) { width: 20%; }
    tr:nth-child(even) td { background: #fafafa; }
    ul,ol { padding-left: 1.5rem; margin: 0.5rem 0; }
    li { margin: 0.2rem 0; }
    hr { border: none; border-top: 1px solid #ddd; margin: 1.5rem 0; }
    img { max-width: 100%; height: auto; }
  </style>
</head>
<body>
  <nav><a href="/">← HelmRepoLite</a></nav>
  {{body}}
</body>
</html>
""";
}

static string GenerateFallbackReadme(ChartMetadata meta)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("# ").AppendLine(meta.Name);
    sb.AppendLine();
    if (!string.IsNullOrEmpty(meta.Description))
    {
        sb.AppendLine(meta.Description);
        sb.AppendLine();
    }
    if (!string.IsNullOrEmpty(meta.AppVersion))
    {
        sb.Append("**App version:** ").AppendLine(meta.AppVersion);
        sb.AppendLine();
    }
    sb.AppendLine("## Installation");
    sb.AppendLine();
    sb.AppendLine("```bash");
    sb.Append("helm install ").Append(meta.Name).Append(" <repo>/").Append(meta.Name)
      .Append(" --version ").AppendLine(meta.Version);
    sb.AppendLine("```");
    sb.AppendLine();
    sb.AppendLine("## Upgrade");
    sb.AppendLine();
    sb.AppendLine("```bash");
    sb.Append("helm upgrade ").Append(meta.Name).Append(" <repo>/").Append(meta.Name)
      .Append(" --version ").AppendLine(meta.Version);
    sb.AppendLine("```");
    sb.AppendLine();
    sb.AppendLine("---");
    sb.AppendLine("*No README.md was found in this chart package.*");
    return sb.ToString();
}

static string WelcomePage(ServerOptions opts, string baseUrl, IReadOnlyList<ChartMetadata> charts)
{    var byName = charts
        .GroupBy(c => c.Name, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal);

    var sb = new System.Text.StringBuilder();
    foreach (var group in byName)
    {
        sb.Append("<details open><summary><strong>");
        sb.Append(System.Net.WebUtility.HtmlEncode(group.Key));
        sb.Append("</strong> <span class=\"count\">(");
        sb.Append(group.Count());
        sb.Append(group.Count() == 1 ? " version" : " versions");
        sb.Append(")</span></summary><table><thead><tr><th>Version</th><th>App Version</th><th>Description</th><th>Created</th><th>Download</th><th>Readme</th><th></th></tr></thead><tbody>");
        foreach (var c in group.OrderByDescending(c => c.Created))
        {
            sb.Append("<tr><td><code>");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Version));
            sb.Append("</code></td><td>");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.AppVersion ?? ""));
            sb.Append("</td><td>");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Description ?? ""));
            sb.Append("</td><td>");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            sb.Append("</td><td><a href=\"charts/");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.FileName));
            sb.Append("\">⬇ ");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.FileName));
            sb.Append("</a></td><td><a href=\"charts/");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Name));
            sb.Append('/');
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Version));
            sb.Append("/readme\">README</a></td><td>");
            sb.Append("<button class=\"del\" data-name=\"");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Name));
            sb.Append("\" data-version=\"");
            sb.Append(System.Net.WebUtility.HtmlEncode(c.Version));
            sb.Append("\">🗑 Delete</button>");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></details>");
    }

    var chartsHtml = charts.Count == 0
        ? "<p class=\"empty\">No charts indexed yet. Copy a <code>.tgz</code> into the storage directory or upload via the API.</p>"
        : sb.ToString();

    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta http-equiv="refresh" content="30">
  <title>HelmRepoLite</title>
  <style>
    *, *::before, *::after { box-sizing: border-box; }
    body { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; max-width: 960px; margin: 2rem auto; padding: 0 1rem; color: #222; }
    h1 { font-weight: 700; margin-bottom: 0.25rem; }
    .subtitle { color: #555; margin-top: 0; margin-bottom: 1.5rem; }
    h2 { font-size: 1rem; font-weight: 600; border-bottom: 1px solid #ddd; padding-bottom: 0.3rem; margin-top: 2rem; }
    code { background: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 3px; font-size: 0.9em; }
    pre { background: #f0f0f0; padding: 0.8rem; border-radius: 4px; overflow-x: auto; }
    a { color: #0a66c2; text-decoration: none; }
    a:hover { text-decoration: underline; }
    nav { display: flex; gap: 1rem; margin-bottom: 1.5rem; flex-wrap: wrap; }
    nav a { background: #f0f0f0; padding: 0.3rem 0.7rem; border-radius: 4px; font-size: 0.9em; }
    nav a:hover { background: #e0e0e0; text-decoration: none; }
    .meta { font-size: 0.85em; color: #555; margin-bottom: 1.5rem; }
    details { margin-bottom: 1rem; border: 1px solid #ddd; border-radius: 6px; }
    summary { padding: 0.6rem 0.8rem; cursor: pointer; user-select: none; background: #fafafa; border-radius: 6px; }
    summary::-webkit-details-marker { color: #888; }
    details[open] summary { border-bottom: 1px solid #ddd; border-radius: 6px 6px 0 0; }
    .count { font-weight: normal; color: #666; }
    table { width: 100%; border-collapse: collapse; font-size: 0.9em; }
    th { text-align: left; padding: 0.4rem 0.7rem; color: #555; font-weight: 600; border-bottom: 1px solid #eee; }
    td { padding: 0.4rem 0.7rem; border-bottom: 1px solid #f4f4f4; vertical-align: top; }
    tr:last-child td { border-bottom: none; }
    .empty { color: #888; font-style: italic; }
    .refresh-note { font-size: 0.8em; color: #aaa; float: right; line-height: 1; }
    button.del { font-size: 0.8em; padding: 0.2rem 0.5rem; border: 1px solid #e0a0a0; border-radius: 3px; background: #fff4f4; color: #c00; cursor: pointer; white-space: nowrap; }
    button.del:hover { background: #ffe0e0; }
  </style>
</head>
<body>
  <h1>HelmRepoLite</h1>
  <p class="subtitle">A lightweight, self-hosted Helm chart repository.</p>

  <nav>
    <a href="/index.yaml">/index.yaml</a>
    <a href="/api/charts">/api/charts</a>
    <a href="/health">/health</a>
    {{(opts.DisableApi ? "" : "<a href=\"#\" onclick=\"resyncCharts(event)\">↻ Resync</a>")}}
    {{(opts.EnableShutdown ? "<a href=\"#\" onclick=\"shutdownServer(event)\" style=\"background:#fff4f4;color:#c00;border:1px solid #e0a0a0\">⏻ Shutdown</a>" : "")}}
  </nav>

  <div class="meta">
    Storage: <code>{{Path.GetFullPath(opts.StorageDir)}}</code> &nbsp;&middot;&nbsp;
    <strong>{{charts.Count}}</strong> package{{(charts.Count == 1 ? "" : "s")}} indexed
    <span class="refresh-note">page auto-refreshes every 30 s &middot; filesystem changes are detected instantly</span>
  </div>

  <h2>Packages</h2>
  {{chartsHtml}}

  <h2>Storage</h2>
  <p>Storage directory: <code>{{System.Net.WebUtility.HtmlEncode(Path.GetFullPath(opts.StorageDir))}}</code><br>
  Deleting a <code>.tgz</code> from that directory removes it from the index automatically.</p>

  <p><strong>Method 1 — Copy file directly (no tools required)</strong></p>
  <pre>helm package ./mychart
copy mychart-0.1.0.tgz {{System.Net.WebUtility.HtmlEncode(Path.GetFullPath(opts.StorageDir))}}\</pre>

  <p><strong>Method 2 — HTTP upload (no plugin required)</strong></p>
  <pre># curl
curl --data-binary "@mychart-0.1.0.tgz" {{baseUrl}}/api/charts

# PowerShell
Invoke-RestMethod -Method POST -Uri {{baseUrl}}/api/charts `
    -InFile mychart-0.1.0.tgz -ContentType "application/octet-stream"</pre>

  <p><strong>Method 3 — helm-push plugin</strong></p>
  <pre># Install the plugin once
helm plugin install https://github.com/chartmuseum/helm-push

# Push
helm cm-push mychart-0.1.0.tgz local</pre>

  <h2>Add to Helm</h2>
  <pre>helm repo add local {{baseUrl}}
helm repo update
helm search repo local</pre>
<script>
  document.addEventListener('click', function(e) {
    var btn = e.target.closest('button.del');
    if (!btn) return;
    var name = btn.dataset.name, version = btn.dataset.version;
    if (!confirm('Delete ' + name + ' ' + version + '?\n\nThis removes the .tgz file from storage and updates the index.')) return;
    btn.disabled = true;
    fetch('/api/charts/' + encodeURIComponent(name) + '/' + encodeURIComponent(version), { method: 'DELETE' })
      .then(function(r) {
        if (r.ok) { location.reload(); }
        else { r.json().then(function(j) { alert('Delete failed: ' + (j.error || r.status)); btn.disabled = false; }); }
      })
      .catch(function(err) { alert('Delete failed: ' + err); btn.disabled = false; });
  });
  function resyncCharts(e) {
    e.preventDefault();
    var btn = e.currentTarget;
    btn.textContent = '↻ Resyncing…';
    btn.style.pointerEvents = 'none';
    fetch('/api/resync', { method: 'POST' })
      .then(function(r) {
        if (r.ok) { location.reload(); }
        else { btn.textContent = '↻ Resync'; btn.style.pointerEvents = ''; alert('Resync failed: ' + r.status); }
      })
      .catch(function(err) { btn.textContent = '↻ Resync'; btn.style.pointerEvents = ''; alert('Resync failed: ' + err); });
  }
  function shutdownServer(e) {
    e.preventDefault();
    if (!confirm('Shut down the HelmRepoLite server?\n\nThe process will exit.')) return;
    fetch('/shutdown', { method: 'POST' })
      .then(function() { document.body.innerHTML = '<p style="font-family:monospace;margin:2rem">Server is shutting down.</p>'; })
      .catch(function(err) { alert('Shutdown failed: ' + err); });
  }
</script>
</body>
</html>
""";
}
