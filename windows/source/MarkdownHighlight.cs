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
    private static readonly MarkdownPipeline _pipeline = MarkdownShared.CreatePipeline();

    private readonly struct Paint
    {
        public readonly MarkdownShared.Rgb Fg;
        public readonly MarkdownShared.Rgb? Bg;
        public readonly bool Bold;
        public readonly bool Italic;
        public readonly bool Strike;
        public readonly bool Underline;

        public Paint(MarkdownShared.Rgb fg, MarkdownShared.Rgb? bg = null, bool bold = false, bool italic = false,
            bool strike = false, bool underline = false)
        {
            Fg = fg; Bg = bg; Bold = bold; Italic = italic; Strike = strike; Underline = underline;
        }
    }

    private sealed class Ctx
    {
        public readonly string Text;
        public readonly int[] Starts;
        public readonly string[] Lines;
        public readonly MarkdownShared.Palette Pal;
        public readonly List<(int Start, int Length, Paint Paint)> Ops = new();
        public readonly List<(int Start, int Length)> Guarded = new(); // code/link/image/html: table pipes skip these
        public readonly List<(int Start, int Length, List<(int Start, int Length)> Covered)> Tables = new();
        public int TableStart = -1; // current table span (tables never nest): rows accumulate into TableCovered
        public int TableLen;
        public readonly List<(int Start, int Length)> TableCovered = new();

        public Ctx(string text, MarkdownShared.Palette pal)
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
        Color baseFg, Color baseBg) =>
        TextPainter.Apply(editor, dark, fontSize, baseFg, baseBg, (parse, pal, ops) =>
        {
            if (!isMarkdown) return;
            MarkdownDocument md;
            try { md = Markdig.Markdown.Parse(parse, _pipeline); }
            catch { return; }
            var ctx = new Ctx(parse, pal);
            foreach (var b in md) PaintBlock(b, ctx);
            PaintTables(ctx);
            foreach (var (s, l, p) in ctx.Ops)
                ops.Add(new TextPainter.Span(s, l, p.Fg, p.Italic, p.Bold, p.Strike, p.Underline, p.Bg));
        });

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
                if (b is LeafBlock hb && hb.Inline != null) PaintInline(hb.Inline, ctx);
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
                { ctx.TableStart = tas; ctx.TableLen = tal; ctx.TableCovered.Clear(); }
                foreach (var c in ((ContainerBlock)b)) PaintBlock(c, ctx);
                if (ctx.TableStart >= 0)
                {
                    ctx.Tables.Add((ctx.TableStart, ctx.TableLen, new List<(int, int)>(ctx.TableCovered)));
                    ctx.TableStart = -1;
                }
                break;
            case TableRow row:
                if (row.IsHeader && TrySpan(b.Span, ctx.Text.Length, out int hrs, out int hrl))
                    ctx.Ops.Add((hrs, hrl, new Paint(pal.Heading, bold: true)));
                if (TrySpan(b.Span, ctx.Text.Length, out int rs, out int rl) && ctx.TableStart >= 0)
                    ctx.TableCovered.Add((rs, rl));
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

    private static void PaintLineMarkers(int rs, int rl, Ctx ctx, Func<char, bool> check, MarkdownShared.Rgb color)
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

    // Delimiter rows (`|:---|---:|:---:|`) get the dim tone: single compiled pattern.
    private static readonly Regex DelimRe = new(@"^\s*\|?(\s*:?-+:?\s*\|?)+\s*$", RegexOptions.Compiled);

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
                    if (DelimRe.IsMatch(line)) ctx.Ops.Add((lstart, llen, new Paint(ctx.Pal.Dim)));
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
}
