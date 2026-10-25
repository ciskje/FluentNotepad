using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Formatting harness shared by the tolerant edit-mode scanners (JSON, XML,
/// YAML, TOML, INI): one display-update batch that resets the base format over
/// the whole document, then paints the spans the scanner collected.
/// </summary>
internal static class TextPainter
{
    // Above this the scanners are skipped: coloring a huge doc costs more than
    // it helps (same cap as the Mac).
    internal const int MaxChars = 300_000;

    internal readonly record struct Span(int Start, int Length, MarkdownShared.Rgb Fg, bool Italic = false,
        bool Bold = false, bool Strike = false, bool Underline = false, MarkdownShared.Rgb? Bg = null);

    internal static void Apply(RichEditBox editor, bool dark, float fontSize,
        Color baseFg, Color baseBg,
        Action<string, MarkdownShared.Palette, List<Span>> scan)
    {
        // Same TOM mapping as the editor text everywhere else (MainWindow).
        string parse = MainWindow.GetEditorText(editor);
        int n = parse.Length;
        var doc = editor.Document;

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

            var ops = new List<Span>();
            scan(parse, new MarkdownShared.Palette(dark), ops);
            foreach (var p in ops)
            {
                if (p.Start < 0 || p.Length <= 0 || p.Start + p.Length > n) continue;
                var r = doc.GetRange(p.Start, p.Start + p.Length);
                var f = r.CharacterFormat;
                f.ForegroundColor = p.Fg.Color;
                f.BackgroundColor = p.Bg?.Color ?? baseBg;
                f.Bold = p.Bold ? FormatEffect.On : FormatEffect.Off;
                f.Italic = p.Italic ? FormatEffect.On : FormatEffect.Off;
                f.Strikethrough = p.Strike ? FormatEffect.On : FormatEffect.Off;
                f.Underline = p.Underline ? UnderlineType.Single : UnderlineType.None;
                f.Size = fontSize;
                r.CharacterFormat = f;
            }
        }
        finally { doc.ApplyDisplayUpdates(); }
    }
}
