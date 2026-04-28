using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HelmRepoLite.Tests;

public class ChartStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempRoot;
    private readonly string _storage;
    private readonly string _drop;
    private ChartStore? _store;

    public ChartStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "helmrepolite-store-" + Guid.NewGuid().ToString("N"));
        _storage = Path.Combine(_tempRoot, "storage");
        _drop = Path.Combine(_tempRoot, "drop");
        Directory.CreateDirectory(_storage);
        Directory.CreateDirectory(_drop);
    }

    public async Task InitializeAsync()
    {
        _store = new ChartStore(
            new ServerOptions { StorageDir = _storage, DropDir = _drop },
            NullLogger<ChartStore>.Instance);
        await _store.InitializeAsync("http://localhost:8080", CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(_tempRoot, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Upload_persists_chart_and_updates_index()
    {
        var src = TestChartBuilder.Build(_tempRoot, "alpha", "0.1.0");
        await using var fs = File.OpenRead(src);

        var meta = await _store!.UploadAsync(fs, force: false, CancellationToken.None);

        Assert.Equal("alpha", meta.Name);
        Assert.True(File.Exists(Path.Combine(_storage, "alpha-0.1.0.tgz")));
        Assert.NotEmpty(_store.IndexBytes);
        var indexText = System.Text.Encoding.UTF8.GetString(_store.IndexBytes);
        Assert.Contains("alpha", indexText);
        Assert.Contains("0.1.0", indexText);
    }

    [Fact]
    public async Task Upload_duplicate_without_force_throws()
    {
        var src = TestChartBuilder.Build(_tempRoot, "beta", "1.0.0");
        await using (var fs = File.OpenRead(src)) await _store!.UploadAsync(fs, force: false, CancellationToken.None);

        var src2 = TestChartBuilder.Build(_tempRoot, "beta", "1.0.0", description: "second");
        await using var fs2 = File.OpenRead(src2);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store!.UploadAsync(fs2, force: false, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_file_and_entry()
    {
        var src = TestChartBuilder.Build(_tempRoot, "gamma", "0.2.0");
        await using (var fs = File.OpenRead(src)) await _store!.UploadAsync(fs, force: false, CancellationToken.None);

        var deleted = await _store!.DeleteAsync("gamma", "0.2.0", CancellationToken.None);

        Assert.True(deleted);
        Assert.False(File.Exists(Path.Combine(_storage, "gamma-0.2.0.tgz")));
        Assert.Null(_store.FindVersion("gamma", "0.2.0"));
    }

    [Fact]
    public async Task DropFolder_imports_dropped_chart()
    {
        var src = TestChartBuilder.Build(_tempRoot, "delta", "0.3.0");
        var dropTarget = Path.Combine(_drop, "delta-0.3.0.tgz");
        File.Move(src, dropTarget);

        // Drop watcher is async; poll briefly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_store!.FindVersion("delta", "0.3.0") is not null) break;
            await Task.Delay(100);
        }

        Assert.NotNull(_store!.FindVersion("delta", "0.3.0"));
        Assert.True(File.Exists(Path.Combine(_storage, "delta-0.3.0.tgz")));
        Assert.False(File.Exists(dropTarget)); // moved out of drop
    }
}
