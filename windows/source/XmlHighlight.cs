using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Tolerant XML scanner for edit mode (RichEditBox), mirroring JsonHighlight:
/// visual attributes only; runs in TextChanging so it joins the user's undo
/// unit. Tags, attribute names/values, comments, CDATA and processing
/// instructions are painted; text content stays in the base tone.
/// </summary>
public static class XmlHighlight
{
    public static void Apply(RichEditBox editor, bool dark, float fontSize,
        Color baseFg, Color baseBg) =>
        TextPainter.Apply(editor, dark, fontSize, baseFg, baseBg, Paint);

    private static void Paint(string parse, MarkdownShared.Palette pal,
        List<TextPainter.Span> ops)
    {
        int n = parse.Length;
        int i = 0;
            while (i < n)
            {
                if (Starts(parse, i, "<!--"))
                {
                    int s = i;
                    i += 4;
                    int end = parse.IndexOf("-->", i, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                    ops.Add(new TextPainter.Span(s, i - s, pal.JsonComment, Italic: true));
                }
                else if (Starts(parse, i, "<?"))
                {
                    int s = i;
                    i += 2;
                    int end = parse.IndexOf("?>", i, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    ops.Add(new TextPainter.Span(s, i - s, pal.Dim));
                }
                else if (Starts(parse, i, "<![CDATA["))
                {
                    ops.Add(new TextPainter.Span(i, 9, pal.JsonKey));
                    i += 9;
                    int s = i;
                    int end = parse.IndexOf("]]>", i, StringComparison.Ordinal);
                    int ce = end < 0 ? n : end;
                    if (ce > s) ops.Add(new TextPainter.Span(s, ce - s, pal.JsonString));
                    i = ce;
                    if (end >= 0) { ops.Add(new TextPainter.Span(i, 3, pal.JsonKey)); i += 3; }
                }
                else if (Starts(parse, i, "<!"))
                {
                    int s = i;
                    int end = parse.IndexOf('>', i);
                    i = end < 0 ? n : end + 1;
                    ops.Add(new TextPainter.Span(s, i - s, pal.Dim));
                }
                else if (parse[i] == '<')
                {
                    i = PaintTag(parse, i, n, pal, ops);
                }
                else
                {
                    int nl = parse.IndexOf('<', i);
                    i = nl < 0 ? n : nl; // text content: base tone
                }
            }
    }

    // Paints one <...> tag from the '<', returns the index past its '>' (or n).
    private static int PaintTag(string t, int i, int n, MarkdownShared.Palette pal,
        List<TextPainter.Span> ops)
    {
        int s = i;
        i++; // '<'
        if (i < n && t[i] == '/') i++;
        int ns = i;
        while (i < n && IsNameChar(t[i])) i++;
        if (i > ns) ops.Add(new TextPainter.Span(s, 1, pal.JsonKey)); // '<'
        if (i > ns) ops.Add(new TextPainter.Span(ns, i - ns, pal.JsonKey)); // tag name
        while (i < n)
        {
            char c = t[i];
            if (c == '>') { ops.Add(new TextPainter.Span(i, 1, pal.JsonKey)); return i + 1; }
            if (c == '/' && i + 1 < n && t[i + 1] == '>')
            {
                ops.Add(new TextPainter.Span(i, 2, pal.JsonKey));
                return i + 2;
            }
            if (c == '"' || c == '\'')
            {
                int vs = i;
                char q = c;
                i++;
                while (i < n && t[i] != q && t[i] != '\n') i++;
                if (i < n && t[i] == q) i++;
                ops.Add(new TextPainter.Span(vs, i - vs, pal.JsonString));
                continue;
            }
            if (IsNameStart(t[i]))
            {
                int astart = i;
                while (i < n && IsNameChar(t[i])) i++;
                int aend = i;
                int j = aend;
                while (j < n && (t[j] == ' ' || t[j] == '\t' || t[j] == '\r' || t[j] == '\n')) j++;
                if (j < n && t[j] == '=') ops.Add(new TextPainter.Span(astart, aend - astart, pal.JsonBool));
                continue;
            }
            i++;
        }
        return i; // unterminated tag (typing): paint what we have
    }

    private static bool Starts(string t, int i, string s)
    {
        if (i + s.Length > t.Length) return false;
        for (int k = 0; k < s.Length; k++)
            if (t[i + k] != s[k]) return false;
        return true;
    }

    private static bool IsNameStart(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == ':';

    private static bool IsNameChar(char c) =>
        IsNameStart(c) || (c >= '0' && c <= '9') || c == '.' || c == '-';
}
