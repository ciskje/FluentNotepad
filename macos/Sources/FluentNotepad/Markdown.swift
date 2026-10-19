import Foundation
import Markdown
import UniformTypeIdentifiers

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
            // Footnotes + definition lists (Markdig renders them on Windows;
            // swift-markdown/cmark has no equivalent): text-level expansion
            // first, so the AST walk below needs no changes.
            body = childrenHTML(Document(parsing: expandNotes(markdown)), baseDir: baseDir)
        }
        let css = dark ? darkCss(fontSize) : lightCss(fontSize)
        let t = title.map { "<title>\(esc($0))</title>" } ?? ""
        return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='\(dark ? "dark" : "light")'>"
            + t + "<style>\(css)</style></head><body>\(body)</body></html>"
    }

    // MARK: - Footnotes + definition lists (no cmark equivalent)

    /// Text-level expansion of Markdig footnote/definition-list syntax.
    /// Removed lines become blanks (never deletions) so data-src-line
    /// numbers still match the source. Fast path: no `[^` and no `:`-led
    /// line means the text returns untouched.
    static func expandNotes(_ markdown: String) -> String {
        guard markdown.contains("[^") || markdown.contains("\n:") || markdown.hasPrefix(":") else {
            return markdown
        }
        var lines = markdown.components(separatedBy: "\n")
        var inFence = false
        // 1. Footnote definitions: `[^id]: ...` + indented continuations.
        var defs: [(id: String, content: [String])] = []
        var i = 0
        while i < lines.count {
            let ln = lines[i]
            if ln.trimmingCharacters(in: .whitespaces).hasPrefix("```") { inFence.toggle() }
            if !inFence, let (id, first) = footnoteDef(ln) {
                var content = [first]
                var j = i + 1
                while j < lines.count,
                    lines[j].trimmingCharacters(in: .whitespaces).isEmpty
                        || lines[j].hasPrefix("    ") || lines[j].hasPrefix("\t")
                {
                    var c = lines[j]
                    if c.hasPrefix("    ") { c = String(c.dropFirst(4)) }
                    else if c.hasPrefix("\t") { c = String(c.dropFirst()) }
                    content.append(c)
                    lines[j] = ""
                    j += 1
                }
                defs.append((id, content))
                lines[i] = ""
                i = j
                continue
            }
            i += 1
        }
        guard !defs.isEmpty else { return joinDeflists(lines) }
        var order: [String] = []
        var num: [String: Int] = [:]
        func ref(_ id: String) -> String {
            if let n = num[id] { return String(n) }
            order.append(id)
            num[id] = order.count
            return String(order.count)
        }
        // 2. References `[^id]` -> sup links (outside fences/code spans).
        inFence = false
        for k in lines.indices {
            let ln = lines[k]
            if ln.trimmingCharacters(in: .whitespaces).hasPrefix("```") { inFence.toggle(); continue }
            if inFence || !ln.contains("[^") { continue }
            lines[k] = expandRefs(ln, defs: defs, ref: ref)
        }
        for d in defs.indices {
            defs[d].content = defs[d].content.map { expandRefs($0, defs: defs, ref: ref) }
        }
        // 3. Footnotes section (appended: shifts nothing).
        if !order.isEmpty {
            var out = "<div class=\"footnotes\">\n<hr />\n<ol>\n"
            for id in order {
                let content = defs.first { $0.id == id }?.content.joined(separator: "\n") ?? ""
                let html = childrenHTML(Document(parsing: content), baseDir: nil)
                out += "<li id=\"fn:\(esc(id))\">\(html)<a href=\"#fnref:\(esc(id))\" class=\"footnote-backref\">&#8617;</a></li>\n"
            }
            lines.append("")
            lines.append(contentsOf: out.components(separatedBy: "\n"))
            lines.append("</ol>\n</div>")
        }
        return joinDeflists(lines)
    }

    private static func footnoteDef(_ line: String) -> (String, String)? {
        guard line.hasPrefix("[^"), let end = line.firstIndex(of: "]") else { return nil }
        let id = String(line[line.index(line.startIndex, offsetBy: 2)..<end])
        if id.isEmpty || id.contains("[") || id.contains("]") { return nil }
        let rest = line[line.index(after: end)...]
        guard rest.hasPrefix(":") else { return nil }
        var content = rest.dropFirst()
        if content.hasPrefix(" ") { content = content.dropFirst() }
        else if content.hasPrefix("\t") { content = content.dropFirst() }
        return (id, String(content))
    }

    /// Rewrites `[^id]` outside backtick spans; unknown ids stay literal.
    private static func expandRefs(_ line: String, defs: [(id: String, content: [String])],
        ref: (String) -> String) -> String
    {
        let ids = Set(defs.map(\.id))
        var out = ""
        var k = line.startIndex
        while k < line.endIndex {
            let c = line[k]
            if c == "`" {
                // Skip the whole code span (matched backtick run).
                var run = 0
                var m = k
                while m < line.endIndex, line[m] == "`" { run += 1; m = line.index(after: m) }
                var close = m
                var found = false
                while close < line.endIndex {
                    if line[close] == "`" {
                        var r2 = 0
                        var m2 = close
                        while m2 < line.endIndex, line[m2] == "`" { r2 += 1; m2 = line.index(after: m2) }
                        if r2 == run { close = m2; found = true; break }
                        close = m2
                    } else {
                        close = line.index(after: close)
                    }
                }
                out += String(line[k..<(found ? close : m)])
                k = found ? close : m
                continue
            }
            if c == "[", line[k...].hasPrefix("[^"),
                let rb = line[k...].firstIndex(of: "]"),
                !line[k...].contains("\n")
            {
                let id = String(line[line.index(k, offsetBy: 2)..<rb])
                if ids.contains(id) {
                    let n = ref(id)
                    out += "<sup id=\"fnref:\(esc(id))\"><a href=\"#fn:\(esc(id))\">\(n)</a></sup>"
                    k = line.index(after: rb)
                    continue
                }
            }
            out.append(c)
            k = line.index(after: k)
        }
        return out
    }

    /// `Term` + `: def` lines -> single-line `<dl>` block (padded with blanks
    /// to preserve line numbers). Outside fenced code only.
    private static func joinDeflists(_ lines: [String]) -> String {
        var lines = lines
        var inFence = false
        var i = 0
        while i < lines.count {
            let ln = lines[i]
            if ln.trimmingCharacters(in: .whitespaces).hasPrefix("```") { inFence.toggle(); i += 1; continue }
            if inFence { i += 1; continue }
            if isDefLine(ln), i > 0, isTermLine(lines[i - 1]) {
                // Collect terms above (consecutive plain lines).
                var t = i - 1
                while t > 0, isTermLine(lines[t - 1]) { t -= 1 }
                let terms = Array(lines[t..<i])
                // Collect def lines.
                var j = i
                var defs: [String] = []
                while j < lines.count, isDefLine(lines[j]) {
                    var d = String(lines[j].dropFirst())
                    if d.hasPrefix(" ") { d = String(d.dropFirst()) }
                    defs.append(d)
                    lines[j] = ""
                    j += 1
                }
                var dl = "<dl>"
                for term in terms { dl += "<dt>\(inlineHTML(term))</dt>" }
                for d in defs { dl += "<dd>\(inlineHTML(d))</dd>" }
                dl += "</dl>"
                for k in t..<i { lines[k] = "" }
                lines[t] = dl
                i = j
                continue
            }
            i += 1
        }
        return lines.joined(separator: "\n")
    }

    private static func isDefLine(_ s: String) -> Bool {
        s == ":" || s.hasPrefix(": ") || s.hasPrefix(":\t")
    }

    private static func isTermLine(_ s: String) -> Bool {
        let t = s.trimmingCharacters(in: .whitespaces)
        if t.isEmpty { return false }
        if t.hasPrefix("```") || t.hasPrefix(">") || t.hasPrefix("#") || t.hasPrefix("<") { return false }
        if t.hasPrefix("- ") || t.hasPrefix("* ") || t.hasPrefix("+ ") { return false }
        if t.range(of: #"^\d+[.)] "#, options: .regularExpression) != nil { return false }
        if isDefLine(t) { return false }
        if t.hasPrefix("|") { return false }
        return true
    }

    /// Markdown span -> inline HTML (single `<p>` unwrapped).
    private static func inlineHTML(_ s: String) -> String {
        let h = childrenHTML(Document(parsing: s), baseDir: nil)
        if h.hasPrefix("<p"), let open = h.firstIndex(of: ">"), h.hasSuffix("</p>") {
            return String(h[h.index(after: open)..<h.index(h.endIndex, offsetBy: -4)])
        }
        return h
    }

    // MARK: - Walker

    private static func childrenHTML(_ markup: Markup, baseDir: String?) -> String {
        var out = ""
        for child in markup.children { out += node(child, baseDir: baseDir) }
        return out
    }

    private static func node(_ m: Markup, baseDir: String?) -> String {
        switch m {
        case let h as Heading:
            return "<h\(h.level)\(src(h))>\(childrenHTML(h, baseDir: baseDir))</h\(h.level)>"
        case let p as Paragraph:
            return "<p\(src(p))>\(childrenHTML(p, baseDir: baseDir))</p>"
        case let b as BlockQuote:
            return "<blockquote\(src(b))>\(childrenHTML(b, baseDir: baseDir))</blockquote>"
        case let c as CodeBlock:
            let lang = c.language.map { " class='language-\(esc($0))'" } ?? ""
            return "<pre\(src(c))><code\(lang)>\(esc(c.code))</code></pre>"
        case let h as HTMLBlock:
            return h.rawHTML
        case is ThematicBreak:
            return "<hr\(src(m))>"
        case let l as OrderedList:
            return "<ol>\(childrenHTML(l, baseDir: baseDir))</ol>"
        case let l as UnorderedList:
            return "<ul>\(childrenHTML(l, baseDir: baseDir))</ul>"
        case let i as ListItem:
            let box: String
            if let cb = i.checkbox {
                box = "<input type='checkbox' disabled\(cb == .checked ? " checked" : "")> "
            } else { box = "" }
            // Tight lists: inline children directly; loose lists keep <p>.
            let inner = i.children.allSatisfy { $0 is Paragraph }
                ? i.children.map { childrenHTML($0, baseDir: baseDir) }.joined()
                : childrenHTML(i, baseDir: baseDir)
            return "<li\(src(i))>\(box)\(inner)</li>"
        case let t as Table:
            var head = "", rows = ""
            for child in t.children {
                if let h = child as? Table.Head { head = tableRows(h, baseDir: baseDir, isHeader: true) }
                else if let b = child as? Table.Body { rows = tableRows(b, baseDir: baseDir, isHeader: false) }
            }
            return "<table\(src(t))><thead>\(head)</thead><tbody>\(rows)</tbody></table>"
        case let t as Text:
            return esc(t.string)
        case let e as Emphasis:
            return "<em>\(childrenHTML(e, baseDir: baseDir))</em>"
        case let s as Strong:
            return "<strong>\(childrenHTML(s, baseDir: baseDir))</strong>"
        case let s as Strikethrough:
            return "<del>\(childrenHTML(s, baseDir: baseDir))</del>"
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
            return "<a href=\"\(esc(dest))\"\(title)>\(childrenHTML(l, baseDir: baseDir))</a>"
        case let i as Image:
            let alt = i.plainText
            let src = resolveSrc(i.source ?? "", baseDir: baseDir)
            let onerror = "this.style.display='none';var s=document.createElement('span');s.className='img-missing';s.textContent='\\uD83D\\uDDBC '+this.alt+'\(escJS(L("imgMissing")))';this.after(s);"
            return "<img src=\"\(esc(src))\" alt=\"\(esc(alt))\" onerror=\"\(onerror)\">"
        default:
            return childrenHTML(m, baseDir: baseDir)
        }
    }

    // Source line of a parsed node for click-to-source ("" when unknown).
    private static func src(_ m: Markup) -> String {
        if let line = m.range?.lowerBound.line {
            return " data-src-line=\"\(line)\""
        }
        return ""
    }

    // Table rows with header-ness passed down (no parent walk per cell).
    private static func tableRows(_ m: Markup, baseDir: String?, isHeader: Bool) -> String {
        var out = ""
        for row in m.children {
            var cells = ""
            for cell in row.children {
                let tag = isHeader ? "th" : "td"
                cells += "<\(tag)>\(childrenHTML(cell, baseDir: baseDir))</\(tag)>"
            }
            out += "<tr\(src(row))>\(cells)</tr>"
        }
        return out
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
        let mime = UTType(filenameExtension: (full as NSString).pathExtension.lowercased())?.preferredMIMEType
            ?? "application/octet-stream"
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
        dl{margin:0 0 10px}dt{font-weight:700}dd{margin:0 0 8px 20px}
        .footnotes{font-size:.9em;margin-top:16px}.footnote-backref{text-decoration:none}
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
