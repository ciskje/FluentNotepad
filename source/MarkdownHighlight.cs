using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Parser-driven Markdown highlighting for edit mode (RichEditBox),
/// mirroring FluentNotepadMac Highlight.swift: only real syntax nodes are
/// painted (Markdig AST, already a dependency), so `_` in `IQ3_S`, `|` in
/// code spans or `*` across table cells can never leak color. Visual
/// attributes only; the text stays plain. Later ops overwrite earlier ones.
/// Skipped for non-markdown, empty and huge docs.
/// Runs inside TextChanging (the EN_UPDATE equivalent), so the formatting
/// joins the user's own undo unit instead of polluting the undo stack.
/// </summary>
public static class MarkdownHighlight
{
    public const int MaxChars = 300_000;

    private static readonly MarkdownPipeline _pipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        // Same as MarkdownHtml: single-dollar math swallows "$ ... | ... $"
        // across table cells; it never renders, keep dollars literal.
        builder.Extensions.RemoveAll(e => (e.GetType().FullName ?? "").Contains("Mathematic"));
        return builder.Build();
    }

    private readonly struct Paint
    {
        public readonly Color Fg;
        public readonly Color? Bg;
        public readonly bool Bold;
        public readonly bool Italic;
        public readonly bool Strike;
        public readonly bool Underline;

        public Paint(Color fg, Color? bg = null, bool bold = false, bool italic = false,
            bool strike = false, bool underline = false)
        {
            Fg = fg; Bg = bg; Bold = bold; Italic = italic; Strike = strike; Underline = underline;
        }
    }

    // Same VS tones as the preview CSS (MarkdownHtml) and the Mac palette.
    private readonly struct Palette
    {
        public readonly Color Heading, Strong, Emphasis, Code, CodeBg, Link, Quote, Marker, Dim;
        public Palette(bool dark)
        {
            if (dark)
            {
                Heading = C(0x56, 0x9C, 0xD6); Strong = C(0xDC, 0xDC, 0xAA); Emphasis = C(0xC5, 0x86, 0xC0);
                Code = C(0xCE, 0x91, 0x78); CodeBg = C(0x2D, 0x2D, 0x30); Link = C(0x4F, 0xC1, 0xFF);
                Quote = C(0x9C, 0xDC, 0xFE); Marker = C(0x56, 0x9C, 0xD6); Dim = C(0x80, 0x80, 0x80);
            }
            else
            {
                Heading = C(0x0F, 0x6C, 0xBD); Strong = C(0xA3, 0x15, 0x15); Emphasis = C(0x7A, 0x3E, 0x9D);
                Code = C(0xA3, 0x15, 0x15); CodeBg = C(0xF3, 0xF3, 0xF3); Link = C(0x0F, 0x6C, 0xBD);
                Quote = C(0x07, 0x5A, 0xA0); Marker = C(0x0F, 0x6C, 0xBD); Dim = C(0x80, 0x80, 0x80);
            }
        }
        private static Color C(int r, int g, int b) => Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
    }

    private sealed class Ctx
    {
        public readonly string Text;
        public readonly int[] Starts;
        public readonly string[] Lines;
        public readonly Palette Pal;
        public readonly List<(int Start, int Length, Paint Paint)> Ops = new();
        public readonly List<(int Start, int Length)> Guarded = new(); // code/link/image/html: table pipes skip these
        public readonly List<(int Start, int Length, List<(int Start, int Length)> Covered)> Tables = new();
        public readonly Stack<(int Start, int Length, List<(int Start, int Length)> Covered)> TableStack = new();

        public Ctx(string text, Palette pal)
        {
            Text = text;
            Pal = pal;
            var starts = new List<int>();
            var lines = new List<string>();
            int pos = 0;
            while (true)
            {
                int nl = text.IndexOf('\n', pos);
                if (nl < 0) { starts.Add(pos); lines.Add(text.Substring(pos)); break; }
                starts.Add(pos); lines.Add(text.Substring(pos, nl - pos)); pos = nl + 1;
            }
            Starts = starts.ToArray();
            Lines = lines.ToArray();
        }
    }

    public static void Apply(RichEditBox editor, bool isMarkdown, bool dark, float fontSize,
        Color baseFg, Color baseBg)
    {
        var doc = editor.Document;
        doc.GetText(TextGetOptions.None, out string raw);
        // TOM appends a story-final \r and uses \r for breaks: trim exactly one
        // (a real trailing newline keeps its own \r) and map \r->\n 1:1, so
        // Markdig spans index the control content directly.
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
                // NOT transparent: RichEdit ignores the alpha and renders it
                // black (black bars behind every line). Paint the editor
                // background instead; theme changes repaint via RefreshHighlight.
                f.BackgroundColor = baseBg;
                f.Bold = FormatEffect.Off;
                f.Italic = FormatEffect.Off;
                f.Strikethrough = FormatEffect.Off;
                f.Underline = UnderlineType.None;
                f.Size = fontSize;
                all.CharacterFormat = f;
            }
            if (!isMarkdown || n == 0 || n > MaxChars) return;

            MarkdownDocument md;
            try { md = Markdig.Markdown.Parse(parse, _pipeline); }
            catch { return; }
            var ctx = new Ctx(parse, new Palette(dark));
            foreach (var b in md) PaintBlock(b, ctx);
            PaintTables(ctx);
            foreach (var (s, l, p) in ctx.Ops)
            {
                if (s < 0 || l <= 0 || s + l > n) continue;
                var r = doc.GetRange(s, s + l);
                var f = r.CharacterFormat;
                f.ForegroundColor = p.Fg;
                f.BackgroundColor = p.Bg ?? baseBg;
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

    private static bool TrySpan(SourceSpan span, int maxLen, out int start, out int length)
    {
        start = 0; length = 0;
        if (span.IsEmpty) return false;
        start = span.Start;
        length = span.End - span.Start + 1;
        if (start < 0 || length <= 0 || start + length > maxLen) return false;
        return true;
    }

    // MARK: - Tree walk (parent ops first, children overwrite)

    private static void PaintBlock(Block b, Ctx ctx)
    {
        var pal = ctx.Pal;
        switch (b)
        {
            case HeadingBlock:
                if (TrySpan(b.Span, ctx.Text.Length, out int hs, out int hl))
                    ctx.Ops.Add((hs, hl, new Paint(pal.Heading, bold: true)));
                PaintLeafInline(b, ctx);
                break;
            case QuoteBlock:
                if (TrySpan(b.Span, ctx.Text.Length, out int qs, out int ql))
                {
                    ctx.Ops.Add((qs, ql, new Paint(pal.Quote)));
                    PaintLineMarkers(qs, ql, ctx, c => c == '>', pal.Marker);
                }
                foreach (var c in ((ContainerBlock)b)) PaintBlock(c, ctx);
                break;
            case CodeBlock:
                if (TrySpan(b.Span, ctx.Text.Length, out int cs, out int cl))
                {
                    ctx.Ops.Add((cs, cl, new Paint(pal.Code, pal.CodeBg)));
                    ctx.Guarded.Add((cs, cl));
                }
                break;
            case ThematicBreakBlock:
                if (TrySpan(b.Span, ctx.Text.Length, out int ts, out int tl))
                    ctx.Ops.Add((ts, tl, new Paint(pal.Dim)));
                break;
            case ListBlock:
                foreach (var c in ((ContainerBlock)b)) PaintBlock(c, ctx);
                break;
            case ListItemBlock item:
                PaintListMarker(item, ctx);
                foreach (var c in ((ContainerBlock)b)) PaintBlock(c, ctx);
                break;
            case Table:
                if (TrySpan(b.Span, ctx.Text.Length, out int tas, out int tal))
                    ctx.TableStack.Push((tas, tal, new List<(int, int)>()));
                foreach (var c in ((ContainerBlock)b)) PaintBlock(c, ctx);
                if (ctx.TableStack.Count > 0)
                {
                    var done = ctx.TableStack.Pop();
                    ctx.Tables.Add((done.Start, done.Length, done.Covered));
                }
                break;
            case TableRow row:
                if (row.IsHeader && TrySpan(b.Span, ctx.Text.Length, out int hrs, out int hrl))
                    ctx.Ops.Add((hrs, hrl, new Paint(pal.Heading, bold: true)));
                if (TrySpan(b.Span, ctx.Text.Length, out int rs, out int rl) && ctx.TableStack.Count > 0)
                    ctx.TableStack.Peek().Covered.Add((rs, rl));
                foreach (var c in ((ContainerBlock)b)) PaintBlock(c, ctx);
                break;
            default:
                if (b is LeafBlock leaf && leaf.Inline != null) PaintInline(leaf.Inline, ctx);
                else if (b is ContainerBlock cb) foreach (var c in cb) PaintBlock(c, ctx);
                break;
        }
        if (b is HtmlBlock && TrySpan(b.Span, ctx.Text.Length, out int ys, out int yl))
        {
            PaintHtml(ys, yl, ctx);
            ctx.Guarded.Add((ys, yl));
        }
    }

    private static void PaintLeafInline(Block b, Ctx ctx)
    {
        if (b is LeafBlock leaf && leaf.Inline != null) PaintInline(leaf.Inline, ctx);
    }

    private static void PaintInline(Inline inl, Ctx ctx)
    {
        var pal = ctx.Pal;
        for (Inline? cur = inl; cur != null; cur = cur.NextSibling)
        {
            switch (cur)
            {
                case EmphasisInline e:
                    if (TrySpan(e.Span, ctx.Text.Length, out int es, out int el))
                    {
                        Paint p = e.DelimiterChar == '~'
                            ? new Paint(pal.Dim, strike: true)
                            : e.DelimiterCount <= 1
                                ? new Paint(pal.Emphasis, italic: true)
                                : new Paint(pal.Strong, bold: true);
                        ctx.Ops.Add((es, el, p));
                    }
                    if (e.FirstChild != null) PaintInline(e.FirstChild, ctx);
                    break;
                case CodeInline:
                    if (TrySpan(cur.Span, ctx.Text.Length, out int cs, out int cl))
                    {
                        ctx.Ops.Add((cs, cl, new Paint(pal.Code, pal.CodeBg)));
                        ctx.Guarded.Add((cs, cl));
                    }
                    break;
                case LinkInline l:
                    PaintLinkLike(l, ctx);
                    if (TrySpan(l.Span, ctx.Text.Length, out int ls, out int ll))
                        ctx.Guarded.Add((ls, ll));
                    if (l.FirstChild != null) PaintInline(l.FirstChild, ctx);
                    break;
                case AutolinkInline:
                    if (TrySpan(cur.Span, ctx.Text.Length, out int aus, out int aul))
                        ctx.Ops.Add((aus, aul, new Paint(pal.Link, underline: true)));
                    break;
                case TaskList:
                    if (TrySpan(cur.Span, ctx.Text.Length, out int tls, out int tll))
                        ctx.Ops.Add((tls, tll, new Paint(pal.Marker, bold: true)));
                    break;
                case HtmlInline:
                    if (TrySpan(cur.Span, ctx.Text.Length, out int ys, out int yl))
                    {
                        PaintHtmlTag(ys, yl, ctx);
                        ctx.Guarded.Add((ys, yl));
                    }
                    break;
                case ContainerInline c:
                    if (c.FirstChild != null) PaintInline(c.FirstChild, ctx);
                    break;
                default:
                    break;
            }
        }
    }

    // MARK: - Block markers (first non-space char scans, no regex)

    private static IEnumerable<(int Start, string Line)> LinesIn(int rs, int rl, Ctx ctx)
    {
        int re = rs + rl;
        for (int k = 0; k < ctx.Starts.Length; k++)
        {
            int lstart = ctx.Starts[k];
            int llen = ctx.Lines[k].Length;
            if (lstart < re && lstart + llen > rs)
                yield return (lstart, ctx.Lines[k]);
        }
    }

    private static void PaintLineMarkers(int rs, int rl, Ctx ctx, Func<char, bool> check, Color color)
    {
        foreach (var (lstart, line) in LinesIn(rs, rl, ctx))
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i < line.Length && check(line[i]))
                ctx.Ops.Add((lstart + i, 1, new Paint(color)));
        }
    }

    private static void PaintListMarker(ListItemBlock item, Ctx ctx)
    {
        if (!TrySpan(item.Span, ctx.Text.Length, out int rs, out int rl)) return;
        var first = LinesIn(rs, rl, ctx).FirstOrDefault();
        if (first.Line == null) return;
        int lstart = first.Start;
        string line = first.Line;
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        string rest = line[i..];
        int markLen = 0;
        if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ "))
            markLen = 2;
        else
        {
            int digits = 0;
            while (digits < rest.Length && char.IsDigit(rest[digits])) digits++;
            string after = rest[digits..];
            if (digits > 0 && (after.StartsWith(". ") || after.StartsWith(") ")))
                markLen = digits + 2;
        }
        if (markLen == 0) return;
        ctx.Ops.Add((lstart + i, markLen, new Paint(ctx.Pal.Marker)));
        // Task checkbox right after the marker: accent it.
        string afterMark = line[(i + markLen)..];
        if (afterMark.StartsWith("[ ]") || afterMark.StartsWith("[x]") || afterMark.StartsWith("[X]"))
            ctx.Ops.Add((lstart + i + markLen, 3, new Paint(ctx.Pal.Marker, bold: true)));
    }

    // MARK: - Links/images: children blue+underline, the rest dim

    private static void PaintLinkLike(LinkInline l, Ctx ctx)
    {
        if (!TrySpan(l.Span, ctx.Text.Length, out int ls, out int ll)) return;
        var kids = new List<(int Start, int Length)>();
        int count = 0;
        bool ok = true;
        for (var c = l.FirstChild; c != null; c = c.NextSibling)
        {
            count++;
            if (!TrySpan(c.Span, ctx.Text.Length, out int ks, out int kl)) { ok = false; break; }
            kids.Add((ks, kl));
        }
        var dim = new Paint(ctx.Pal.Dim);
        var link = new Paint(ctx.Pal.Link, underline: true);
        if (!ok || kids.Count != count || kids.Count == 0)
        {
            ctx.Ops.Add((ls, ll, dim));
            return;
        }
        kids.Sort((a, b) => a.Start.CompareTo(b.Start));
        int cursor = ls;
        int end = ls + ll;
        foreach (var (ks, kl) in kids)
        {
            if (ks > cursor) ctx.Ops.Add((cursor, ks - cursor, dim));
            ctx.Ops.Add((ks, kl, link));
            cursor = ks + kl;
        }
        if (cursor < end) ctx.Ops.Add((cursor, end - cursor, dim));
    }

    // MARK: - HTML (tags/comments confined to HTML nodes)

    private static readonly Regex HtmlRe = new(
        "(<!--[\\s\\S]*?-->)|(</?[A-Za-z][A-Za-z0-9]*(?:\\s[^<>\\n]*)?/?>)",
        RegexOptions.Compiled);
    private static readonly Regex TagNameRe = new(
        "^(</?)([A-Za-z][A-Za-z0-9]*)", RegexOptions.Compiled);
    private static readonly Regex AttrStrRe = new("\"[^\"]*\"", RegexOptions.Compiled);

    private static void PaintHtml(int rs, int rl, Ctx ctx)
    {
        string text = ctx.Text.Substring(rs, rl);
        foreach (Match m in HtmlRe.Matches(text))
        {
            int ms = rs + m.Index;
            if (m.Groups[1].Success)
                ctx.Ops.Add((ms, m.Length, new Paint(ctx.Pal.Dim, italic: true)));
            else
                PaintHtmlTag(ms, m.Length, ctx);
        }
    }

    private static void PaintHtmlTag(int rs, int rl, Ctx ctx)
    {
        string s = ctx.Text.Substring(rs, rl);
        ctx.Ops.Add((rs, rl, new Paint(ctx.Pal.Dim)));
        var m = TagNameRe.Match(s);
        if (!m.Success) return;
        var g = m.Groups[2];
        ctx.Ops.Add((rs + g.Index, g.Length, new Paint(ctx.Pal.Marker, bold: true)));
        foreach (Match sm in AttrStrRe.Matches(s))
            ctx.Ops.Add((rs + sm.Index, sm.Length, new Paint(ctx.Pal.Code)));
    }

    // MARK: - Tables: delimiter rows + structural pipes (code/link spans excluded)

    private static void PaintTables(Ctx ctx)
    {
        foreach (var (ts, tl, covered) in ctx.Tables)
        {
            foreach (var (lstart, line) in LinesIn(ts, tl, ctx))
            {
                int llen = line.Length;
                bool inRow = covered.Any(c =>
                    Math.Max(c.Start, lstart) < Math.Min(c.Start + c.Length, lstart + llen));
                if (!inRow)
                {
                    if (IsDelimiterLine(line)) ctx.Ops.Add((lstart, llen, new Paint(ctx.Pal.Dim)));
                    continue;
                }
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] != '|') continue;
                    int abs = lstart + i;
                    if (!ctx.Guarded.Any(g => abs >= g.Start && abs < g.Start + g.Length))
                        ctx.Ops.Add((abs, 1, new Paint(ctx.Pal.Dim)));
                }
            }
        }
    }

    private static bool IsDelimiterLine(string line)
    {
        string t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        var cells = t.Split('|');
        if (cells.Length == 0) return false;
        foreach (var cell in cells)
        {
            string k = cell.Trim();
            if (k.Length == 0) return false;
            if (k.StartsWith(':')) k = k[1..];
            if (k.EndsWith(':')) k = k[..^1];
            if (k.Length == 0 || k.Any(ch => ch != '-')) return false;
        }
        return true;
    }
}
