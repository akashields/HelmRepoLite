using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HelmRepoLite;

/// <summary>
/// Subset of Helm Chart.yaml fields we surface in index.yaml.
/// We keep the original string representations to avoid lossy coercion.
/// </summary>
public sealed record ChartMetadata
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? ApiVersion { get; init; }
    public string? AppVersion { get; init; }
    public string? Description { get; init; }
    public string? Type { get; init; }
    public string? KubeVersion { get; init; }
    public string? Home { get; init; }
    public string? Icon { get; init; }
    public bool? Deprecated { get; init; }
    public List<string>? Sources { get; init; }
    public List<string>? Keywords { get; init; }
    public List<Maintainer>? Maintainers { get; init; }
    public List<Dependency>? Dependencies { get; init; }
    public List<string>? Annotations { get; init; }

    /// <summary>SHA-256 digest of the .tgz file (hex, lowercase).</summary>
    public required string Digest { get; init; }

    /// <summary>The .tgz filename relative to the storage root (e.g. "mychart-0.1.2.tgz").</summary>
    public required string FileName { get; init; }

    /// <summary>UTC timestamp when this entry was created (file mtime or upload time).</summary>
    public required DateTimeOffset Created { get; init; }

    public sealed record Maintainer(string? Name, string? Email, string? Url);
    public sealed record Dependency(string? Name, string? Version, string? Repository, string? Condition);
}

public static class ChartInspector
{
    /// <summary>
    /// Opens a .tgz, locates Chart.yaml at the top level (typically <name>/Chart.yaml),
    /// parses it, computes the SHA-256 digest, and returns a ChartMetadata record.
    /// </summary>
    /// <param name="tgzPath">Absolute path to the .tgz file.</param>
    public static ChartMetadata Inspect(string tgzPath)
    {
        if (!File.Exists(tgzPath))
            throw new FileNotFoundException("chart package not found", tgzPath);

        // Compute digest while streaming once; then re-open to read tar entries.
        // (Reading twice is simpler than juggling a single stream for both jobs.)
        var digest = ComputeSha256(tgzPath);

        var chartYamlText = ReadChartYamlFromTgz(tgzPath)
            ?? throw new InvalidDataException($"Chart.yaml not found inside {Path.GetFileName(tgzPath)}");

        var parsed = MiniYaml.Parse(chartYamlText);
        var name = MiniYaml.GetString(parsed, "name")
            ?? throw new InvalidDataException(
                $"Chart.yaml missing required 'name'. Raw content:\n{chartYamlText}");
        var version = MiniYaml.GetString(parsed, "version")
            ?? throw new InvalidDataException(
                $"Chart.yaml missing required 'version'. Raw content:\n{chartYamlText}");

        // Validate filename matches name+version per Helm convention.
        var expected = $"{name}-{version}.tgz";
        var actual = Path.GetFileName(tgzPath);
        // Helm allows the file to be named anything technically, but every tool
        // expects this convention. We warn rather than throw - let caller decide.

        var maintainers = MiniYaml.GetMappingList(parsed, "maintainers")?
            .Select(m => new ChartMetadata.Maintainer(
                MiniYaml.GetString(m, "name"),
                MiniYaml.GetString(m, "email"),
                MiniYaml.GetString(m, "url")))
            .ToList();

        var dependencies = MiniYaml.GetMappingList(parsed, "dependencies")?
            .Select(d => new ChartMetadata.Dependency(
                MiniYaml.GetString(d, "name"),
                MiniYaml.GetString(d, "version"),
                MiniYaml.GetString(d, "repository"),
                MiniYaml.GetString(d, "condition")))
            .ToList();

        return new ChartMetadata
        {
            Name = name,
            Version = version,
            ApiVersion = MiniYaml.GetString(parsed, "apiVersion"),
            AppVersion = MiniYaml.GetString(parsed, "appVersion"),
            Description = MiniYaml.GetString(parsed, "description"),
            Type = MiniYaml.GetString(parsed, "type"),
            KubeVersion = MiniYaml.GetString(parsed, "kubeVersion"),
            Home = MiniYaml.GetString(parsed, "home"),
            Icon = MiniYaml.GetString(parsed, "icon"),
            Deprecated = MiniYaml.GetString(parsed, "deprecated") is "true" ? true : null,
            Sources = MiniYaml.GetStringList(parsed, "sources"),
            Keywords = MiniYaml.GetStringList(parsed, "keywords"),
            Maintainers = maintainers,
            Dependencies = dependencies,
            Digest = digest,
            FileName = actual,
            Created = new DateTimeOffset(File.GetLastWriteTimeUtc(tgzPath), TimeSpan.Zero),
        };
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Reads Chart.yaml out of a gzip+tar archive without external libs.
    /// Looks for the first entry whose path ends with "/Chart.yaml" (or is exactly "Chart.yaml").
    /// </summary>
    private static string? ReadChartYamlFromTgz(string tgzPath)
    {
        using var fs = File.OpenRead(tgzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        // .NET 10 ships System.Formats.Tar in the BCL.
        using var tar = new System.Formats.Tar.TarReader(gz);

        System.Formats.Tar.TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            var name = entry.Name?.Replace('\\', '/');
            if (string.IsNullOrEmpty(name)) continue;
            if (entry.EntryType is System.Formats.Tar.TarEntryType.Directory) continue;

            // Match "<chartdir>/Chart.yaml" only - not nested Chart.yaml under charts/<dep>/Chart.yaml
            // The convention is that the top-level directory is the chart name.
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Equals("Chart.yaml", StringComparison.Ordinal))
            {
                if (entry.DataStream is null) continue;
                using var sr = new StreamReader(entry.DataStream, Encoding.UTF8);
                return sr.ReadToEnd();
            }
        }
        return null;
    }
}
