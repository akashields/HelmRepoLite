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

    [Fact]
    public void Parse_multiline_plain_scalar_does_not_break_subsequent_keys()
    {
        // authentik's Chart.yaml has a description that wraps onto the next line
        // with deeper indentation — a YAML multi-line plain scalar. Without handling
        // this, the mapping loop breaks early and 'name' is never reached.
        var yaml = """
            apiVersion: v2
            description: authentik is an open-source Identity Provider focused on flexibility
              and versatility
            name: authentik
            version: 2026.2.1
            """;

        var node = MiniYaml.Parse(yaml);

        Assert.Equal("authentik", MiniYaml.GetString(node, "name"));
        Assert.Equal("2026.2.1", MiniYaml.GetString(node, "version"));
        Assert.Equal("v2", MiniYaml.GetString(node, "apiVersion"));
    }

    [Fact]
    public void Parse_skips_block_scalars_and_reads_subsequent_keys()
    {
        // Real-world ArtifactHub charts have annotations with | block scalars.
        // If the parser doesn't skip the block content, fields after annotations
        // (including 'name') are never reached.
        var yaml = """
            apiVersion: v2
            annotations:
              artifacthub.io/changes: |
                - kind: fixed
                  description: Some fix
              artifacthub.io/images: |
                - name: authentik
                  image: ghcr.io/goauthentik/server:2026.2.1
            name: authentik
            version: 2026.2.1
            description: authentik is an open-source IdP
            """;

        var node = MiniYaml.Parse(yaml);

        Assert.Equal("authentik", MiniYaml.GetString(node, "name"));
        Assert.Equal("2026.2.1", MiniYaml.GetString(node, "version"));
        Assert.Equal("v2", MiniYaml.GetString(node, "apiVersion"));
        Assert.Equal("authentik is an open-source IdP", MiniYaml.GetString(node, "description"));
    }

    [Fact]
    public void Parse_compact_sequence_notation_at_same_indent_as_key()
    {
        // YAML compact notation: sequence at same indent as parent key.
        // This is what MiniYamlWriter emits for index.yaml entries.
        var yaml = """
            entries:
              mychart:
              - name: mychart
                version: 0.1.0
            """;

        var node = MiniYaml.Parse(yaml);

        var entries = MiniYaml.GetMappingList(node, "entries");
        Assert.Null(entries); // entries is a mapping, not a list
        if (node is System.Collections.Generic.Dictionary<string, object?> root &&
            root["entries"] is System.Collections.Generic.Dictionary<string, object?> entriesMap &&
            entriesMap["mychart"] is System.Collections.Generic.List<object?> versions)
        {
            Assert.Single(versions);
            var first = versions[0] as System.Collections.Generic.Dictionary<string, object?>;
            Assert.NotNull(first);
            Assert.Equal("mychart", first!["name"]);
        }
        else
        {
            Assert.Fail("entries/mychart was not parsed as a list of mappings");
        }
    }
}
