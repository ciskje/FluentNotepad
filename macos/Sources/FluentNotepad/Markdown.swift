import Foundation
import Markdown

/// Markdown -> HTML for WKWebView. Parsing is Apple's cmark-gfm (swift-markdown);
/// only rendering is ours: VS-style palette + data-URI local images + graceful
/// placeholders, mirroring the Windows app's MarkdownHtml.cs.
enum MarkdownHTML {
    static let maxImageBytes = 20 * 1024 * 1024

    static func build(
        _ markdown: String, baseDir: String?, dark: Bool, fontSize: Double, title: String?
    ) -> String {
        let body: String
        if markdown.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            body = "<p class='empty-hint'>\(esc(L("emptyHint")))</p>"
        } else {
            body = render(Document(parsing: markdown), baseDir: baseDir)
        }
        let css = dark ? darkCss(fontSize) : lightCss(fontSize)
        let t = title.map { "<title>\(esc($0))</title>" } ?? ""
        return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='\(dark ? "dark" : "light")'>"
            + t + "<style>\(css)</style></head><body>\(body)</body></html>"
    }

    // MARK: - Walker

    private static func render(_ markup: Markup, baseDir: String?) -> String {
        var out = ""
        for child in markup.children { out += node(child, baseDir: baseDir) }
        return out
    }

    private static func inline(_ markup: Markup, baseDir: String?) -> String {
        var out = ""
        for child in markup.children { out += node(child, baseDir: baseDir) }
        return out
    }

    private static func node(_ m: Markup, baseDir: String?) -> String {
        switch m {
        case let h as Heading:
            return "<h\(h.level)\(src(h))>\(inline(h, baseDir: baseDir))</h\(h.level)>"
        case let p as Paragraph:
            return "<p\(src(p))>\(inline(p, baseDir: baseDir))</p>"
        case let b as BlockQuote:
            return "<blockquote\(src(b))>\(render(b, baseDir: baseDir))</blockquote>"
        case let c as CodeBlock:
            let lang = c.language.map { " class='language-\(esc($0))'" } ?? ""
            return "<pre\(src(c))><code\(lang)>\(esc(c.code))</code></pre>"
        case let h as HTMLBlock:
            return h.rawHTML
        case is ThematicBreak:
            return "<hr\(src(m))>"
        case let l as OrderedList:
            return "<ol>\(render(l, baseDir: baseDir))</ol>"
        case let l as UnorderedList:
            return "<ul>\(render(l, baseDir: baseDir))</ul>"
        case let i as ListItem:
            let box: String
            if let cb = i.checkbox {
                box = "<input type='checkbox' disabled\(cb == .checked ? " checked" : "")> "
            } else { box = "" }
            // Tight lists: inline children directly; loose lists keep <p>.
            let inner = i.children.allSatisfy { $0 is Paragraph }
                ? i.children.map { inline($0, baseDir: baseDir) }.joined()
                : render(i, baseDir: baseDir)
            return "<li\(src(i))>\(box)\(inner)</li>"
        case let t as Table:
            var head = "", rows = ""
            for child in t.children {
                if child is Table.Head { head = render(child, baseDir: baseDir) }
                if child is Table.Body { rows = render(child, baseDir: baseDir) }
            }
            return "<table\(src(t))><thead>\(head)</thead><tbody>\(rows)</tbody></table>"
        case let r as Table.Row:
            return "<tr\(src(r))>\(render(r, baseDir: baseDir))</tr>"
        case let c as Table.Cell:
            return cell(c, baseDir: baseDir)
        case let t as Text:
            return esc(t.string)
        case let e as Emphasis:
            return "<em>\(inline(e, baseDir: baseDir))</em>"
        case let s as Strong:
            return "<strong>\(inline(s, baseDir: baseDir))</strong>"
        case let s as Strikethrough:
            return "<del>\(inline(s, baseDir: baseDir))</del>"
        case let c as InlineCode:
            return "<code>\(esc(c.code))</code>"
        case let h as InlineHTML:
            return h.rawHTML
        case is LineBreak:
            return "<br>"
        case is SoftBreak:
            return "\n"
        case let l as Link:
            let dest = l.destination ?? "#"
            let title = l.title.map { " title=\"\(esc($0))\"" } ?? ""
            return "<a href=\"\(esc(dest))\"\(title)>\(inline(l, baseDir: baseDir))</a>"
        case let i as Image:
            let alt = i.plainText
            let src = resolveSrc(i.source ?? "", baseDir: baseDir)
            let onerror = "this.style.display='none';var s=document.createElement('span');s.className='img-missing';s.textContent='\\uD83D\\uDDBC '+this.alt+'\(escJS(L("imgMissing")))';this.after(s);"
            return "<img src=\"\(esc(src))\" alt=\"\(esc(alt))\" onerror=\"\(onerror)\">"
        default:
            return render(m, baseDir: baseDir)
        }
    }

    // Source line of a parsed node for click-to-source ("" when unknown).
    private static func src(_ m: Markup) -> String {
        if let line = m.range?.lowerBound.line {
            return " data-src-line=\"\(line)\""
        }
        return ""
    }

    private static func cell(_ c: Table.Cell, baseDir: String?) -> String {
        // Header-ness: head cells are rows whose parent is a Table.Head.
        var p = c.parent
        if p is Table.Row { p = p?.parent }
        let tag = p is Table.Head ? "th" : "td"
        return "<\(tag)>\(inline(c, baseDir: baseDir))</\(tag)>"
    }

    // MARK: - Images (same rules as the Windows app)

    private static func resolveSrc(_ src: String, baseDir: String?) -> String {
        let s = src.trimmingCharacters(in: .whitespaces)
        let low = s.lowercased()
        if low.hasPrefix("data:") || low.hasPrefix("http://") || low.hasPrefix("https://")
            || low.hasPrefix("file://") || s.hasPrefix("#") { return s }
        var clean = s
        if let q = [clean.firstIndex(of: "?"), clean.firstIndex(of: "#")].compactMap({ $0 }).min() {
            clean = String(clean[..<q])
        }
        clean = clean.removingPercentEncoding ?? clean
        guard !clean.trimmingCharacters(in: .whitespaces).isEmpty else { return src }
        let full: String
        if (clean as NSString).isAbsolutePath { full = clean }
        else if let b = baseDir { full = (b as NSString).appendingPathComponent(clean) }
        else { return src }
        guard FileManager.default.fileExists(atPath: full),
            let bytes = try? Data(contentsOf: URL(fileURLWithPath: full)),
            !bytes.isEmpty, bytes.count <= maxImageBytes
        else { return src }
        let mime: String
        switch (full as NSString).pathExtension.lowercased() {
        case "png": mime = "image/png"
        case "jpg", "jpeg": mime = "image/jpeg"
        case "gif": mime = "image/gif"
        case "webp": mime = "image/webp"
        case "bmp": mime = "image/bmp"
        case "svg": mime = "image/svg+xml"
        default: mime = "application/octet-stream"
        }
        return "data:\(mime);base64,\(bytes.base64EncodedString())"
    }

    // MARK: - Escaping

    private static func esc(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }

    private static func escJS(_ s: String) -> String {
        s.replacingOccurrences(of: "'", with: "\\'")
    }

    // MARK: - CSS (Visual Studio 2022 palette, system fonts on macOS)

    private static func baseCss(_ fs: Double) -> String {
        """
        body{font-family:-apple-system,'SF Mono',Menlo,Consolas,monospace;font-size:\(Int(fs))px;line-height:1.6;margin:0;padding:20px 28px;word-wrap:break-word}
        h1{font-size:\(Int(fs * 1.9))px;border-bottom:2px solid;margin:18px 0 10px;padding-bottom:5px;font-weight:700}
        h2{font-size:\(Int(fs * 1.55))px;border-bottom:1px solid;margin:16px 0 8px;padding-bottom:4px;font-weight:700}
        h3{font-size:\(Int(fs * 1.3))px;margin:14px 0 6px;font-weight:600}h4{font-size:\(Int(fs * 1.15))px;margin:12px 0 6px;font-weight:600}
        h5{font-size:\(Int(fs))px;margin:10px 0 6px;font-weight:600}h6{font-size:\(Int(fs * 0.9))px;margin:10px 0 6px;font-weight:600}
        p{margin:0 0 10px}a{text-decoration:none}a:hover{text-decoration:underline}
        strong{font-weight:700}em{font-style:italic}del{opacity:.75}
        code{font-family:'SF Mono',Menlo,Consolas,monospace;font-size:.88em;padding:1px 6px;border-radius:4px}
        pre{padding:12px 14px;border-radius:8px;overflow:auto;border:1px solid}pre code{padding:0;background:none;border:0}
        blockquote{margin:0 0 10px;padding:6px 6px 6px 14px;border-left:4px solid;border-radius:0 6px 6px 0}
        ul,ol{margin:0 0 10px;padding-left:26px}li{margin:3px 0}li input[type=checkbox]{margin-right:6px}
        table{border-collapse:collapse;margin:0 0 14px;font-size:.95em}th,td{border:1px solid;padding:6px 12px}th{font-weight:700}
        img{max-width:100%;margin:8px 0}.img-missing{font-style:italic;opacity:.7}
        .empty-hint{opacity:.6;font-style:italic;text-align:center;margin-top:40px}
        hr{border:none;border-top:1px solid;margin:18px 0}
        @media print{html{background:#FFFFFF!important;color-scheme:light!important}*{print-color-adjust:exact!important;-webkit-print-color-adjust:exact!important}body{margin:0;background:#FFFFFF!important;color:#1E1E1E!important}h1,h2{border-color:#E5E5E5!important}h1,h2,h3,h4,h5{color:#0F6CBD!important}h6{color:#808080!important}a{color:#0F6CBD!important}strong{color:#A31515!important}em{color:#7A3E9D!important}code{background:#F3F3F3!important;color:#A31515!important}pre{background:#F6F6F6!important;border-color:#E5E5E5!important}pre code{color:#1E1E1E!important;background:none!important;border:0!important}blockquote{background:#F6F6F6!important;border-color:#0F6CBD!important;color:#075AA0!important}th{background:#F3F3F3!important;color:#0F6CBD!important}th,td{border-color:#D4D4D4!important}tbody tr:nth-child(even){background:#FAFAFA!important}hr{border-color:#E5E5E5!important}}
        """
    }

    private static func lightCss(_ fs: Double) -> String {
        baseCss(fs)
            + """
            body{background:#FFFFFF;color:#1E1E1E}
            h1,h2{border-color:#E5E5E5}h1,h2,h3,h4,h5{color:#0F6CBD}h6{color:#808080}
            a{color:#0F6CBD}strong{color:#A31515}em{color:#7A3E9D}
            code{background:#F3F3F3;color:#A31515}pre{background:#F6F6F6;border-color:#E5E5E5}pre code{color:#1E1E1E}
            blockquote{background:#F6F6F6;border-color:#0F6CBD;color:#075AA0}
            th{background:#F3F3F3;color:#0F6CBD}th,td{border-color:#D4D4D4}
            tbody tr:nth-child(even){background:#FAFAFA}hr{border-color:#E5E5E5}
            """
    }

    private static func darkCss(_ fs: Double) -> String {
        baseCss(fs)
            + """
            body{background:#1E1E1E;color:#D4D4D4}
            h1,h2{border-color:#3F3F46}h1,h2,h3,h4,h5{color:#569CD6}h6{color:#808080}
            a{color:#4FC1FF}strong{color:#DCDCAA}em{color:#C586C0}
            code{background:#2D2D30;color:#CE9178}pre{background:#252526;border-color:#3F3F46}pre code{color:#D4D4D4}
            blockquote{background:#252526;border-color:#569CD6;color:#9CDCFE}
            th{background:#2D2D30;color:#569CD6}th,td{border-color:#3F3F46}
            tbody tr:nth-child(even){background:#232326}hr{border-color:#3F3F46}
            """
    }
}

// swift-markdown niceties missing from the base API.
private extension Image {
    var plainText: String {
        var t = ""
        for c in children { if let x = c as? Text { t += x.string } }
        return t
    }
}
