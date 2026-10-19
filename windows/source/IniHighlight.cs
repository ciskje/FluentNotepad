using Microsoft.UI.Text;
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
            int ls = 0;
            while (ls < n)
            {
                int le = parse.IndexOf('\n', ls);
                if (le < 0) le = n;
                int p = ls;
                while (p < le && (parse[p] == ' ' || parse[p] == '\t')) p++;
                if (p < le && (parse[p] == ';' || parse[p] == '#'))
                {
                    ops.Add((p, le - p, new Paint(pal.JsonComment, italic: true)));
                }
                else if (p < le && parse[p] == '[')
                {
                    int close = parse.IndexOf(']', p, le - p);
                    if (close >= 0)
                    {
                        ops.Add((p, close - p + 1, new Paint(pal.JsonKey)));
                        // Trailing comment after the section.
                        int q = close + 1;
                        while (q < le && (parse[q] == ' ' || parse[q] == '\t')) q++;
                        if (q < le && (parse[q] == ';' || parse[q] == '#'))
                            ops.Add((q, le - q, new Paint(pal.JsonComment, italic: true)));
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
                        ops.Add((p, ke - p, new Paint(pal.JsonKey)));
                        int v = sep + 1;
                        while (v < le && (parse[v] == ' ' || parse[v] == '\t')) v++;
                        if (v < le && (parse[v] == '"' || parse[v] == '\''))
                        {
                            int ve = QuotedEnd(parse, v, le);
                            ops.Add((v, ve - v, new Paint(pal.JsonString)));
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
                            if (cp >= 0) ops.Add((cp, le - cp, new Paint(pal.JsonComment, italic: true)));
                        }
                    }
                }
                ls = le + 1;
            }
            foreach (var (s, l, pp) in ops)
            {
                if (s < 0 || l <= 0 || s + l > n) continue;
                var r = doc.GetRange(s, s + l);
                var f = r.CharacterFormat;
                f.ForegroundColor = pp.Fg.Color;
                f.BackgroundColor = baseBg;
                f.Bold = FormatEffect.Off;
                f.Italic = pp.Italic ? FormatEffect.On : FormatEffect.Off;
                f.Strikethrough = FormatEffect.Off;
                f.Underline = UnderlineType.None;
                f.Size = fontSize;
                r.CharacterFormat = f;
            }
        }
        finally { doc.ApplyDisplayUpdates(); }
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
