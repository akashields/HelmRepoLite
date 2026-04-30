using System.Text;

namespace HelmRepoLite;

/// <summary>
/// Reflection-free JSON writer for the concrete types this server serialises.
/// System.Text.Json strips its reflection metadata when PublishTrimmed=true, so
/// Results.Json() fails at runtime on anonymous types and Dictionary&lt;string,object?&gt;.
/// This writer handles our known shapes (string, bool, Dictionary, List) without
/// any type-info resolution.
/// </summary>
internal static class SimpleJson
{
    public static string Write(object? value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value);
        return sb.ToString();
    }

    /// <summary>Returns <c>{"error":"&lt;escaped&gt;"}</c>.</summary>
    public static string Err(string message)
    {
        var sb = new StringBuilder("{\"error\":");
        WriteString(sb, message);
        sb.Append('}');
        return sb.ToString();
    }

    internal static void WriteValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case Dictionary<string, object?> dict:
                WriteObject(sb, dict);
                break;
            case List<object?> list:
                WriteArray(sb, list);
                break;
            default:
                WriteString(sb, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static void WriteObject(StringBuilder sb, Dictionary<string, object?> dict)
    {
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in dict)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, k);
            sb.Append(':');
            WriteValue(sb, v);
        }
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, List<object?> list)
    {
        sb.Append('[');
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteValue(sb, list[i]);
        }
        sb.Append(']');
    }

    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
