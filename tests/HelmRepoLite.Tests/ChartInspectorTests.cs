using Xunit;

namespace HelmRepoLite.Tests;

public class ChartInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public ChartInspectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmrepolite-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Inspect_extracts_name_and_version_from_Chart_yaml()
    {
        var path = TestChartBuilder.Build(_tempDir, "mychart", "1.2.3", appVersion: "1.0.0", description: "hello");

        var meta = ChartInspector.Inspect(path);

        Assert.Equal("mychart", meta.Name);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("v2", meta.ApiVersion);
        Assert.Equal("1.0.0", meta.AppVersion);
        Assert.Equal("hello", meta.Description);
        Assert.Equal("application", meta.Type);
        Assert.Equal("mychart-1.2.3.tgz", meta.FileName);
        Assert.Equal(64, meta.Digest.Length); // SHA-256 hex
    }

    [Fact]
    public void Inspect_throws_on_missing_file()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ChartInspector.Inspect(Path.Combine(_tempDir, "nope.tgz")));
    }
}
