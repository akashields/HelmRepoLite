using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace HelmRepoLite.Tests;

/// <summary>
/// Helper that builds a Helm-shaped .tgz on disk: top-level directory named after
/// the chart, containing a Chart.yaml with the supplied fields. Returns the path.
/// </summary>
internal static class TestChartBuilder
{
    public static string Build(string dir, string name, string version, string? appVersion = null, string? description = null)
    {
        Directory.CreateDirectory(dir);
        var tgzPath = Path.Combine(dir, $"{name}-{version}.tgz");

        using (var fs = File.Create(tgzPath))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
        {
            // Top-level directory entry
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{name}/"));

            // Chart.yaml
            var sb = new StringBuilder();
            sb.AppendLine("apiVersion: v2");
            sb.AppendLine($"name: {name}");
            sb.AppendLine($"version: {version}");
            sb.AppendLine("type: application");
            if (description is not null) sb.AppendLine($"description: {description}");
            if (appVersion is not null) sb.AppendLine($"appVersion: \"{appVersion}\"");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());

            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{name}/Chart.yaml")
            {
                DataStream = new MemoryStream(bytes),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            };
            writer.WriteEntry(entry);

            // values.yaml (empty but present)
            var values = new PaxTarEntry(TarEntryType.RegularFile, $"{name}/values.yaml")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("# default values\n")),
            };
            writer.WriteEntry(values);
        }

        return tgzPath;
    }
}
