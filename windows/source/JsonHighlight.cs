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
                    ops.Add(new TextPainter.Span(s, i - s, isKey ? pal.JsonKey : pal.JsonString));
                }
                else if (c == '/' && i + 1 < n && parse[i + 1] == '/')
                {
                    int s = i;
                    i += 2;
                    while (i < n && parse[i] != '\n') i++;
                    ops.Add(new TextPainter.Span(s, i - s, pal.JsonComment, Italic: true));
                }
                else if (c == '/' && i + 1 < n && parse[i + 1] == '*')
                {
                    int s = i;
                    i += 2;
                    while (i + 1 < n && !(parse[i] == '*' && parse[i + 1] == '/')) i++;
                    if (i + 1 < n) i += 2;
                    else i = n;
                    ops.Add(new TextPainter.Span(s, i - s, pal.JsonComment, Italic: true));
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
                        if (i == dot + 1) { i = dot; ops.Add(new TextPainter.Span(s, i - s, pal.JsonNumber)); continue; }
                    }
                    if (i < n && (parse[i] == 'e' || parse[i] == 'E'))
                    {
                        int e = i++;
                        if (i < n && (parse[i] == '+' || parse[i] == '-')) i++;
                        int ds = i;
                        while (i < n && parse[i] >= '0' && parse[i] <= '9') i++;
                        if (i == ds) i = e;
                    }
                    ops.Add(new TextPainter.Span(s, i - s, pal.JsonNumber));
                }
                else if (MatchWord(parse, i, "true") || MatchWord(parse, i, "false"))
                {
                    int len = parse[i] == 't' ? 4 : 5;
                    ops.Add(new TextPainter.Span(i, len, pal.JsonBool));
                    i += len;
                }
                else if (MatchWord(parse, i, "null"))
                {
                    ops.Add(new TextPainter.Span(i, 4, pal.JsonNull, Italic: true));
                    i += 4;
                }
                else
                {
                    i++; // punctuation/whitespace: base colors
                }
            }
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
