using Xunit;

namespace HelmRepoLite.Tests;

public class MiniYamlTests
{
    private static readonly string[] KeywordFixture = ["foo", "bar"];
    [Fact]
    public void Parse_simple_chart_yaml()
    {
        var yaml = """
            apiVersion: v2
            name: mychart
            version: 1.2.3
            description: A Helm chart for Kubernetes
            type: application
            appVersion: "1.0.0"
            keywords:
              - foo
              - bar
            maintainers:
              - name: Ada
                email: ada@example.com
            """;

        var node = MiniYaml.Parse(yaml);

        Assert.Equal("v2", MiniYaml.GetString(node, "apiVersion"));
        Assert.Equal("mychart", MiniYaml.GetString(node, "name"));
        Assert.Equal("1.2.3", MiniYaml.GetString(node, "version"));
        Assert.Equal("1.0.0", MiniYaml.GetString(node, "appVersion"));

        var keywords = MiniYaml.GetStringList(node, "keywords");
        Assert.NotNull(keywords);
        Assert.Equal(KeywordFixture, keywords);

        var maintainers = MiniYaml.GetMappingList(node, "maintainers");
        Assert.NotNull(maintainers);
        Assert.Single(maintainers!);
        Assert.Equal("Ada", MiniYaml.GetString(maintainers![0], "name"));
        Assert.Equal("ada@example.com", MiniYaml.GetString(maintainers[0], "email"));
    }

    [Fact]
    public void Writer_emits_quoted_versions_to_avoid_float_coercion()
    {
        var doc = new Dictionary<string, object?>
        {
            ["version"] = "1.0",
            ["name"] = "mychart",
        };

        var output = MiniYamlWriter.Write(doc);

        Assert.Contains("version: \"1.0\"", output);
        Assert.Contains("name: mychart", output);
    }

    [Fact]
    public void Writer_handles_nested_lists_of_mappings()
    {
        var doc = new Dictionary<string, object?>
        {
            ["entries"] = new Dictionary<string, object?>
            {
                ["mychart"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "mychart",
                        ["version"] = "0.1.0",
                        ["urls"] = new List<object?> { "http://example.com/mychart-0.1.0.tgz" }
                    }
                }
            }
        };

        var output = MiniYamlWriter.Write(doc);

        // Round trip and verify
        var parsed = MiniYaml.Parse(output);
        var entries = ((Dictionary<string, object?>)parsed!)["entries"] as Dictionary<string, object?>;
        Assert.NotNull(entries);
        var versions = entries!["mychart"] as List<object?>;
        Assert.NotNull(versions);
        Assert.Single(versions!);
        var first = versions![0] as Dictionary<string, object?>;
        Assert.NotNull(first);
        Assert.Equal("mychart", first!["name"]);
        Assert.Equal("0.1.0", first["version"]);
    }
}
