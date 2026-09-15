using System.Text.RegularExpressions;
using Markdig;

namespace FluentNotepad;

/// <summary>
/// Markdown -&gt; HTML per WebView2 (Markdig, zero codice di rendering nostro).
/// Immagini locali -&gt; data-URI (niente server virtuale), remote/data-URI native,
/// SVG supportato dal browser, file mancanti -&gt; segnaposto elegante via onerror.
/// Query (?raw=true) e frammenti (#...) ignorati nella ricerca file.
/// </summary>
public static class MarkdownHtml
{
    private static readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private const long MaxImageBytes = 20L * 1024 * 1024;

    public static string Build(string markdown, string? baseDir, bool dark, double fontSize)
    {
        string body = Markdig.Markdown.ToHtml(markdown, _pipeline);
        body = ProcessImages(body, baseDir);
        string css = dark ? DarkCss(fontSize) : LightCss(fontSize);
        return "<!DOCTYPE html><html><head><meta charset='utf-8'>" +
               "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>" +
               "<style>" + css + "</style></head><body>" + body + "</body></html>";
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
        return attrs + "onerror=\"this.style.display='none';var s=document.createElement('span');s.className='img-missing';s.textContent='\\uD83D\\uDDBC '+(this.alt||'Immagine')+' — non disponibile';this.after(s);\" ";
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
        // Percorso locale: togli query/frammenti, risolvi, incorpora come data-URI
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

    private static string BaseCss(double fs) => $@"
body{{font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;font-size:{fs:0}px;line-height:1.55;margin:0;padding:20px 24px;word-wrap:break-word}}
h1{{font-size:{fs * 1.8:0}px;border-bottom:1px solid;margin:16px 0 8px;padding-bottom:4px}}
h2{{font-size:{fs * 1.5:0}px;border-bottom:1px solid;margin:14px 0 8px;padding-bottom:4px}}
h3{{font-size:{fs * 1.3:0}px;margin:12px 0 6px}}h4{{font-size:{fs * 1.15:0}px;margin:10px 0 6px}}
h5{{font-size:{fs:0}px}}h6{{font-size:{fs * 0.9:0}px;color:gray}}
p{{margin:0 0 8px}}a{{text-decoration:none}}a:hover{{text-decoration:underline}}
code{{font-family:'Cascadia Mono',Consolas,monospace;font-size:.88em;padding:1px 5px;border-radius:4px}}
pre{{padding:10px;border-radius:6px;overflow:auto}}pre code{{padding:0;background:none}}
blockquote{{margin:0 0 8px;padding:4px 4px 4px 12px;border-left:3px solid}}
ul,ol{{margin:0 0 8px;padding-left:24px}}li{{margin:2px 0}}li input[type=checkbox]{{margin-right:6px}}
table{{border-collapse:collapse;margin:0 0 12px}}th,td{{border:1px solid;padding:4px 10px}}th{{font-weight:700}}
img{{max-width:100%;margin:6px 0}}.img-missing{{font-style:italic;opacity:.7}}
hr{{border:none;border-top:1px solid;margin:14px 0}}";

    private static string LightCss(double fs) => BaseCss(fs) + @"
body{background:#F9F9F9;color:#262626}h1,h2{border-color:#E1DFDD}
a{color:#0078D4}code{background:#F1F0EE}pre{background:#F1F0EE;border:1px solid #E1DFDD}
blockquote{background:#F7F7F7;border-color:#C8C6C4}th{background:#F3F3F3}th,td{border-color:#E1DFDD}hr{border-color:#E1DFDD}";

    private static string DarkCss(double fs) => BaseCss(fs) + @"
body{background:#282828;color:#EAEAEA}h1,h2{border-color:#3A3A3A}
a{color:#60CDFF}code{background:#262626}pre{background:#262626;border:1px solid #3A3A3A}
blockquote{background:#2A2A2A;border-color:#605E5C}th{background:#2D2D2D}th,td{border-color:#3A3A3A}hr{border-color:#3A3A3A}";
}
