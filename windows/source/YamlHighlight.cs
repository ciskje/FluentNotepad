using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Tolerant YAML scanner for edit mode (RichEditBox), mirroring JsonHighlight:
/// visual attributes only; runs in TextChanging so it joins the user's undo
/// unit. Mapping keys, quoted strings (span lines), block-scalar content,
/// `-` bullets, tags/anchors/aliases and # comments are painted; plain
/// scalars stay in the base tone except whole-line number/bool/null values.
/// </summary>
public static class YamlHighlight
{
    public static void Apply(RichEditBox editor, bool dark, float fontSize,
        Color baseFg, Color baseBg) =>
        TextPainter.Apply(editor, dark, fontSize, baseFg, baseBg, Paint);

    private static void Paint(string parse, MarkdownShared.Palette pal,
        List<TextPainter.Span> ops)
    {
        int n = parse.Length;
        int depth = 0;
            int blockIndent = -1;
            int i = 0;
            while (i < n)
            {
                bool lineStart = i == 0 || parse[i - 1] == '\n';
                int indent = 0;
                if (lineStart)
                {
                    while (indent < n - i && parse[i + indent] == ' ') indent++;
                    // Block scalar content: more-indented lines after a | or > header.
                    if (blockIndent >= 0)
                    {
                        bool blank = true;
                        for (int k = i; k < n && parse[k] != '\n'; k++)
                            if (parse[k] != ' ' && parse[k] != '\t') { blank = false; break; }
                        if (blank || indent > blockIndent)
                        {
                            int e = parse.IndexOf('\n', i);
                            if (e < 0) e = n;
                            if (e > i) ops.Add(new TextPainter.Span(i, e - i, pal.JsonString));
                            i = e;
                            continue;
                        }
                        blockIndent = -1;
                    }
                }
                char c = parse[i];
                // Quoted spans (single quotes use '' escapes, double quotes \ escapes;
                // both may run past EOL while typing an unclosed string).
                if (c == '\'')
                {
                    int s = i;
                    i++;
                    while (i < n)
                    {
                        if (parse[i] == '\'')
                        {
                            if (i + 1 < n && parse[i + 1] == '\'') { i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    if (i > s) ops.Add(new TextPainter.Span(s, i - s, pal.JsonString));
                    continue;
                }
                if (c == '"')
                {
                    int s = i;
                    i++;
                    while (i < n)
                    {
                        if (parse[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (parse[i] == '"') { i++; break; }
                        i++;
                    }
                    if (i > s) ops.Add(new TextPainter.Span(s, i - s, pal.JsonString));
                    continue;
                }
                if (c == '#' && (lineStart || (i > 0 && (parse[i - 1] == ' ' || parse[i - 1] == '\t'))))
                {
                    int s = i;
                    while (i < n && parse[i] != '\n') i++;
                    ops.Add(new TextPainter.Span(s, i - s, pal.JsonComment, Italic: true));
                    continue;
                }
                if (c == '[' || c == '{') { depth++; i++; continue; }
                if ((c == ']' || c == '}') && depth > 0) { depth--; i++; continue; }
                if (c == '&' || c == '*' || (c == '!' && i + 1 < n && parse[i + 1] != ' '))
                {
                    int s = i;
                    i++;
                    while (i < n && (char.IsLetterOrDigit(parse[i]) || parse[i] == '_' || parse[i] == '-' || parse[i] == '.' || parse[i] == '/')) i++;
                    if (i > s) ops.Add(new TextPainter.Span(s, i - s, pal.Dim));
                    continue;
                }
                bool atKey = lineStart && depth == 0;
                if (atKey)
                {
                    int p = i + indent;
                    if (p < n && parse[p] == '-')
                    {
                        int q = p + 1;
                        if (q < n && (parse[q] == ' ' || parse[q] == '\t'))
                        {
                            ops.Add(new TextPainter.Span(p, 1, pal.Dim));
                            p = q + 1;
                            while (p < n && (parse[p] == ' ' || parse[p] == '\t')) p++;
                        }
                        else if (q >= n || parse[q] == '\n' || parse[q] == '\r')
                        {
                            ops.Add(new TextPainter.Span(p, 1, pal.Dim));
                            i = q;
                            continue;
                        }
                    }
                    else
                    {
                        p = i + indent;
                        while (p < n && (parse[p] == ' ' || parse[p] == '\t')) p++;
                    }
                    if (p < n && parse[p] != '"' && parse[p] != '\'' && parse[p] != '#' && parse[p] != '\n')
                    {
                        int ks = p, ke = p;
                        while (ke < n && parse[ke] != ':' && parse[ke] != '\n' && parse[ke] != '#') ke++;
                        int kt = ke;
                        while (kt > ks && (parse[kt - 1] == ' ' || parse[kt - 1] == '\t')) kt--;
                        if (ke < n && parse[ke] == ':' && (ke + 1 >= n || parse[ke + 1] == ' ' || parse[ke + 1] == '\t' || parse[ke + 1] == '\n' || parse[ke + 1] == '\r'))
                        {
                            string key = parse.Substring(ks, kt - ks);
                            if (key.Length > 0 && key != "?" && !key.StartsWith("? ", StringComparison.Ordinal))
                            {
                                ops.Add(new TextPainter.Span(ks, kt - ks, pal.JsonKey));
                                // Block scalar header? arm the content painter.
                                string after = parse[(ke + 1)..(parse.IndexOf('\n', ke + 1) is int eol && eol >= 0 ? eol : n)];
                                if (IsBlockHeader(after)) blockIndent = indent;
                                // Whole-line scalar value: paint numbers/bools/null.
                                else PaintScalarValue(parse, ke + 1, n, pal, ops);
                                i = ke + 1;
                                continue;
                            }
                        }
                    }
                }
                i++;
            }
    }

    private static bool IsBlockHeader(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        if (i >= s.Length || (s[i] != '|' && s[i] != '>')) return false;
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '+' || c == '-' || char.IsDigit(c) || c == ' ' || c == '\t') i++;
            else if (c == '#') return true;
            else return false;
        }
        return true;
    }

    private static void PaintScalarValue(string text, int i, int n, MarkdownShared.Palette pal, List<TextPainter.Span> ops)
    {
        int s = i;
        while (s < n && (text[s] == ' ' || text[s] == '\t')) s++;
        int e = s;
        while (e < n && text[e] != '\n' && text[e] != '\r' && text[e] != '#') e++;
        while (e > s && (text[e - 1] == ' ' || text[e - 1] == '\t')) e--;
        if (e <= s) return;
        string v = text[s..e];
        if (v.Length == 0) return;
        if (v[0] == '"' || v[0] == '\'') return; // quoted: the string painter owns it
        string low = v.ToLowerInvariant();
        if (low is "true" or "false" or "yes" or "no" or "on" or "off")
        {
            ops.Add(new TextPainter.Span(s, e - s, pal.JsonBool));
            return;
        }
        if (low is "null" or "~" || low == "nul")
        {
            ops.Add(new TextPainter.Span(s, e - s, pal.JsonNull, Italic: true));
            return;
        }
        if (IsNum(v)) ops.Add(new TextPainter.Span(s, e - s, pal.JsonNumber));
    }

    private static bool IsNum(string v)
    {
        int i = 0;
        if (v.Length == 0) return false;
        if (v[0] == '+' || v[0] == '-')
        {
            if (v.Length == 1) return false;
            i = 1;
        }
        bool digit = false, dot = false, exp = false;
        for (; i < v.Length; i++)
        {
            char c = v[i];
            if (char.IsDigit(c)) digit = true;
            else if (c == '_') { }
            else if (c == '.' && !dot && !exp) dot = true;
            else if ((c == 'e' || c == 'E') && !exp && digit)
            {
                exp = true;
                digit = false;
                if (i + 1 < v.Length && (v[i + 1] == '+' || v[i + 1] == '-')) i++;
            }
            else return false;
        }
        return digit && (dot || exp);
    }
}
