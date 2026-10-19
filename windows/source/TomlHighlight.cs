using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Tolerant TOML scanner for edit mode (RichEditBox), mirroring JsonHighlight:
/// visual attributes only; runs in TextChanging so it joins the user's undo
/// unit. Bare/quoted keys, table headers, strings (incl. multi-line),
/// numbers (incl. hex/oct/bin), booleans, datetimes-as-text and # comments
/// (# starts a comment anywhere outside a string).
/// </summary>
public static class TomlHighlight
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

            // Full-line table headers: [table] and [[array]] (always single-line).
            for (int ls = 0; ls < n;)
            {
                int le = parse.IndexOf('\n', ls);
                if (le < 0) le = n;
                int p = ls;
                while (p < le && (parse[p] == ' ' || parse[p] == '\t')) p++;
                if (p < le && parse[p] == '[')
                {
                    int q = le - 1;
                    while (q > p && (parse[q] == ' ' || parse[q] == '\t' || parse[q] == '\r')) q--;
                    bool closed = q > p && parse[q] == ']';
                    if (closed && !parse.Substring(p, q - p + 1).Contains('#'))
                        ops.Add((p, q - p + 1, new Paint(pal.JsonKey)));
                }
                ls = le + 1;
            }

            int i = 0;
            while (i < n)
            {
                char c = parse[i];
                if (c == '#')
                {
                    int s = i;
                    while (i < n && parse[i] != '\n') i++;
                    ops.Add((s, i - s, new Paint(pal.JsonComment, italic: true)));
                }
                else if (c == '"')
                {
                    int s = i;
                    if (i + 2 < n && parse[i + 1] == '"' && parse[i + 2] == '"') i = ScanTriple(parse, i, '"');
                    else i = ScanString(parse, i, '"');
                    if (i > s) ops.Add((s, i - s, new Paint(pal.JsonString)));
                }
                else if (c == '\'')
                {
                    int s = i;
                    if (i + 2 < n && parse[i + 1] == '\'' && parse[i + 2] == '\'')
                    {
                        int end = parse.IndexOf("'''", i + 3, StringComparison.Ordinal);
                        i = end < 0 ? n : end + 3;
                    }
                    else i = ScanString(parse, i, '\'');
                    if (i > s) ops.Add((s, i - s, new Paint(pal.JsonString)));
                }
                else if (c == '[' || c == ']' || c == '{' || c == '}')
                {
                    ops.Add((i, 1, new Paint(pal.JsonKey)));
                    i++;
                }
                else if (IsBareStart(c))
                {
                    // Bare word (maybe dotted): a key when followed by '='.
                    int s = i;
                    while (true)
                    {
                        while (i < n && IsBareChar(parse[i])) i++;
                        int j = i;
                        while (j < n && (parse[j] == ' ' || parse[j] == '\t')) j++;
                        if (j < n && parse[j] == '.')
                        {
                            i = j + 1;
                            while (i < n && (parse[i] == ' ' || parse[i] == '\t')) i++;
                            continue;
                        }
                        if (j < n && parse[j] == '=')
                        {
                            ops.Add((s, i - s, new Paint(pal.JsonKey)));
                            i = j + 1; // consume '=' too: nothing to paint for it
                        }
                        break;
                    }
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
                else if (char.IsDigit(c) || ((c == '+' || c == '-') && i + 1 < n && (char.IsDigit(parse[i + 1]) || parse[i + 1] == 'i' || parse[i + 1] == 'n')))
                {
                    int s = i;
                    if (c == '+' || c == '-') i++;
                    int ds = i;
                    while (i < n && (char.IsLetterOrDigit(parse[i]) || parse[i] == '_' || parse[i] == '.' || parse[i] == '+' || parse[i] == '-' || parse[i] == ':')) i++;
                    string tok = parse.Substring(s, i - s);
                    if (tok.Contains('-') || tok.Contains(':'))
                        ops.Add((s, i - s, new Paint(pal.JsonString))); // datetime-ish
                    else if (i > ds)
                        ops.Add((s, i - s, new Paint(pal.JsonNumber)));
                }
                else if (MatchWord(parse, i, "inf") || MatchWord(parse, i, "nan"))
                {
                    ops.Add((i, 3, new Paint(pal.JsonNumber)));
                    i += 3;
                }
                else
                {
                    i++;
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

    private static int ScanString(string t, int i, char q)
    {
        i++; // opening quote
        while (i < t.Length)
        {
            if (t[i] == '\\' && q == '"') { i += 2; continue; }
            if (t[i] == q) return i + 1;
            if (t[i] == '\n') return i;
            i++;
        }
        return i;
    }

    private static int ScanTriple(string t, int i, char q)
    {
        string run = new(q, 3);
        int end = t.IndexOf(run, i + 3, StringComparison.Ordinal);
        return end < 0 ? t.Length : end + 3;
    }

    private static bool IsBareStart(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || char.IsDigit(c);

    private static bool IsBareChar(char c) =>
        IsBareStart(c) || c == '-';

    private static bool MatchWord(string text, int i, string word)
    {
        if (i + word.Length > text.Length) return false;
        for (int k = 0; k < word.Length; k++)
            if (text[i + k] != word[k]) return false;
        int after = i + word.Length;
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_' || text[after] == '-')) return false;
        return true;
    }
}
