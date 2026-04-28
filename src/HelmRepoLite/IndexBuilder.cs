using System.Globalization;

namespace HelmRepoLite;

/// <summary>
/// Builds an in-memory representation of index.yaml that MiniYamlWriter can serialize.
///
/// Layout per Helm spec:
///   apiVersion: v1
///   generated: <RFC3339 timestamp>
///   entries:
///     <chart-name>:
///       - apiVersion: ...
///         name: ...
///         version: ...
///         appVersion: ...
///         created: ...
///         digest: ...
///         description: ...
///         urls:
///           - <baseUrl>/<file>.tgz
///         (etc)
/// </summary>
public static class IndexBuilder
{
    public static Dictionary<string, object?> Build(IEnumerable<ChartMetadata> charts, string baseUrl)
    {
        var entries = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Group by name; within each name, order versions newest-first by Created
        // then by version string descending (good-enough lexicographic SemVer for now).
        var byName = charts.GroupBy(c => c.Name, StringComparer.Ordinal);

        foreach (var group in byName.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var versions = group
                .OrderByDescending(c => c.Created)
                .ThenByDescending(c => c.Version, StringComparer.Ordinal)
                .Select(c => (object?)BuildEntry(c, baseUrl))
                .ToList();
            entries[group.Key] = versions;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apiVersion"] = "v1",
            ["generated"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
            ["entries"] = entries,
        };
    }

    private static Dictionary<string, object?> BuildEntry(ChartMetadata c, string baseUrl)
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(c.ApiVersion)) entry["apiVersion"] = c.ApiVersion;
        entry["name"] = c.Name;
        entry["version"] = c.Version;
        if (!string.IsNullOrEmpty(c.AppVersion)) entry["appVersion"] = c.AppVersion;
        if (!string.IsNullOrEmpty(c.Type)) entry["type"] = c.Type;
        if (!string.IsNullOrEmpty(c.Description)) entry["description"] = c.Description;
        if (!string.IsNullOrEmpty(c.KubeVersion)) entry["kubeVersion"] = c.KubeVersion;
        if (!string.IsNullOrEmpty(c.Home)) entry["home"] = c.Home;
        if (!string.IsNullOrEmpty(c.Icon)) entry["icon"] = c.Icon;
        if (c.Deprecated == true) entry["deprecated"] = true;
        if (c.Sources is { Count: > 0 }) entry["sources"] = c.Sources.Cast<object?>().ToList();
        if (c.Keywords is { Count: > 0 }) entry["keywords"] = c.Keywords.Cast<object?>().ToList();

        if (c.Maintainers is { Count: > 0 })
        {
            entry["maintainers"] = c.Maintainers.Select(m =>
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(m.Name)) d["name"] = m.Name;
                if (!string.IsNullOrEmpty(m.Email)) d["email"] = m.Email;
                if (!string.IsNullOrEmpty(m.Url)) d["url"] = m.Url;
                return (object?)d;
            }).ToList();
        }

        if (c.Dependencies is { Count: > 0 })
        {
            entry["dependencies"] = c.Dependencies.Select(d =>
            {
                var x = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(d.Name)) x["name"] = d.Name;
                if (!string.IsNullOrEmpty(d.Version)) x["version"] = d.Version;
                if (!string.IsNullOrEmpty(d.Repository)) x["repository"] = d.Repository;
                if (!string.IsNullOrEmpty(d.Condition)) x["condition"] = d.Condition;
                return (object?)x;
            }).ToList();
        }

        entry["created"] = c.Created.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        entry["digest"] = c.Digest;
        entry["urls"] = new List<object?> { $"{baseUrl.TrimEnd('/')}/charts/{c.FileName}" };

        return entry;
    }
}
