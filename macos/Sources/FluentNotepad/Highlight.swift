import AppKit
import Markdown

/// Parser-driven Markdown highlighting (swift-markdown/cmark-gfm, already a
/// dependency): only real syntax nodes are painted, so `_` in `IQ3_S`, `|` in
/// code spans or `*` across table cells can never leak color. Visual attributes
/// only; the string stays plain text. Later ops overwrite earlier ones wholesale
/// (no attribute merging). Skipped for non-markdown, huge docs and IME composition.
/// Undo is disabled while painting.
enum SyntaxHighlight {
    static let maxChars = 300_000

    struct Paint {
        var fg: NSColor
        var bg: NSColor?
        var bold = false
        var italic = false
        var strike = false
        var underline = false
    }

    static func apply(to tv: NSTextView, baseFont: NSFont, dark: Bool, markdown: Bool) {
        guard let storage = tv.textStorage, !tv.hasMarkedText() else { return }
        let text = tv.string
        let ns = text as NSString
        let full = NSRange(location: 0, length: ns.length)
        tv.undoManager?.disableUndoRegistration()
        storage.beginEditing()
        storage.setAttributes([.font: baseFont, .foregroundColor: NSColor.textColor], range: full)
        if markdown, !text.isEmpty, text.count <= maxChars {
            let ctx = Ctx(text: text, pal: Palette(dark: dark), baseFont: baseFont)
            walk(Document(parsing: text), ctx)
            for (r, p) in ctx.ops {
                let rr = NSIntersectionRange(r, full)
                if rr.length > 0 { storage.setAttributes(ctx.dict(p), range: rr) }
            }
        }
        storage.endEditing()
        tv.undoManager?.enableUndoRegistration()
        // Newly typed characters start neutral; the next keystroke re-highlights.
        tv.typingAttributes = [.font: baseFont, .foregroundColor: NSColor.textColor]
    }

    // MARK: - Source mapping (cmark: 1-based line, 1-based UTF-8-byte column)

    final class LineMap {
        private(set) var starts: [Int] = []
        private(set) var lines: [String] = []
        init(_ text: String) {
            let ns = text as NSString
            var loc = 0
            while loc < ns.length {
                let lr = ns.lineRange(for: NSRange(location: loc, length: 0))
                if lr.length == 0 { break }
                starts.append(lr.location)
                var line = ns.substring(with: lr)
                if line.hasSuffix("\n") { line.removeLast() }
                if line.hasSuffix("\r") { line.removeLast() }
                lines.append(line)
                loc = lr.location + lr.length
            }
        }
        /// UTF-16 offset for (line, column); clamps (never nil-traps on \r quirks).
        func offset(line: Int, column: Int) -> Int? {
            guard line >= 1, line <= lines.count, column >= 1 else { return nil }
            let s = lines[line - 1]
            var bytes = 0, utf16 = 0
            let want = column - 1
            for scalar in s.unicodeScalars {
                if bytes >= want { break }
                bytes += scalar.utf8.count
                utf16 += scalar.utf16.count
            }
            return starts[line - 1] + utf16
        }
        func nsRange(_ r: SourceRange) -> NSRange? {
            guard let a = offset(line: r.lowerBound.line, column: r.lowerBound.column),
                let b = offset(line: r.upperBound.line, column: r.upperBound.column),
                b >= a
            else { return nil }
            return NSRange(location: a, length: b - a)
        }
    }

    // MARK: - Walk context

    final class Ctx {
        let map: LineMap
        let ns: NSString
        let pal: Palette
        let baseFont: NSFont
        let boldFont: NSFont
        let italicFont: NSFont
        let boldItalicFont: NSFont
        var ops: [(NSRange, Paint)] = []
        var guarded: [NSRange] = [] // code/link/image/html: table pipes skip these
        init(text: String, pal: Palette, baseFont: NSFont) {
            self.map = LineMap(text)
            self.ns = text as NSString
            self.pal = pal
            self.baseFont = baseFont
            let fm = NSFontManager.shared
            self.boldFont = fm.convert(baseFont, toHaveTrait: .boldFontMask)
            self.italicFont = fm.convert(baseFont, toHaveTrait: .italicFontMask)
            self.boldItalicFont = fm.convert(baseFont, toHaveTrait: [.boldFontMask, .italicFontMask])
        }
        func dict(_ p: Paint) -> [NSAttributedString.Key: Any] {
            var d: [NSAttributedString.Key: Any] = [.foregroundColor: p.fg]
            if p.bold, p.italic { d[.font] = boldItalicFont }
            else if p.bold { d[.font] = boldFont }
            else if p.italic { d[.font] = italicFont }
            else { d[.font] = baseFont }
            if let bg = p.bg { d[.backgroundColor] = bg }
            if p.strike {
                d[.strikethroughStyle] = NSUnderlineStyle.single.rawValue
                d[.strikethroughColor] = p.fg
            }
            if p.underline {
                d[.underlineStyle] = NSUnderlineStyle.single.rawValue
                d[.underlineColor] = p.fg
            }
            return d
        }
        func paint(_ m: Markup, _ p: Paint) {
            if let r = m.range.flatMap({ map.nsRange($0) }) { ops.append((r, p)) }
        }
        func guardRange(_ m: Markup) {
            if let r = m.range.flatMap({ map.nsRange($0) }) { guarded.append(r) }
        }
    }

    // MARK: - Tree walk (parent ops first, children overwrite)

    static func walk(_ m: Markup, _ ctx: Ctx) {
        let pal = ctx.pal
        switch m {
        case is Heading:
            ctx.paint(m, Paint(fg: pal.heading, bold: true))
            for c in m.children { walk(c, ctx) }
        case is BlockQuote:
            ctx.paint(m, Paint(fg: pal.quote))
            paintLineMarkers(m, ctx, check: { $0 == ">" }, color: pal.marker)
            for c in m.children { walk(c, ctx) }
        case is CodeBlock:
            ctx.paint(m, Paint(fg: pal.code, bg: pal.codeBg))
            ctx.guardRange(m)
        case is ThematicBreak:
            ctx.paint(m, Paint(fg: pal.dim))
        case let i as ListItem:
            paintListMarker(i, ctx)
            for c in i.children { walk(c, ctx) }
        case let t as Table:
            for c in t.children { walk(c, ctx) }
            paintTable(t, ctx)
        case is Table.Head:
            ctx.paint(m, Paint(fg: pal.heading, bold: true))
            for c in m.children { walk(c, ctx) }
        case is Table.Row:
            for c in m.children { walk(c, ctx) }
        case is Emphasis:
            ctx.paint(m, Paint(fg: pal.emphasis, italic: true))
            for c in m.children { walk(c, ctx) }
        case is Strong:
            ctx.paint(m, Paint(fg: pal.strong, bold: true))
            for c in m.children { walk(c, ctx) }
        case is Strikethrough:
            ctx.paint(m, Paint(fg: pal.dim, strike: true))
            for c in m.children { walk(c, ctx) }
        case is InlineCode:
            ctx.paint(m, Paint(fg: pal.code, bg: pal.codeBg))
            ctx.guardRange(m)
        case let l as Link:
            paintLinkLike(l, ctx)
            ctx.guardRange(l)
            for c in l.children { walk(c, ctx) }
        case let i as Image:
            paintLinkLike(i, ctx)
            ctx.guardRange(i)
            for c in i.children { walk(c, ctx) }
        case is InlineHTML:
            if let r = m.range.flatMap({ ctx.map.nsRange($0) }) { paintHtmlTag(in: r, ctx) }
            ctx.guardRange(m)
        case is HTMLBlock:
            if let r = m.range.flatMap({ ctx.map.nsRange($0) }) { paintHtml(in: r, ctx) }
            ctx.guardRange(m)
        default:
            for c in m.children { walk(c, ctx) }
        }
    }

    // MARK: - Block markers (first non-space char scans, no regex)

    /// Paints the first `>` (or list marker) of each line intersecting the node.
    private static func paintLineMarkers(_ m: Markup, _ ctx: Ctx, check: (String) -> Bool, color: NSColor) {
        guard let r = m.range.flatMap({ ctx.map.nsRange($0) }) else { return }
        for (lstart, line) in lines(in: r, ctx) {
            var idx = line.startIndex
            while idx < line.endIndex && (line[idx] == " " || line[idx] == "\t") { idx = line.index(after: idx) }
            guard idx < line.endIndex, check(String(line[idx])) else { continue }
            let off = lstart + line.utf16.distance(from: line.startIndex, to: idx)
            ctx.ops.append((NSRange(location: off, length: 1), Paint(fg: color)))
        }
    }

    private static func lines(in r: NSRange, _ ctx: Ctx) -> [(Int, String)] {
        var out: [(Int, String)] = []
        for (i, lstart) in ctx.map.starts.enumerated() {
            let line = ctx.map.lines[i]
            let len = line.utf16.count
            if lstart < r.location + r.length && lstart + len > r.location {
                out.append((lstart, line))
            }
        }
        return out
    }

    private static func paintListMarker(_ i: ListItem, _ ctx: Ctx) {
        guard let r = i.range.flatMap({ ctx.map.nsRange($0) }),
            let first = lines(in: r, ctx).first
        else { return }
        let (lstart, line) = first
        var idx = line.startIndex
        while idx < line.endIndex && (line[idx] == " " || line[idx] == "\t") { idx = line.index(after: idx) }
        let rest = String(line[idx...])
        var markLen = 0
        if rest.hasPrefix("- ") || rest.hasPrefix("* ") || rest.hasPrefix("+ ") {
            markLen = 2
        } else {
            let digits = rest.prefix(while: { $0.isNumber }).count
            let after = rest.dropFirst(digits)
            if digits > 0 && (after.hasPrefix(". ") || after.hasPrefix(") ")) { markLen = digits + 2 }
        }
        guard markLen > 0 else { return }
        let off = lstart + line.utf16.distance(from: line.startIndex, to: idx)
        ctx.ops.append((NSRange(location: off, length: markLen), Paint(fg: ctx.pal.marker)))
        // Task checkbox right after the marker: accent it.
        let afterMark = String(line[line.index(idx, offsetBy: markLen)...])
        if afterMark.hasPrefix("[ ]") || afterMark.hasPrefix("[x]") || afterMark.hasPrefix("[X]") {
            ctx.ops.append((NSRange(location: off + markLen, length: 3), Paint(fg: ctx.pal.marker, bold: true)))
        }
    }

    // MARK: - Links/images: children blue+underline, the rest dim

    private static func paintLinkLike(_ m: Markup, _ ctx: Ctx) {
        guard let lr = m.range.flatMap({ ctx.map.nsRange($0) }) else { return }
        let kids = m.children.compactMap { $0.range.flatMap { ctx.map.nsRange($0) } }
            .sorted { $0.location < $1.location }
        let dim = Paint(fg: ctx.pal.dim)
        let link = Paint(fg: ctx.pal.link, underline: true)
        guard kids.count == m.children.reduce(0, { acc, _ in acc + 1 }), !kids.isEmpty else {
            ctx.ops.append((lr, dim))
            return
        }
        var cursor = lr.location
        let end = lr.location + lr.length
        for k in kids {
            if k.location > cursor { ctx.ops.append((NSRange(location: cursor, length: k.location - cursor), dim)) }
            ctx.ops.append((k, link))
            cursor = k.location + k.length
        }
        if cursor < end { ctx.ops.append((NSRange(location: cursor, length: end - cursor), dim)) }
    }

    // MARK: - HTML (tags/comments confined to HTML nodes)

    private static let htmlRe = try! NSRegularExpression(pattern: "(<!--[\\s\\S]*?-->)|(</?[A-Za-z][A-Za-z0-9]*(?:\\s[^<>\\n]*)?/?>)")
    private static let tagRe = try! NSRegularExpression(pattern: "^(</?)([A-Za-z][A-Za-z0-9]*)")
    private static let attrStrRe = try! NSRegularExpression(pattern: "\"[^\"]*\"")

    private static func paintHtml(in r: NSRange, _ ctx: Ctx) {
        let text = ctx.ns.substring(with: NSIntersectionRange(r, NSRange(location: 0, length: ctx.ns.length)))
        let base = r.location
        for m in htmlRe.matches(in: text, range: NSRange(location: 0, length: (text as NSString).length)) {
            let mr = NSRange(location: base + m.range.location, length: m.range.length)
            if m.range(at: 1).location != NSNotFound {
                ctx.ops.append((mr, Paint(fg: ctx.pal.dim, italic: true)))
            } else {
                paintHtmlTag(in: mr, ctx)
            }
        }
    }

    private static func paintHtmlTag(in range: NSRange, _ ctx: Ctx) {
        let s = ctx.ns.substring(with: range)
        let base = range.location
        ctx.ops.append((range, Paint(fg: ctx.pal.dim)))
        guard let m = tagRe.firstMatch(in: s, range: NSRange(location: 0, length: (s as NSString).length))
        else { return }
        ctx.ops.append((NSRange(location: base + m.range(at: 2).location, length: m.range(at: 2).length),
            Paint(fg: ctx.pal.marker, bold: true)))
        for sm in attrStrRe.matches(in: s, range: NSRange(location: 0, length: (s as NSString).length)) {
            ctx.ops.append((NSRange(location: base + sm.range.location, length: sm.range.length),
                Paint(fg: ctx.pal.code)))
        }
    }

    // MARK: - Tables: delimiter rows + structural pipes (code/link spans excluded)

    /// Pipes and delimiter tint, painted right after the table's children
    /// walked (guarded ranges already complete): no second pass, no row cover.
    private static func paintTable(_ t: Table, _ ctx: Ctx) {
        guard let tr = t.range.flatMap({ ctx.map.nsRange($0) }) else { return }
        for (lstart, line) in lines(in: tr, ctx) {
            if isDelimiterLine(line) {
                ctx.ops.append((NSRange(location: lstart, length: line.utf16.count), Paint(fg: ctx.pal.dim)))
                continue
            }
            var unit = 0
            var idx = line.startIndex
            while idx < line.endIndex {
                if line[idx] == "|" {
                    let abs = lstart + unit
                    if !ctx.guarded.contains(where: { NSLocationInRange(abs, $0) }) {
                        ctx.ops.append((NSRange(location: abs, length: 1), Paint(fg: ctx.pal.dim)))
                    }
                }
                unit += line[idx].utf16.count
                idx = line.index(after: idx)
            }
        }
    }

    private static func isDelimiterLine(_ line: String) -> Bool {
        var t = line.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("|") { t.removeFirst() }
        if t.hasSuffix("|") { t.removeLast() }
        let cells = t.split(separator: "|", omittingEmptySubsequences: false)
        guard !cells.isEmpty else { return false }
        return cells.allSatisfy { c in
            var k = c.trimmingCharacters(in: .whitespaces)
            guard !k.isEmpty else { return false }
            if k.hasPrefix(":") { k.removeFirst() }
            if k.hasSuffix(":") { k.removeLast() }
            return !k.isEmpty && k.allSatisfy({ $0 == "-" })
        }
    }

    // MARK: - Palette (same VS tones as the preview CSS)

    struct Palette {
        let heading, strong, emphasis, code, codeBg, link, quote, marker, dim: NSColor
        // JSON tree + edit-mode highlight (VS JSON tones, same as Windows).
        let jsonKey, jsonString, jsonNumber, jsonBool, jsonNull, jsonComment: NSColor
        init(dark: Bool) {
            func c(_ r: Int, _ g: Int, _ b: Int) -> NSColor {
                NSColor(red: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: 1)
            }
            if dark {
                heading = c(0x56, 0x9C, 0xD6); strong = c(0xDC, 0xDC, 0xAA); emphasis = c(0xC5, 0x86, 0xC0)
                code = c(0xCE, 0x91, 0x78); codeBg = c(0x2D, 0x2D, 0x30); link = c(0x4F, 0xC1, 0xFF)
                quote = c(0x9C, 0xDC, 0xFE); marker = c(0x56, 0x9C, 0xD6); dim = c(0x80, 0x80, 0x80)
                jsonKey = c(0x9C, 0xDC, 0xFE); jsonString = c(0xCE, 0x91, 0x78); jsonNumber = c(0xB5, 0xCE, 0xA8)
                jsonBool = c(0x56, 0x9C, 0xD6); jsonNull = c(0x80, 0x80, 0x80); jsonComment = c(0x6A, 0x99, 0x50)
            } else {
                heading = c(0x0F, 0x6C, 0xBD); strong = c(0xA3, 0x15, 0x15); emphasis = c(0x7A, 0x3E, 0x9D)
                code = c(0xA3, 0x15, 0x15); codeBg = c(0xF3, 0xF3, 0xF3); link = c(0x0F, 0x6C, 0xBD)
                quote = c(0x07, 0x5A, 0xA0); marker = c(0x0F, 0x6C, 0xBD); dim = c(0x80, 0x80, 0x80)
                jsonKey = c(0x00, 0x10, 0x80); jsonString = c(0xA3, 0x15, 0x15); jsonNumber = c(0x09, 0x86, 0x58)
                jsonBool = c(0x0F, 0x6C, 0xBD); jsonNull = c(0x80, 0x80, 0x80); jsonComment = c(0x00, 0x80, 0x00)
            }
        }
    }
}
