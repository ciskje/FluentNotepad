using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Tolerant JSON scanner for edit mode (RichEditBox), mirroring MarkdownHighlight:
/// visual attributes only, the text stays plain. A hand-rolled single pass (not
/// System.Text.Json) so colors survive while typing incomplete JSON — a strict
/// parser would drop every color on each missing bracket. Keys are strings
/// followed by ':'; // and /* */ comments are painted (JSONC). Skipped for
/// huge docs; runs in TextChanging so it joins the user's undo unit.
/// </summary>
public static class JsonHighlight
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
        // Same TOM mapping as MarkdownHighlight: trim the story-final \r, \r->\n 1:1.
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
                char c = parse[i];
                if (c == '"')
                {
                    int s = i;
                    i++;
                    bool esc = false, closed = false;
                    while (i < n)
                    {
                        char d = parse[i];
                        if (esc) { esc = false; i++; continue; }
                        if (d == '\\') { esc = true; i++; continue; }
                        if (d == '"') { i++; closed = true; break; }
                        if (d == '\n') break; // unterminated: don't paint across lines
                        i++;
                    }
                    if (!closed) continue; // incomplete string while typing: keep base text
                    int j = i;
                    while (j < n && (parse[j] == ' ' || parse[j] == '\t' || parse[j] == '\r' || parse[j] == '\n')) j++;
                    bool isKey = j < n && parse[j] == ':';
                    ops.Add((s, i - s, new Paint(isKey ? pal.JsonKey : pal.JsonString)));
                }
                else if (c == '/' && i + 1 < n && parse[i + 1] == '/')
                {
                    int s = i;
                    i += 2;
                    while (i < n && parse[i] != '\n') i++;
                    ops.Add((s, i - s, new Paint(pal.JsonComment, italic: true)));
                }
                else if (c == '/' && i + 1 < n && parse[i + 1] == '*')
                {
                    int s = i;
                    i += 2;
                    while (i + 1 < n && !(parse[i] == '*' && parse[i + 1] == '/')) i++;
                    if (i + 1 < n) i += 2;
                    else i = n;
                    ops.Add((s, i - s, new Paint(pal.JsonComment, italic: true)));
                }
                else if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int s = i;
                    if (c == '-') { i++; if (i >= n || parse[i] < '0' || parse[i] > '9') continue; }
                    while (i < n && parse[i] >= '0' && parse[i] <= '9') i++;
                    if (i < n && parse[i] == '.')
                    {
                        int dot = i++;
                        while (i < n && parse[i] >= '0' && parse[i] <= '9') i++;
                        if (i == dot + 1) { i = dot; ops.Add((s, i - s, new Paint(pal.JsonNumber))); continue; }
                    }
                    if (i < n && (parse[i] == 'e' || parse[i] == 'E'))
                    {
                        int e = i++;
                        if (i < n && (parse[i] == '+' || parse[i] == '-')) i++;
                        int ds = i;
                        while (i < n && parse[i] >= '0' && parse[i] <= '9') i++;
                        if (i == ds) i = e;
                    }
                    ops.Add((s, i - s, new Paint(pal.JsonNumber)));
                }
                else if (MatchWord(parse, i, "true") || MatchWord(parse, i, "false"))
                {
                    int len = parse[i] == 't' ? 4 : 5;
                    ops.Add((i, len, new Paint(pal.JsonBool)));
                    i += len;
                }
                else if (MatchWord(parse, i, "null"))
                {
                    ops.Add((i, 4, new Paint(pal.JsonNull, italic: true)));
                    i += 4;
                }
                else
                {
                    i++; // punctuation/whitespace: base colors
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

    private static bool MatchWord(string text, int i, string word)
    {
        if (i + word.Length > text.Length) return false;
        for (int k = 0; k < word.Length; k++)
            if (text[i + k] != word[k]) return false;
        int after = i + word.Length;
        // A longer identifier (e.g. "nullable") must not match the prefix.
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_')) return false;
        return true;
    }
}
