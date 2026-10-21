using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using System.Text.RegularExpressions;

namespace FluentNotepad;

/// <summary>
/// Markdown -&gt; HTML for WebView2 (Markdig, no rendering code of our own).
/// Local images -&gt; data-URI (no virtual server), remote/data-URI natively,
/// SVG supported by the browser, missing files -&gt; graceful placeholder via onerror.
/// Query (?raw=true) and fragments (#...) ignored in file lookup.
/// Click-to-source (like FluentNotepadMac): every rendered block carries
/// data-src-line (1-based), emitted during rendering by SrcLineRenderer.
/// </summary>
public static class MarkdownHtml
{
    private static readonly MarkdownPipeline _pipeline = MarkdownShared.CreatePipeline();

    private const long MaxImageBytes = 20L * 1024 * 1024;

    // Click-to-source: a custom HtmlRenderer that tags every rendered block with
    // data-src-line (1-based, like FluentNotepadMac) during rendering. Blocks are
    // pre-annotated from the AST; every block renderer emits WriteAttributes(obj)
    // mid-tag, so the anchor lands inside the emitted tag with zero post-processing
    // (no tag scan, no raw-HTML blanking: raw blocks render untouched, as before).
    // Tight list items and table cells render without <p>: their annotation is
    // simply never emitted, mirroring the output. Unknown containers get no anchor.
    private sealed class SrcLineRenderer : HtmlRenderer
    {
        public SrcLineRenderer(TextWriter w) : base(w) { }

        public static string Render(string markdown, MarkdownPipeline pipeline)
        {
            MarkdownDocument doc;
            try { doc = Markdown.Parse(markdown, pipeline); }
            catch { return Markdown.ToHtml(markdown, pipeline); }
            foreach (var b in doc) Annotate(b);
            try
            {
                using var writer = new StringWriter();
                var renderer = new SrcLineRenderer(writer);
                pipeline.Setup(renderer);
                renderer.Render(doc);
                writer.Flush();
                return writer.ToString();
            }
            catch { return Markdown.ToHtml(markdown, pipeline); }
        }

        private static void Annotate(Block block)
        {
            switch (block)
            {
                case ParagraphBlock:
                    // Tight list items and table cells render without <p>
                    if (block.Parent is TableCell) break;
                    if (block.Parent is ListItemBlock && block.Parent.Parent is ListBlock lb && !lb.IsLoose) break;
                    Tag(block);
                    break;
                case HeadingBlock:
                case QuoteBlock:
                case CodeBlock:
                case ThematicBreakBlock:
                case ListBlock:
                case ListItemBlock:
                case Table:
                case TableRow:
                    Tag(block);
                    break;
                default:
                    break; // HtmlBlock (raw, untouched) and anything unknown: no anchor
            }
            if (block is ContainerBlock cb)
                foreach (var c in cb) Annotate(c);
        }

        private static void Tag(Block block) =>
            HtmlAttributesExtensions.GetAttributes(block).AddProperty("data-src-line", (block.Line + 1).ToString());
    }

    public static string Build(string markdown, string? baseDir, bool dark, double fontSize, string? title = null, bool wrap = true)
    {
        string body = string.IsNullOrWhiteSpace(markdown)
            ? "<p class='empty-hint'>" + MainWindow.Str("EmptyHint") + "</p>"
            : SrcLineRenderer.Render(markdown, _pipeline);
        body = ProcessImages(body, baseDir);
        // Same "A capo automatico" as the editor: off = no line breaking in the
        // preview either, the page scrolls horizontally. Print always wraps.
        string css = (dark ? DarkCss(fontSize) : LightCss(fontSize)) +
            (wrap ? "" : "body{white-space:nowrap;word-wrap:normal}" +
                "@media print{body{white-space:normal;word-wrap:break-word}}");
        // <title>: the browser print dialog prints headers (title + URL) by default.
        // NavigateToString has no URL, so without a title the header reads "about:blank".
        string head = "<!DOCTYPE html><html><head><meta charset='utf-8'>" +
               "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>" +
               (title != null ? "<title>" + System.Net.WebUtility.HtmlEncode(title) + "</title>" : "") +
               "<style>" + css + "</style></head><body>" + body + "</body></html>";
        return head;
    }

    private static string ProcessImages(string html, string? baseDir)
    {
        return Regex.Replace(html, "<img\\s+([^>]*?)src=\"([^\"]+)\"",
            m => "<img " + InjectOnError(m.Groups[1].Value) + "src=\"" + ResolveSrc(m.Groups[2].Value, baseDir) + "\"",
            RegexOptions.IgnoreCase);
    }

    private static string InjectOnError(string attrs)
    {
        if (attrs.Contains("onerror")) return attrs;
        return attrs + "onerror=\"this.style.display='none';var s=document.createElement('span');s.className='img-missing';s.textContent='\\uD83D\\uDDBC '+(this.alt||'" + MainWindow.Str("ImgName") + "')+'" + MainWindow.Str("ImgMissing") + "';this.after(s);\" ";
    }

    private static string ResolveSrc(string src, string? baseDir)
    {
        var s = src.Trim();
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith('#'))
            return s;
        // Local path: strip query/fragments, resolve, embed as data-URI
        var clean = s;
        int q = clean.IndexOf('?'); int h = clean.IndexOf('#');
        int cut = q >= 0 && h >= 0 ? Math.Min(q, h) : q >= 0 ? q : h;
        if (cut >= 0) clean = clean[..cut];
        try { clean = Uri.UnescapeDataString(clean); } catch { }
        if (string.IsNullOrWhiteSpace(clean)) return src;
        try
        {
            string full = Path.IsPathRooted(clean) ? clean
                : baseDir != null ? Path.GetFullPath(Path.Combine(baseDir, clean)) : clean;
            if (!File.Exists(full)) return src;
            var bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return src;
            string mime = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch { return src; }
    }

    // Visual Studio 2022 theme palette (MarkdownShared): blue keywords/links,
    // orange strings (code), gold functions (bold), purple control flow (italic).
    // Print always uses the light tones (paper is white).
    private static string BaseCss(double fs)
    {
        var l = new MarkdownShared.Palette(false);
        return $@"
body{{font-family:'Cascadia Mono','Cascadia Code',Consolas,monospace;font-size:{fs:0}px;line-height:1.6;margin:0;padding:20px 28px;word-wrap:break-word}}
h1{{font-size:{fs * 1.9:0}px;border-bottom:2px solid;margin:18px 0 10px;padding-bottom:5px;font-weight:700}}
h2{{font-size:{fs * 1.55:0}px;border-bottom:1px solid;margin:16px 0 8px;padding-bottom:4px;font-weight:700}}
h3{{font-size:{fs * 1.3:0}px;margin:14px 0 6px;font-weight:600}}h4{{font-size:{fs * 1.15:0}px;margin:12px 0 6px;font-weight:600}}
h5{{font-size:{fs:0}px;margin:10px 0 6px;font-weight:600}}h6{{font-size:{fs * 0.9:0}px;margin:10px 0 6px;font-weight:600}}
p{{margin:0 0 10px}}a{{text-decoration:none}}a:hover{{text-decoration:underline}}
strong{{font-weight:700}}em{{font-style:italic}}
code{{font-family:'Cascadia Mono','Cascadia Code',Consolas,monospace;font-size:.88em;padding:1px 6px;border-radius:4px}}
pre{{padding:12px 14px;border-radius:8px;overflow:auto;border:1px solid}}pre code{{padding:0;background:none;border:0}}
blockquote{{margin:0 0 10px;padding:6px 6px 6px 14px;border-left:4px solid;border-radius:0 6px 6px 0}}
ul,ol{{margin:0 0 10px;padding-left:26px}}li{{margin:3px 0}}li input[type=checkbox]{{margin-right:6px}}
table{{border-collapse:collapse;margin:0 0 14px;font-size:.95em}}th,td{{border:1px solid;padding:6px 12px}}th{{font-weight:700}}
img{{max-width:100%;margin:8px 0}}.img-missing{{font-style:italic;opacity:.7}}
.empty-hint{{opacity:.6;font-style:italic;text-align:center;margin-top:40px}}
hr{{border:none;border-top:1px solid;margin:18px 0}}
@media print{{html{{background:{l.Body.Hex}!important;color-scheme:light!important}}*{{print-color-adjust:exact!important;-webkit-print-color-adjust:exact!important}}body{{margin:0;background:{l.Body.Hex}!important;color:{l.BodyText.Hex}!important}}h1,h2{{border-color:{l.PanelBorder.Hex}!important}}h1,h2,h3,h4,h5{{color:{l.Heading.Hex}!important}}h6{{color:{l.Dim.Hex}!important}}a{{color:{l.Link.Hex}!important}}strong{{color:{l.Strong.Hex}!important}}em{{color:{l.Emphasis.Hex}!important}}code{{background:{l.CodeBg.Hex}!important;color:{l.Code.Hex}!important}}pre{{background:{l.Panel.Hex}!important;border-color:{l.PanelBorder.Hex}!important}}pre code{{color:{l.BodyText.Hex}!important;background:none!important;border:0!important}}blockquote{{background:{l.Panel.Hex}!important;border-color:{l.Marker.Hex}!important;color:{l.Quote.Hex}!important}}th{{background:{l.CodeBg.Hex}!important;color:{l.Heading.Hex}!important}}th,td{{border-color:{l.CellBorder.Hex}!important}}tbody tr:nth-child(even){{background:{l.Zebra.Hex}!important}}hr{{border-color:{l.PanelBorder.Hex}!important}}}}";
    }

    private static string ThemeCss(MarkdownShared.Palette p) => $@"
body{{background:{p.Body.Hex};color:{p.BodyText.Hex}}}
h1,h2{{border-color:{p.PanelBorder.Hex}}}h1,h2,h3,h4,h5{{color:{p.Heading.Hex}}}h6{{color:{p.Dim.Hex}}}
a{{color:{p.Link.Hex}}}strong{{color:{p.Strong.Hex}}}em{{color:{p.Emphasis.Hex}}}
code{{background:{p.CodeBg.Hex};color:{p.Code.Hex}}}pre{{background:{p.Panel.Hex};border-color:{p.PanelBorder.Hex}}}pre code{{color:{p.BodyText.Hex}}}
blockquote{{background:{p.Panel.Hex};border-color:{p.Marker.Hex};color:{p.Quote.Hex}}}
th{{background:{p.CodeBg.Hex};color:{p.Heading.Hex}}}th,td{{border-color:{p.CellBorder.Hex}}}
tbody tr:nth-child(even){{background:{p.Zebra.Hex}}}hr{{border-color:{p.PanelBorder.Hex}}}";

    private static string LightCss(double fs) => BaseCss(fs) + ThemeCss(new MarkdownShared.Palette(false));

    private static string DarkCss(double fs) => BaseCss(fs) + ThemeCss(new MarkdownShared.Palette(true));
}
