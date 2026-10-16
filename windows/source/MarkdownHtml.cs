using System.Text.RegularExpressions;
using Markdig;

namespace FluentNotepad;

/// <summary>
/// Markdown -&gt; HTML for WebView2 (Markdig, no rendering code of our own).
/// Local images -&gt; data-URI (no virtual server), remote/data-URI natively,
/// SVG supported by the browser, missing files -&gt; graceful placeholder via onerror.
/// Query (?raw=true) and fragments (#...) ignored in file lookup.
/// </summary>
public static class MarkdownHtml
{
    private static readonly MarkdownPipeline _pipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        // Single-dollar math swallows "$ ... | ... $" across table cells
        // (e.g. "| Go cap $ | AA $/task | ..."), merging cells into a
        // "<span class='math'>". No MathJax is loaded, so math never rendered
        // anyway: drop it and keep dollars literal.
        builder.Extensions.RemoveAll(e => (e.GetType().FullName ?? "").Contains("Mathematic"));
        return builder.Build();
    }

    private const long MaxImageBytes = 20L * 1024 * 1024;

    public static string Build(string markdown, string? baseDir, bool dark, double fontSize, string? title = null)
    {
        string body = string.IsNullOrWhiteSpace(markdown)
            ? "<p class='empty-hint'>" + MainWindow.Str("EmptyHint") + "</p>"
            : Markdig.Markdown.ToHtml(markdown, _pipeline);
        body = ProcessImages(body, baseDir);
        string css = dark ? DarkCss(fontSize) : LightCss(fontSize);
        // 'busy' class on the root: the page is born "busy" (ring cursor) and the
        // window clears it on NavigationCompleted. The web content cursor is owned by
        // WebView2, not XAML: ProtectedCursor alone would not cover the page area.
        // <title>: the browser print dialog prints headers (title + URL) by default.
        // NavigateToString has no URL, so without a title the header reads "about:blank".
        string head = "<!DOCTYPE html><html class='busy'><head><meta charset='utf-8'>" +
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

    // Visual Studio 2022 theme palette: blue keywords/links, orange strings (code),
    // gold functions (bold), purple control flow (italic), editor panel surfaces.
    private static string BaseCss(double fs) => $@"
html.busy,html.busy *{{cursor:wait!important}}
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
@media print{{html{{background:#FFFFFF!important;color-scheme:light!important}}*{{print-color-adjust:exact!important;-webkit-print-color-adjust:exact!important}}body{{margin:0;background:#FFFFFF!important;color:#1E1E1E!important}}h1,h2{{border-color:#E5E5E5!important}}h1,h2,h3,h4,h5{{color:#0F6CBD!important}}h6{{color:#808080!important}}a{{color:#0F6CBD!important}}strong{{color:#A31515!important}}em{{color:#7A3E9D!important}}code{{background:#F3F3F3!important;color:#A31515!important}}pre{{background:#F6F6F6!important;border-color:#E5E5E5!important}}pre code{{color:#1E1E1E!important;background:none!important;border:0!important}}blockquote{{background:#F6F6F6!important;border-color:#0F6CBD!important;color:#075AA0!important}}th{{background:#F3F3F3!important;color:#0F6CBD!important}}th,td{{border-color:#D4D4D4!important}}tbody tr:nth-child(even){{background:#FAFAFA!important}}hr{{border-color:#E5E5E5!important}}}}";

    private static string LightCss(double fs) => BaseCss(fs) + @"
body{background:#FFFFFF;color:#1E1E1E}
h1,h2{border-color:#E5E5E5}h1,h2,h3,h4,h5{color:#0F6CBD}h6{color:#808080}
a{color:#0F6CBD}strong{color:#A31515}em{color:#7A3E9D}
code{background:#F3F3F3;color:#A31515}pre{background:#F6F6F6;border-color:#E5E5E5}pre code{color:#1E1E1E}
blockquote{background:#F6F6F6;border-color:#0F6CBD;color:#075AA0}
th{background:#F3F3F3;color:#0F6CBD}th,td{border-color:#D4D4D4}
tbody tr:nth-child(even){background:#FAFAFA}hr{border-color:#E5E5E5}";

    private static string DarkCss(double fs) => BaseCss(fs) + @"
body{background:#1E1E1E;color:#D4D4D4}
h1,h2{border-color:#3F3F46}h1,h2,h3,h4,h5{color:#569CD6}h6{color:#808080}
a{color:#4FC1FF}strong{color:#DCDCAA}em{color:#C586C0}
code{background:#2D2D30;color:#CE9178}pre{background:#252526;border-color:#3F3F46}pre code{color:#D4D4D4}
blockquote{background:#252526;border-color:#569CD6;color:#9CDCFE}
th{background:#2D2D30;color:#569CD6}th,td{border-color:#3F3F46}
tbody tr:nth-child(even){background:#232326}hr{border-color:#3F3F46}";
}
