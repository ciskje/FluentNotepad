using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Line-based INI scanner for edit mode (RichEditBox): sections, keys,
/// quoted strings and ;/# comments. Values stay in the base tone.
/// Runs in TextChanging so it joins the user's own undo unit.
/// </summary>
public static class IniHighlight
{
    public static void Apply(RichEditBox editor, bool dark, float fontSize,
        Color baseFg, Color baseBg) =>
        TextPainter.Apply(editor, dark, fontSize, baseFg, baseBg, Paint);

    private static void Paint(string parse, MarkdownShared.Palette pal,
        List<TextPainter.Span> ops)
    {
        int n = parse.Length;
        int ls = 0;
            while (ls < n)
            {
                int le = parse.IndexOf('\n', ls);
                if (le < 0) le = n;
                int p = ls;
                while (p < le && (parse[p] == ' ' || parse[p] == '\t')) p++;
                if (p < le && (parse[p] == ';' || parse[p] == '#'))
                {
                    ops.Add(new TextPainter.Span(p, le - p, pal.JsonComment, Italic: true));
                }
                else if (p < le && parse[p] == '[')
                {
                    int close = parse.IndexOf(']', p, le - p);
                    if (close >= 0)
                    {
                        ops.Add(new TextPainter.Span(p, close - p + 1, pal.JsonKey));
                        // Trailing comment after the section.
                        int q = close + 1;
                        while (q < le && (parse[q] == ' ' || parse[q] == '\t')) q++;
                        if (q < le && (parse[q] == ';' || parse[q] == '#'))
                            ops.Add(new TextPainter.Span(q, le - q, pal.JsonComment, Italic: true));
                    }
                }
                else if (p < le)
                {
                    // key = value / key : value (first separator outside quotes).
                    int sep = -1, q = p;
                    bool inS = false, inD = false;
                    for (; q < le; q++)
                    {
                        char c = parse[q];
                        if (inS) { if (c == '\'') inS = false; continue; }
                        if (inD)
                        {
                            if (c == '\\') q++;
                            else if (c == '"') inD = false;
                            continue;
                        }
                        if (c == '\'') { inS = true; continue; }
                        if (c == '"') { inD = true; continue; }
                        if (c == '=' || c == ':') { sep = q; break; }
                    }
                    if (sep > p)
                    {
                        int ke = sep;
                        while (ke > p && (parse[ke - 1] == ' ' || parse[ke - 1] == '\t')) ke--;
                        ops.Add(new TextPainter.Span(p, ke - p, pal.JsonKey));
                        int v = sep + 1;
                        while (v < le && (parse[v] == ' ' || parse[v] == '\t')) v++;
                        if (v < le && (parse[v] == '"' || parse[v] == '\''))
                        {
                            int ve = QuotedEnd(parse, v, le);
                            ops.Add(new TextPainter.Span(v, ve - v, pal.JsonString));
                        }
                        else
                        {
                            // Trailing ; or # comment after whitespace.
                            int cp = -1;
                            bool qs = false, qd = false;
                            for (int k = v; k < le; k++)
                            {
                                char c = parse[k];
                                if (qs) { if (c == '\'') qs = false; continue; }
                                if (qd)
                                {
                                    if (c == '\\') k++;
                                    else if (c == '"') qd = false;
                                    continue;
                                }
                                if (c == '\'') { qs = true; continue; }
                                if (c == '"') { qd = true; continue; }
                                if ((c == ';' || c == '#') && k > v && (parse[k - 1] == ' ' || parse[k - 1] == '\t')) { cp = k; break; }
                            }
                            if (cp >= 0) ops.Add(new TextPainter.Span(cp, le - cp, pal.JsonComment, Italic: true));
                        }
                    }
                }
                ls = le + 1;
            }
    }

    private static int QuotedEnd(string s, int start, int end)
    {
        char q = s[start];
        int i = start + 1;
        while (i < end)
        {
            if (q == '"' && s[i] == '\\') { i += 2; continue; }
            if (s[i] == q)
            {
                if (q == '\'' && i + 1 < end && s[i + 1] == '\'') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return i; // unterminated: highlight to EOL
    }
}
