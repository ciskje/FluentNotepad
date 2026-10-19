using Microsoft.UI.Text;
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
    public const int MaxChars = 300_000;

    private readonly struct Paint
    {
        public readonly MarkdownShared.Rgb Fg;
        public readonly bool Italic;

        public Paint(MarkdownShared.Rgb fg, bool italic = false)
        {
            Fg = fg; Italic = italic;
        }
    }

    public static void Apply(RichEditBox editor, bool dark, float fontSize,
        Color baseFg, Color baseBg)
    {
        var doc = editor.Document;
        doc.GetText(TextGetOptions.None, out string raw);
        string text = (raw.Length > 0 && raw[^1] == '\r') ? raw[..^1] : raw;
        string parse = text.Replace('\r', '\n');
        int n = parse.Length;

        doc.BatchDisplayUpdates();
        try
        {
            if (n > 0)
            {
                var all = doc.GetRange(0, n);
                var f = all.CharacterFormat;
                f.ForegroundColor = baseFg;
                f.BackgroundColor = baseBg;
                f.Bold = FormatEffect.Off;
                f.Italic = FormatEffect.Off;
                f.Strikethrough = FormatEffect.Off;
                f.Underline = UnderlineType.None;
                f.Size = fontSize;
                all.CharacterFormat = f;
            }
            if (n == 0 || n > MaxChars) return;

            var pal = new MarkdownShared.Palette(dark);
            var ops = new List<(int Start, int Length, Paint Paint)>();
            int i = 0;
            while (i < n)
            {
                if (Starts(parse, i, "<!--"))
                {
                    int s = i;
                    i += 4;
                    int end = parse.IndexOf("-->", i, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                    ops.Add((s, i - s, new Paint(pal.JsonComment, italic: true)));
                }
                else if (Starts(parse, i, "<?"))
                {
                    int s = i;
                    i += 2;
                    int end = parse.IndexOf("?>", i, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    ops.Add((s, i - s, new Paint(pal.Dim)));
                }
                else if (Starts(parse, i, "<![CDATA["))
                {
                    ops.Add((i, 9, new Paint(pal.JsonKey)));
                    i += 9;
                    int s = i;
                    int end = parse.IndexOf("]]>", i, StringComparison.Ordinal);
                    int ce = end < 0 ? n : end;
                    if (ce > s) ops.Add((s, ce - s, new Paint(pal.JsonString)));
                    i = ce;
                    if (end >= 0) { ops.Add((i, 3, new Paint(pal.JsonKey))); i += 3; }
                }
                else if (Starts(parse, i, "<!"))
                {
                    int s = i;
                    int end = parse.IndexOf('>', i);
                    i = end < 0 ? n : end + 1;
                    ops.Add((s, i - s, new Paint(pal.Dim)));
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
            foreach (var (s, l, p) in ops)
            {
                if (s < 0 || l <= 0 || s + l > n) continue;
                var r = doc.GetRange(s, s + l);
                var f = r.CharacterFormat;
                f.ForegroundColor = p.Fg.Color;
                f.BackgroundColor = baseBg;
                f.Bold = FormatEffect.Off;
                f.Italic = p.Italic ? FormatEffect.On : FormatEffect.Off;
                f.Strikethrough = FormatEffect.Off;
                f.Underline = UnderlineType.None;
                f.Size = fontSize;
                r.CharacterFormat = f;
            }
        }
        finally { doc.ApplyDisplayUpdates(); }
    }

    // Paints one <...> tag from the '<', returns the index past its '>' (or n).
    private static int PaintTag(string t, int i, int n, MarkdownShared.Palette pal,
        List<(int Start, int Length, Paint Paint)> ops)
    {
        int s = i;
        i++; // '<'
        if (i < n && t[i] == '/') i++;
        int ns = i;
        while (i < n && IsNameChar(t[i])) i++;
        if (i > ns) ops.Add((s, 1, new Paint(pal.JsonKey))); // '<'
        if (i > ns) ops.Add((ns, i - ns, new Paint(pal.JsonKey))); // tag name
        while (i < n)
        {
            char c = t[i];
            if (c == '>') { ops.Add((i, 1, new Paint(pal.JsonKey))); return i + 1; }
            if (c == '/' && i + 1 < n && t[i + 1] == '>')
            {
                ops.Add((i, 2, new Paint(pal.JsonKey)));
                return i + 2;
            }
            if (c == '"' || c == '\'')
            {
                int vs = i;
                char q = c;
                i++;
                while (i < n && t[i] != q && t[i] != '\n') i++;
                if (i < n && t[i] == q) i++;
                ops.Add((vs, i - vs, new Paint(pal.JsonString)));
                continue;
            }
            if (IsNameStart(t[i]))
            {
                int astart = i;
                while (i < n && IsNameChar(t[i])) i++;
                int aend = i;
                int j = aend;
                while (j < n && (t[j] == ' ' || t[j] == '\t' || t[j] == '\r' || t[j] == '\n')) j++;
                if (j < n && t[j] == '=') ops.Add((astart, aend - astart, new Paint(pal.JsonBool)));
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
