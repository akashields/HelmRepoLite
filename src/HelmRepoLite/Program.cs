using System.Globalization;
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
    k.ListenAnyIP(options.Port); // host filter applied below
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

// GET /health -> simple liveness check
app.MapGet("/health", () => Results.Json(new { status = "ok" }));

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

// ---- Welcome page ----------------------------------------------------------

app.MapGet("/", () => Results.Content(WelcomePage(options, baseUrl), "text/html; charset=utf-8"));

app.Logger.LogInformation("HelmRepoLite listening on {Url}", baseUrl);
app.Logger.LogInformation("  Storage: {Dir}", Path.GetFullPath(options.StorageDir));
if (!string.IsNullOrEmpty(options.DropDir))
    app.Logger.LogInformation("  Drop folder: {Dir}", Path.GetFullPath(options.DropDir));
if (!string.IsNullOrEmpty(options.BasicAuthUser))
    app.Logger.LogInformation("  Basic auth: enabled (anonymous-get={Anon})", options.AnonymousGet);

await app.RunAsync().ConfigureAwait(false);
return 0;

// ---- helpers ---------------------------------------------------------------

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

static string WelcomePage(ServerOptions opts, string baseUrl) => $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>HelmRepoLite</title>
<style>
body { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; max-width: 720px; margin: 2rem auto; padding: 0 1rem; color: #222; }
h1 { font-weight: 600; }
code { background: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 3px; }
pre { background: #f0f0f0; padding: 0.8rem; border-radius: 4px; overflow-x: auto; }
a { color: #0a66c2; }
</style></head><body>
<h1>HelmRepoLite</h1>
<p>A lightweight, self-hosted Helm chart repository. Compatible with the ChartMuseum HTTP API.</p>
<p>Add this repo to Helm:</p>
<pre>helm repo add local {{baseUrl}}
helm repo update
helm search repo local</pre>
<p>Useful endpoints:</p>
<ul>
  <li><a href="/index.yaml">/index.yaml</a> &mdash; chart repository index</li>
  <li><a href="/api/charts">/api/charts</a> &mdash; JSON list of charts (ChartMuseum API)</li>
  <li><a href="/health">/health</a> &mdash; liveness probe</li>
</ul>
<p>Storage dir: <code>{{Path.GetFullPath(opts.StorageDir)}}</code></p>
{{(string.IsNullOrEmpty(opts.DropDir) ? "" : $"<p>Drop folder: <code>{Path.GetFullPath(opts.DropDir)}</code></p>")}}
</body></html>
""";
