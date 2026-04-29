using System.Globalization;
using System.Text;

namespace HelmRepoLite;

/// <summary>
/// A purpose-built YAML reader for the subset of YAML that appears in Helm Chart.yaml
/// files: scalars (quoted, unquoted, multi-line block scalars), mappings, sequences.
/// We deliberately do NOT pull in YamlDotNet to keep the "zero third-party deps" rule.
///
/// What we support (sufficient for Chart.yaml per spec):
///   - block mappings with 2-space indentation and arbitrary alternate indents
///   - block sequences (- item)
///   - flow scalars: plain, single-quoted, double-quoted (with \n, \t, \", \\)
///   - integers, floats, booleans, null - returned as their string form so downstream
///     code controls coercion (matters for YAML 1.1 quirks like "yes"/"no")
///   - comments (# to end of line outside quotes)
///
/// What we DO NOT support: anchors/aliases, tags, flow-style { } / [ ], complex keys,
/// folded/literal block scalars (>, |). Helm's Chart.yaml does not need them.
/// If a chart somehow uses one, ChartInspector falls back to "minimal metadata".
/// </summary>
internal static class MiniYaml
{
    /// <summary>Parses a YAML document. Returns the root node (Dictionary, List, or string).</summary>
    public static object? Parse(string text)
    {
        var lines = SplitLines(text);
        int i = 0;
        return ParseNode(lines, ref i, indent: -1);
    }

    private static List<string> SplitLines(string text)
    {
        // Normalize and strip trailing whitespace; keep blank lines so indent tracking works.
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>(raw.Length);
        foreach (var line in raw)
        {
            // Strip trailing comments only when not inside a quoted scalar - cheap heuristic
            // since we re-parse the value when we get to it.
            result.Add(line);
        }
        return result;
    }

    private static int MeasureIndent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    private static bool IsBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private static object? ParseNode(List<string> lines, ref int i, int indent)
    {
        // Skip blanks
        while (i < lines.Count && IsBlank(lines[i])) i++;
        if (i >= lines.Count) return null;

        var first = lines[i];
        var firstIndent = MeasureIndent(first);
        if (firstIndent <= indent) return null;

        var trimmed = first[firstIndent..];

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
        {
            return ParseSequence(lines, ref i, firstIndent);
        }
        return ParseMapping(lines, ref i, firstIndent);
    }

    private static List<object?> ParseSequence(List<string> lines, ref int i, int indent)
    {
        var list = new List<object?>();
        while (i < lines.Count)
        {
            if (IsBlank(lines[i])) { i++; continue; }
            var ind = MeasureIndent(lines[i]);
            if (ind != indent) break;
            var content = lines[i][indent..];
            if (!content.StartsWith('-')) break;

            // Strip "- "
            var rest = content.Length > 1 ? content[1..].TrimStart() : "";

            if (rest.Length == 0)
            {
                // Nested block on next line
                i++;
                list.Add(ParseNode(lines, ref i, indent));
                continue;
            }

            // Could be inline scalar OR the start of an inline mapping ("- key: value")
            var colonIdx = FindMappingColon(rest);
            if (colonIdx >= 0)
            {
                // Treat the "- key: value" line as the start of a mapping at indent+2 (or wherever rest sits)
                // We rewrite by splicing: keep the current line as a mapping start
                lines[i] = new string(' ', indent + 2) + rest;
                var nested = ParseMapping(lines, ref i, indent + 2);
                list.Add(nested);
            }
            else
            {
                list.Add(ParseScalar(rest));
                i++;
            }
        }
        return list;
    }

    private static Dictionary<string, object?> ParseMapping(List<string> lines, ref int i, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (i < lines.Count)
        {
            if (IsBlank(lines[i])) { i++; continue; }
            var ind = MeasureIndent(lines[i]);
            if (ind != indent) break;
            var content = lines[i][indent..];
            if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-") break;

            var colonIdx = FindMappingColon(content);
            if (colonIdx < 0) break; // not a mapping line

            var key = StripQuotes(content[..colonIdx].Trim());
            var after = content[(colonIdx + 1)..].TrimStart();

            // Strip inline comment (only outside of quotes)
            after = StripInlineComment(after);

            if (after.Length == 0)
            {
                i++;
                // YAML compact notation: a block sequence may begin at the *same*
                // indentation as the mapping key that owns it, e.g.:
                //   mychart:
                //   - name: mychart   ← same indent as key, not deeper
                // Detect this case before falling into the strict-deeper ParseNode.
                var ni = i;
                while (ni < lines.Count && IsBlank(lines[ni])) ni++;
                if (ni < lines.Count)
                {
                    var nextInd = MeasureIndent(lines[ni]);
                    var nextContent = lines[ni][nextInd..];
                    if (nextInd == indent && (nextContent.StartsWith("- ", StringComparison.Ordinal) || nextContent == "-"))
                    {
                        map[key] = ParseSequence(lines, ref i, indent);
                        continue;
                    }
                }
                var child = ParseNode(lines, ref i, indent);
                map[key] = child;
            }
            else if (IsBlockScalarIndicator(after))
            {
                // Literal (|) or folded (>) block scalar: the content lines are at a greater
                // indentation. Consume them so the outer mapping continues normally.
                // We don't need the actual text for index.yaml metadata purposes.
                i++;
                SkipBlockScalarLines(lines, ref i, indent);
                map[key] = null;
            }
            else
            {
                map[key] = ParseScalar(after);
                i++;
                // Skip multi-line plain scalar continuation lines. In YAML, a plain scalar
                // can flow onto subsequent lines that are indented more than the mapping:
                //   description: some long text that wraps
                //     onto the next line          ← indent > mapping indent, not a new key
                // Without skipping these, the mapping loop sees indent mismatch and breaks
                // early, losing all subsequent keys (including 'name').
                while (i < lines.Count && !IsBlank(lines[i]) && MeasureIndent(lines[i]) > indent)
                    i++;
            }
        }
        return map;
    }

    /// <summary>
    /// Returns true if <paramref name="s"/> is a YAML block scalar indicator:
    /// <c>|</c> or <c>&gt;</c> optionally followed by chomping (<c>-</c>/<c>+</c>)
    /// and/or an explicit indentation digit.
    /// </summary>
    private static bool IsBlockScalarIndicator(string s)
    {
        if (s.Length == 0 || (s[0] != '|' && s[0] != '>')) return false;
        for (int k = 1; k < s.Length; k++)
        {
            var c = s[k];
            if (c is '-' or '+' or >= '1' and <= '9') continue;
            return false; // unexpected character — not a block scalar
        }
        return true;
    }

    /// <summary>
    /// Skips all lines that belong to a block scalar's content: blank lines
    /// interspersed within it, and any lines indented more than
    /// <paramref name="mappingIndent"/> (the indent of the enclosing mapping keys).
    /// </summary>
    private static void SkipBlockScalarLines(List<string> lines, ref int i, int mappingIndent)
    {
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line)) { i++; continue; }
            if (MeasureIndent(line) > mappingIndent) { i++; continue; }
            break;
        }
    }

    /// <summary>Find a top-level ": " (or trailing ":") that separates a mapping key from value, ignoring quotes.</summary>
    private static int FindMappingColon(string s)
    {
        bool inSingle = false, inDouble = false;
        for (int k = 0; k < s.Length; k++)
        {
            var c = s[k];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (!inSingle && !inDouble && c == ':')
            {
                if (k + 1 == s.Length || s[k + 1] == ' ' || s[k + 1] == '\t') return k;
            }
        }
        return -1;
    }

    private static string StripInlineComment(string s)
    {
        bool inSingle = false, inDouble = false;
        for (int k = 0; k < s.Length; k++)
        {
            var c = s[k];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (!inSingle && !inDouble && c == '#' && (k == 0 || s[k - 1] == ' ' || s[k - 1] == '\t'))
            {
                return s[..k].TrimEnd();
            }
        }
        return s;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2)
        {
            if ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
                return UnescapeQuoted(s);
        }
        return s;
    }

    private static string UnescapeQuoted(string s)
    {
        if (s[0] == '\'')
        {
            // YAML single-quote: only '' is an escape for a single quote
            return s[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        var inner = s[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (int k = 0; k < inner.Length; k++)
        {
            var c = inner[k];
            if (c == '\\' && k + 1 < inner.Length)
            {
                var n = inner[++k];
                sb.Append(n switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    _ => n
                });
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static object? ParseScalar(string raw)
    {
        var v = raw.TrimEnd();
        if (v.Length == 0) return null;

        // Quoted - strip and unescape, return as string regardless of contents
        if ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))
            return UnescapeQuoted(v);

        // null markers
        if (v == "~" || v.Equals("null", StringComparison.Ordinal) || v.Equals("Null", StringComparison.Ordinal) || v.Equals("NULL", StringComparison.Ordinal))
            return null;

        // We deliberately return raw strings for booleans/numbers. The downstream code
        // (ChartInspector) treats Chart.yaml fields as strings or known typed lists,
        // so coercion isn't needed and we sidestep YAML 1.1 boolean traps.
        return v;
    }

    /// <summary>
    /// Convenience: get a string field from a mapping node, or null.
    /// </summary>
    public static string? GetString(object? node, string key)
    {
        if (node is Dictionary<string, object?> m && m.TryGetValue(key, out var v) && v is string s)
            return s;
        return null;
    }

    /// <summary>Convenience: get a sequence-of-strings from a mapping field.</summary>
    public static List<string>? GetStringList(object? node, string key)
    {
        if (node is Dictionary<string, object?> m && m.TryGetValue(key, out var v) && v is List<object?> lst)
            return lst.OfType<string>().ToList();
        return null;
    }

    /// <summary>Convenience: get a sequence-of-mappings from a mapping field.</summary>
    public static List<Dictionary<string, object?>>? GetMappingList(object? node, string key)
    {
        if (node is Dictionary<string, object?> m && m.TryGetValue(key, out var v) && v is List<object?> lst)
            return lst.OfType<Dictionary<string, object?>>().ToList();
        return null;
    }
}

/// <summary>
/// A purpose-built YAML emitter for index.yaml. We control the schema so we can
/// produce clean, deterministic output without a generic library.
/// </summary>
internal static class MiniYamlWriter
{
    public static string Write(object? root)
    {
        var sb = new StringBuilder();
        WriteNode(sb, root, indent: 0, isListItem: false);
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, object? node, int indent, bool isListItem)
    {
        switch (node)
        {
            case null:
                sb.Append("null\n");
                break;
            case Dictionary<string, object?> map:
                WriteMapping(sb, map, indent, isListItem);
                break;
            case List<object?> list:
                WriteList(sb, list, indent);
                break;
            case string s:
                sb.Append(FormatScalar(s)).Append('\n');
                break;
            case bool b:
                sb.Append(b ? "true" : "false").Append('\n');
                break;
            default:
                sb.Append(FormatScalar(Convert.ToString(node, CultureInfo.InvariantCulture) ?? "")).Append('\n');
                break;
        }
    }

    private static void WriteMapping(StringBuilder sb, Dictionary<string, object?> map, int indent, bool inlinedFromList)
    {
        bool first = true;
        var pad = new string(' ', indent);
        foreach (var (key, value) in map)
        {
            if (!first || !inlinedFromList) sb.Append(pad);
            first = false;
            sb.Append(EscapeKey(key)).Append(':');

            switch (value)
            {
                case null:
                    sb.Append(' ').Append("null").Append('\n');
                    break;
                case Dictionary<string, object?> child when child.Count == 0:
                    sb.Append(' ').Append("{}\n");
                    break;
                case List<object?> child when child.Count == 0:
                    sb.Append(' ').Append("[]\n");
                    break;
                case Dictionary<string, object?> child:
                    sb.Append('\n');
                    WriteMapping(sb, child, indent + 2, false);
                    break;
                case List<object?> child:
                    sb.Append('\n');
                    WriteList(sb, child, indent);
                    break;
                case string s:
                    sb.Append(' ').Append(FormatScalar(s)).Append('\n');
                    break;
                case bool b:
                    sb.Append(' ').Append(b ? "true" : "false").Append('\n');
                    break;
                case DateTimeOffset dto:
                    sb.Append(' ').Append(dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)).Append('\n');
                    break;
                default:
                    sb.Append(' ').Append(FormatScalar(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")).Append('\n');
                    break;
            }
        }
    }

    private static void WriteList(StringBuilder sb, List<object?> list, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var item in list)
        {
            sb.Append(pad).Append("- ");
            switch (item)
            {
                case Dictionary<string, object?> map:
                    WriteMapping(sb, map, indent + 2, inlinedFromList: true);
                    break;
                case List<object?> nested:
                    sb.Append('\n');
                    WriteList(sb, nested, indent + 2);
                    break;
                case null:
                    sb.Append("null\n");
                    break;
                case string s:
                    sb.Append(FormatScalar(s)).Append('\n');
                    break;
                default:
                    sb.Append(FormatScalar(Convert.ToString(item, CultureInfo.InvariantCulture) ?? "")).Append('\n');
                    break;
            }
        }
    }

    private static string EscapeKey(string key)
    {
        if (NeedsQuotes(key)) return "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return key;
    }

    private static string FormatScalar(string s)
    {
        if (s.Length == 0) return "\"\"";
        if (NeedsQuotes(s) || LooksLikeNumberOrBool(s))
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        return s;
    }

    private static bool NeedsQuotes(string s)
    {
        if (s.Length == 0) return true;
        if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) return true;
        foreach (var c in s)
        {
            if (c is ':' or '#' or '[' or ']' or '{' or '}' or ',' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`' or '\n' or '\t')
                return true;
        }
        return s is "yes" or "no" or "Yes" or "No" or "YES" or "NO"
                  or "true" or "false" or "True" or "False" or "TRUE" or "FALSE"
                  or "null" or "Null" or "NULL" or "~" or "on" or "On" or "ON" or "off" or "Off" or "OFF";
    }

    private static bool LooksLikeNumberOrBool(string s)
    {
        // Avoid emitting a SemVer like 1.0 as a float when it's actually a string.
        // Helm best practice is to quote versions; we do that defensively.
        if (s.Length == 0) return false;
        if (s[0] == '-' || char.IsDigit(s[0]))
        {
            // crude check; if the whole thing parses as a number OR matches a "1.0"-ish pattern
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
        }
        return false;
    }
}
