using System.Text;
using System.Text.RegularExpressions;

namespace HelmRepoLite;

/// <summary>
/// Converts a subset of GitHub-Flavored Markdown to an HTML fragment.
/// Covers what helm-docs produces: ATX headings, fenced code blocks, GFM tables,
/// ordered/unordered lists, bold, italic, inline code, links, images, and autolinks.
/// Not a full CommonMark implementation.
/// </summary>
public static partial class MarkdownRenderer
{
    [GeneratedRegex(@"^\s*[-*+]\s+")]
    private static partial Regex UnorderedItemPattern();

    [GeneratedRegex(@"^\d+\.\s+")]
    private static partial Regex OrderedItemPattern();

    [GeneratedRegex(@"^-{3,}\s*$|^\*{3,}\s*$|^_{3,}\s*$")]
    private static partial Regex HorizontalRulePattern();

    public static string ToHtml(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var sb = new StringBuilder();
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var lang = Encode(line.Length > 3 ? line[3..].Trim() : "");
                sb.Append("<pre><code");
                if (!string.IsNullOrEmpty(lang))
                    sb.Append(" class=\"language-").Append(lang).Append('"');
                sb.Append('>');
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    sb.AppendLine(Encode(lines[i]));
                    i++;
                }
                if (i < lines.Length) i++;
                sb.AppendLine("</code></pre>");
                continue;
            }

            // ATX heading
            if (line.StartsWith('#'))
            {
                int level = 0;
                while (level < line.Length && line[level] == '#') level++;
                if (level <= 6 && (level == line.Length || line[level] == ' '))
                {
                    var text = level < line.Length ? line[(level + 1)..].Trim() : "";
                    var id = MakeId(text);
                    sb.Append("<h").Append(level).Append(" id=\"").Append(id).Append("\">")
                      .Append(Inline(text))
                      .Append("</h").Append(level).AppendLine(">");
                    i++;
                    continue;
                }
            }

            // Horizontal rule
            if (HorizontalRulePattern().IsMatch(line))
            {
                sb.AppendLine("<hr>");
                i++;
                continue;
            }

            // GFM table (line starts with |)
            if (line.TrimStart().StartsWith('|'))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                    tableLines.Add(lines[i++]);
                EmitTable(sb, tableLines);
                continue;
            }

            // Unordered list
            if (UnorderedItemPattern().IsMatch(line))
            {
                sb.AppendLine("<ul>");
                while (i < lines.Length && UnorderedItemPattern().IsMatch(lines[i]))
                {
                    var text = UnorderedItemPattern().Replace(lines[i], "");
                    sb.Append("<li>").Append(Inline(text)).AppendLine("</li>");
                    i++;
                }
                sb.AppendLine("</ul>");
                continue;
            }

            // Ordered list
            if (OrderedItemPattern().IsMatch(line))
            {
                sb.AppendLine("<ol>");
                while (i < lines.Length && OrderedItemPattern().IsMatch(lines[i]))
                {
                    var text = OrderedItemPattern().Replace(lines[i], "");
                    sb.Append("<li>").Append(Inline(text)).AppendLine("</li>");
                    i++;
                }
                sb.AppendLine("</ol>");
                continue;
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Paragraph: collect consecutive non-block lines
            var para = new List<string>();
            while (i < lines.Length
                && !string.IsNullOrWhiteSpace(lines[i])
                && !lines[i].StartsWith('#')
                && !lines[i].StartsWith("```", StringComparison.Ordinal)
                && !lines[i].TrimStart().StartsWith('|')
                && !UnorderedItemPattern().IsMatch(lines[i])
                && !OrderedItemPattern().IsMatch(lines[i])
                && !HorizontalRulePattern().IsMatch(lines[i]))
            {
                para.Add(lines[i++]);
            }
            if (para.Count > 0)
                sb.Append("<p>").Append(Inline(string.Join(" ", para))).AppendLine("</p>");
        }

        return sb.ToString();
    }

    private static void EmitTable(StringBuilder sb, List<string> rows)
    {
        if (rows.Count < 2) return;

        sb.AppendLine("<table>");
        sb.Append("<thead><tr>");
        foreach (var h in SplitRow(rows[0]))
            sb.Append("<th>").Append(Inline(h)).Append("</th>");
        sb.AppendLine("</tr></thead>");

        if (rows.Count > 2)
        {
            sb.AppendLine("<tbody>");
            for (int r = 2; r < rows.Count; r++)
            {
                sb.Append("<tr>");
                foreach (var c in SplitRow(rows[r]))
                    sb.Append("<td>").Append(Inline(c)).Append("</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody>");
        }
        sb.AppendLine("</table>");
    }

    private static string[] SplitRow(string row)
    {
        row = row.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|')) row = row[..^1];
        return row.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string MakeId(string text)
    {
        // strip inline markers for an anchor-friendly id
        var plain = Regex.Replace(text, @"[`*_\[\]()!#]", "").Trim().ToLowerInvariant();
        return Regex.Replace(plain, @"[^\w\s-]", "").Replace(' ', '-');
    }

    private static string Inline(string text)
    {
        var sb = new StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // Inline code — single or double backtick
            if (c == '`')
            {
                int ticks = (i + 1 < text.Length && text[i + 1] == '`') ? 2 : 1;
                string marker = new('`', ticks);
                int end = text.IndexOf(marker, i + ticks, StringComparison.Ordinal);
                if (end > i)
                {
                    sb.Append("<code>").Append(Encode(text[(i + ticks)..end].Trim())).Append("</code>");
                    i = end + ticks;
                    continue;
                }
            }

            // Autolink <https://...>
            if (c == '<' && i + 1 < text.Length)
            {
                int end = text.IndexOf('>', i + 1);
                if (end > i)
                {
                    var url = text[(i + 1)..end];
                    if (url.StartsWith("http://", StringComparison.Ordinal) ||
                        url.StartsWith("https://", StringComparison.Ordinal))
                    {
                        sb.Append("<a href=\"").Append(Encode(url)).Append("\">")
                          .Append(Encode(url)).Append("</a>");
                        i = end + 1;
                        continue;
                    }
                }
            }

            // Image ![alt](url)
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int altEnd = text.IndexOf(']', i + 2);
                if (altEnd > 0 && altEnd + 1 < text.Length && text[altEnd + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', altEnd + 2);
                    if (urlEnd > 0)
                    {
                        var alt = text[(i + 2)..altEnd];
                        var url = text[(altEnd + 2)..urlEnd];
                        sb.Append("<img src=\"").Append(Encode(url))
                          .Append("\" alt=\"").Append(Encode(alt)).Append("\">");
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            // Link [text](url)
            if (c == '[')
            {
                int textEnd = text.IndexOf(']', i + 1);
                if (textEnd > 0 && textEnd + 1 < text.Length && text[textEnd + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', textEnd + 2);
                    if (urlEnd > 0)
                    {
                        var linkText = text[(i + 1)..textEnd];
                        var url = text[(textEnd + 2)..urlEnd];
                        sb.Append("<a href=\"").Append(Encode(url)).Append("\">")
                          .Append(Inline(linkText)).Append("</a>");
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            // Bold: **text** or __text__
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                string marker = new(c, 2);
                int end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    sb.Append("<strong>").Append(Inline(text[(i + 2)..end])).Append("</strong>");
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* or _text_
            if (c is '*' or '_')
            {
                int end = text.IndexOf(c, i + 1);
                if (end > i)
                {
                    sb.Append("<em>").Append(Inline(text[(i + 1)..end])).Append("</em>");
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(Encode(c));
            i++;
        }

        return sb.ToString();
    }

    private static string Encode(string s) =>
        System.Net.WebUtility.HtmlEncode(s);

    private static string Encode(char c) => c switch
    {
        '&' => "&amp;",
        '<' => "&lt;",
        '>' => "&gt;",
        '"' => "&quot;",
        _ => c.ToString()
    };
}
