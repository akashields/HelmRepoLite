using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace HelmRepoLite;

/// <summary>
/// The core domain service. Holds the in-memory chart index and owns the storage
/// directory. Any .tgz file added, replaced, or deleted in the storage directory
/// is automatically detected and the index updated.
///
/// Thread-safety: a single <c>SemaphoreSlim</c> serializes all mutations and
/// index rebuilds. Reads of the rendered index.yaml bytes are lock-free via
/// a volatile reference swap.
/// </summary>
public sealed class ChartStore : IDisposable
{
    private readonly ServerOptions _options;
    private readonly ILogger<ChartStore> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<string, ChartMetadata> _byFile = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _storageWatcher;
    private FileSystemWatcher? _indexWatcher;
    private volatile byte[] _indexBytes = [];
    private volatile string _baseUrl = "http://localhost:8080";

    public ChartStore(ServerOptions options, ILogger<ChartStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>The path where .tgz files live and where index.yaml is written.</summary>
    public string StorageDir => _options.StorageDir;

    /// <summary>Cached, fully serialized index.yaml as UTF-8 bytes.</summary>
    public byte[] IndexBytes => _indexBytes;

    /// <summary>True after the initial storage scan completes. Used by the readiness health check.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Snapshot of the current in-memory index; copy returned to caller.</summary>
    public IReadOnlyList<ChartMetadata> Snapshot()
    {
        _mutex.Wait();
        try { return _byFile.Values.ToList(); }
        finally { _mutex.Release(); }
    }

    /// <summary>Initial scan of storage dir + start watcher. Call once at startup.</summary>
    public async Task InitializeAsync(string baseUrl, CancellationToken ct)
    {
        _baseUrl = baseUrl;
        Directory.CreateDirectory(_options.StorageDir);

        _logger.LogInformation("Storage directory : {Dir}", Path.GetFullPath(_options.StorageDir));

        // Full rescan: picks up any adds, changes, or deletes that happened while offline.
        await ScanStorageAsync(ct).ConfigureAwait(false);

        IsReady = true;

        // Watch storage for live changes: copy/move/replace/delete a .tgz and the index
        // updates automatically without a server restart.
        _storageWatcher = new FileSystemWatcher(_options.StorageDir, "*.tgz")
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _storageWatcher.Created += (_, e) => _ = ImportFromStorageAsync(e.FullPath);
        _storageWatcher.Changed += (_, e) => _ = ImportFromStorageAsync(e.FullPath);
        _storageWatcher.Renamed += (_, e) => _ = ImportFromStorageAsync(e.FullPath);
        _storageWatcher.Deleted += (_, e) => _ = HandleStorageDeleteAsync(e.FullPath);
        _logger.LogInformation("Watching storage directory for changes");

        // Watch for index.yaml deletion and regenerate it automatically.
        _indexWatcher = new FileSystemWatcher(_options.StorageDir, "index.yaml")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName,
        };
        _indexWatcher.Deleted += (_, _) =>
        {
            _logger.LogInformation("index.yaml deleted — regenerating");
            _mutex.Wait();
            try { RebuildIndexLocked(); }
            finally { _mutex.Release(); }
        };
    }

    private async Task ScanStorageAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _byFile.Clear();
            int errors = 0;
            var files = Directory.EnumerateFiles(_options.StorageDir, "*.tgz").ToList();
            _logger.LogInformation("Scanning storage: {Count} .tgz file(s) found", files.Count);
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var meta = ChartInspector.Inspect(f);
                    _byFile[meta.FileName] = meta;
                    _logger.LogInformation("  [ok] {File} → {Name} {Version}", Path.GetFileName(f), meta.Name, meta.Version);
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning("  [skip] {File} — could not be identified: {Message}", Path.GetFileName(f), ex.Message);
                }
            }
            RebuildIndexLocked();
            if (errors > 0)
                _logger.LogWarning("Startup scan complete: {Count} packages indexed, {Errors} file(s) could not be identified", _byFile.Count, errors);
            else
                _logger.LogInformation("Startup scan complete: {Count} packages indexed", _byFile.Count);
        }
        finally { _mutex.Release(); }
    }

    private async Task ImportFromStorageAsync(string fullPath)
    {
        // Windows FileSystemWatcher fires multiple events per file operation (Created + Changed).
        // Use an in-flight set to coalesce concurrent calls for the same path.
        if (!_inFlight.TryAdd(fullPath, 0)) return;
        try
        {
            await WaitForStableFileAsync(fullPath).ConfigureAwait(false);
            if (!File.Exists(fullPath)) return;

            var fileName = Path.GetFileName(fullPath);
            _logger.LogInformation("Storage change detected: {File} — re-indexing", fileName);
            ChartMetadata meta;
            try
            {
                meta = ChartInspector.Inspect(fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("  [skip] {File} — could not be identified: {Message}", fileName, ex.Message);
                return;
            }

            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                _byFile[fileName] = meta;
                RebuildIndexLocked();
                _logger.LogInformation("Re-indexed {File} from storage", fileName);
            }
            finally { _mutex.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storage import skipped {Path}", fullPath);
        }
        finally
        {
            _inFlight.TryRemove(fullPath, out _);
        }
    }

    private async Task HandleStorageDeleteAsync(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        _logger.LogInformation("Storage deletion detected: {File}", fileName);
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_byFile.Remove(fileName))
            {
                RebuildIndexLocked();
                _logger.LogInformation("Removed {File} from index (deleted from storage)", fileName);
            }
        }
        finally { _mutex.Release(); }
    }

    /// <summary>HTTP upload path: validate, persist, re-index. Returns the resulting metadata.</summary>
    public async Task<ChartMetadata> UploadAsync(Stream content, bool force, CancellationToken ct)
    {
        // Stream to a temp file first so ChartInspector can read it back.
        var tempPath = Path.Combine(_options.StorageDir, $".upload-{Guid.NewGuid():N}.tgz");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            var meta = ChartInspector.Inspect(tempPath);
            var fileName = $"{meta.Name}-{meta.Version}.tgz";
            var dest = Path.Combine(_options.StorageDir, fileName);

            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var exists = _byFile.ContainsKey(fileName);
                if (exists && !force && !_options.AllowOverwrite)
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException($"chart {fileName} already exists; pass ?force=true or run with --allow-overwrite");
                }

                File.Move(tempPath, dest, overwrite: true);
                meta = meta with { FileName = fileName, Created = DateTimeOffset.UtcNow };
                _byFile[fileName] = meta;
                RebuildIndexLocked();
                _logger.LogInformation("Uploaded {File}", fileName);
                return meta;
            }
            finally { _mutex.Release(); }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Persist a provenance file alongside its chart.</summary>
    public async Task UploadProvenanceAsync(string chartFileName, Stream content, CancellationToken ct)
    {
        if (!chartFileName.EndsWith(".tgz", StringComparison.Ordinal))
            throw new ArgumentException("provenance must reference a .tgz chart", nameof(chartFileName));

        var dest = Path.Combine(_options.StorageDir, chartFileName + ".prov");
        await using var fs = File.Create(dest);
        await content.CopyToAsync(fs, ct).ConfigureAwait(false);
        _logger.LogInformation("Stored provenance {File}", Path.GetFileName(dest));
    }

    /// <summary>Delete a chart version (and its .prov if present).</summary>
    public async Task<bool> DeleteAsync(string name, string version, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fileName = $"{name}-{version}.tgz";
            var fullPath = Path.Combine(_options.StorageDir, fileName);
            if (!_byFile.Remove(fileName)) return false;

            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* swallowed */ }
            try
            {
                var prov = fullPath + ".prov";
                if (File.Exists(prov)) File.Delete(prov);
            }
            catch { /* swallowed */ }

            RebuildIndexLocked();
            _logger.LogInformation("Deleted {File}", fileName);
            return true;
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Lookup helpers for the /api/charts routes.</summary>
    public IReadOnlyList<ChartMetadata> ListVersions(string name)
    {
        _mutex.Wait();
        try
        {
            return _byFile.Values
                .Where(c => string.Equals(c.Name, name, StringComparison.Ordinal))
                .OrderByDescending(c => c.Created)
                .ToList();
        }
        finally { _mutex.Release(); }
    }

    public ChartMetadata? FindVersion(string name, string version)
    {
        _mutex.Wait();
        try
        {
            var fileName = $"{name}-{version}.tgz";
            return _byFile.TryGetValue(fileName, out var m) ? m : null;
        }
        finally { _mutex.Release(); }
    }

    // Path.GetFullPath is required because Results.File rejects relative paths.
    public string GetTgzPath(string fileName) => Path.GetFullPath(Path.Combine(_options.StorageDir, fileName));

    /// <summary>Force a rebuild from scratch (e.g. after manual edits).</summary>
    public Task ResyncAsync(CancellationToken ct) => ScanStorageAsync(ct);

    // ---- internals -----------------------------------------------------------

    private void RebuildIndexLocked()
    {
        var index = IndexBuilder.Build(_byFile.Values, _baseUrl);
        var yaml = MiniYamlWriter.Write(index);
        _indexBytes = System.Text.Encoding.UTF8.GetBytes(yaml);

        // Persist index.yaml on disk too, for tools that scrape the directory directly.
        var indexPath = Path.Combine(_options.StorageDir, "index.yaml");
        try { File.WriteAllBytes(indexPath, _indexBytes); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not write index.yaml to disk"); }
    }

    private static async Task WaitForStableFileAsync(string path, int timeoutMs = 5000)
    {
        long lastSize = -1;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!File.Exists(path)) { await Task.Delay(50).ConfigureAwait(false); continue; }
                var size = new FileInfo(path).Length;
                if (size > 0 && size == lastSize) return;
                lastSize = size;
            }
            catch (IOException)
            {
                // file still being written
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _storageWatcher?.Dispose();
        _indexWatcher?.Dispose();
        _mutex.Dispose();
    }
}
