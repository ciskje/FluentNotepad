import AppKit
import Foundation

/// XML (.xml/.config) -> collapsible tree HTML for WKWebView, on the same
/// PreviewTree schema as JSON: elements are objects, attributes are `@name`
/// leaves, text-only elements collapse to leaves, mixed text becomes `#text`
/// rows, XML comments ride as remarks. Port of Windows' XmlPreviewHtml.cs +
/// XmlHighlight.cs (2.9.0). Positions are tracked 1-based by the hand-rolled
/// scanner (no XMLParser: full control over comments/PIs/CDATA/DOCTYPE,
/// truncation and JSONC-style caps).
enum XmlTree {
    static let maxChars = 10_000_000
    static let maxNodes = 300_000
    static let maxDepth = 200
    static let maxKidsAfterTrunc = 1000

    typealias Node = JsonTree.Node
    typealias ParseError = JsonTree.ParseError

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?, open: Set<String>) -> String {
        if text.count > maxChars {
            let pal = PreviewTree.Palette(dark: dark)
            return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
                + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
                + "<title>" + PreviewTree.esc(title ?? "XML") + "</title>"
                + "<style>" + PreviewTree.css(pal, Int(fontSize)) + "</style></head><body>"
                + "<div class=err><div class=et>" + PreviewTree.esc(L("xmlTooLarge"))
                + "</div></div></body></html>"
        }
        do {
            let (root, truncated) = try parse(text)
            let enc = JSONEncoder()
            return PreviewTree.treePage(dark: dark, fontSize: fontSize, title: title ?? "XML",
                rootJSON: String(data: try enc.encode(root), encoding: .utf8) ?? "null",
                openJSON: String(data: try enc.encode(open.sorted()), encoding: .utf8) ?? "[]",
                truncated: truncated, truncText: L("xmlTruncated"), isXML: true)
        } catch let ex as ParseError {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("xmlInvalidTitle"),
                invalidLine: String(format: L("xmlInvalid"), ex.line, ex.column),
                details: ex.message, source: text, hint: L("xmlErrorHint"))
        } catch {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("xmlInvalidTitle"), invalidLine: "", details: "\(error)",
                source: text, hint: L("xmlErrorHint"))
        }
    }




    // MARK: - Parse (single hand-rolled pass, positions included)

    static func parse(_ text: String) throws -> (Node, Bool) {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ParseError(line: 1, column: 1, message: "The XML document is empty.")
        }
        let p = Parser(text)
        let root = Node(t: "o", l: 1, c: [])
        var docEl: Node?
        while true {
            p.skipWhitespace()
            guard p.peek(0) != nil else { break }
            if p.peek(0) == "<" {
                if let nx = p.peek(1), p.isNameStart(nx) {
                    if docEl != nil {
                        throw ParseError(line: p.line, column: p.col, message: "Extra content after the XML document.")
                    }
                    let el = try p.readElement(depth: 0)
                    docEl = el
                    root.c!.append(el)
                    p.ate(el)
                    continue
                }
                switch try p.scanTag() {
                case let .comment(c): p.ateComment(c)
                case let .pi(n, d): p.ateComment(piRemark(n, d))
                case let .doctype(n): p.ateComment("<!DOCTYPE " + n + ">")
                case .cdata: throw ParseError(line: p.line, column: p.col, message: "CDATA outside the document element.")
                case .start: throw ParseError(line: p.line, column: p.col, message: "Expected an element.")
                case .end: throw ParseError(line: p.line, column: p.col, message: "Unmatched end tag.")
                }
            } else {
                p.skipText() // stray text outside the root is ignored (like Windows)
            }
        }
        if docEl == nil { throw ParseError(line: 1, column: 1, message: "The XML document is empty.") }
        root.e = p.takePending()
        return (root, p.trunc)
    }

    private static func piRemark(_ name: String, _ data: String) -> String {
        data.isEmpty ? "<?" + name + "?>" : "<?" + name + " " + data + "?>"
    }

    enum Tag {
        case comment(String)
        case pi(name: String, data: String)
        case doctype(name: String)
        case cdata(String)
        case start(name: String, attrs: [(String, String)], empty: Bool, line: Int)
        case end(name: String)
    }

    final class Parser {
        let a: [Character]
        var i = 0
        var line = 1
        var col = 1
        var count = 0
        var trunc = false
        var pending: [String] = []
        var last: Node?
        var lastLine = 0

        init(_ text: String) { a = Array(text) }

        func peek(_ off: Int) -> Character? {
            let j = i + off
            return j < a.count ? a[j] : nil
        }

        @discardableResult
        func advance() -> Character? {
            guard i < a.count else { return nil }
            let ch = a[i]; i += 1
            if ch == "\n" { line += 1; col = 1 } else { col += 1 }
            return ch
        }

        func err(_ message: String) -> ParseError { ParseError(line: line, column: col, message: message) }

        func ate(_ n: Node) { last = n; lastLine = line }

        func ateComment(_ c: String) {
            if last != nil, line == lastLine { last!.e = (last!.e ?? []) + [c] }
            else { pending.append(c) }
        }

        func takePending() -> [String]? {
            guard !pending.isEmpty else { return nil }
            let l = pending; pending = []; return l
        }

        func skipWhitespace() {
            while let c = peek(0), c == " " || c == "\t" || c == "\r" || c == "\n" { advance() }
        }

        /// Skips one text chunk (up to the next `<`), used outside the root.
        func skipText() {
            while let c = peek(0), c != "<" { advance() }
        }

        func starts(_ s: String) -> Bool {
            guard i + s.count <= a.count else { return false }
            return String(a[i..<(i + s.count)]) == s
        }

        // MARK: - Tag scanner (positioned at `<`, consumes the whole token)

        func scanTag() throws -> Tag {
            let l = line
            advance() // <
            guard let c = peek(0) else { throw err("Unexpected end of document.") }
            if c == "!" {
                if starts("!--") { return try scanComment() }
                if starts("![CDATA[") { return try scanCdata() }
                if starts("!DOCTYPE") { return try scanDoctype() }
                throw err("Invalid markup declaration.")
            }
            if c == "?" { return try scanPI() }
            if c == "/" {
                advance()
                let n = try scanName()
                skipWhitespace()
                guard peek(0) == ">" else { throw err("Expected '>'.") }
                advance()
                return .end(name: n)
            }
            // Start (or empty) element.
            let n = try scanName()
            var attrs: [(String, String)] = []
            while true {
                skipWhitespace()
                guard let d = peek(0) else { throw err("Unexpected end of document.") }
                if d == ">" { advance(); return .start(name: n, attrs: attrs, empty: false, line: l) }
                if d == "/", peek(1) == ">" {
                    advance(); advance()
                    return .start(name: n, attrs: attrs, empty: true, line: l)
                }
                let an = try scanName()
                skipWhitespace()
                guard peek(0) == "=" else { throw err("Expected '='.") }
                advance()
                skipWhitespace()
                let v = try scanAttrValue()
                if attrs.contains(where: { $0.0 == an }) { throw err("Duplicate attribute.") }
                attrs.append((an, v))
            }
        }

        func scanComment() throws -> Tag {
            for _ in 0..<3 { advance() } // <!-- (`<` already consumed)
            var t = ""
            while true {
                guard peek(0) != nil else { throw err("Unterminated comment.") }
                if starts("-->") { for _ in 0..<3 { advance() }; return .comment("<!--" + t + "-->") }
                t.append(peek(0)!); advance()
            }
        }

        func scanCdata() throws -> Tag {
            for _ in 0..<8 { advance() } // <![CDATA[ (`<` already consumed)
            var t = ""
            while true {
                guard peek(0) != nil else { throw err("Unterminated CDATA section.") }
                if starts("]]>") { for _ in 0..<3 { advance() }; return .cdata(t) }
                t.append(peek(0)!); advance()
            }
        }

        func scanPI() throws -> Tag {
            advance() // ?
            skipWhitespace()
            let n = try scanName()
            skipWhitespace()
            var d = ""
            while true {
                guard peek(0) != nil else { throw err("Unterminated processing instruction.") }
                if starts("?>") { advance(); advance(); return .pi(name: n, data: d.trimmingCharacters(in: .whitespaces)) }
                d.append(peek(0)!); advance()
            }
        }

        func scanDoctype() throws -> Tag {
            for _ in 0..<8 { advance() } // <!DOCTYPE (`<` already consumed)
            skipWhitespace()
            let n = try scanName()
            // Skip to `>`, honoring an internal `[...]` subset and quotes.
            var depth = 0
            var quote: Character?
            while let c = peek(0) {
                advance()
                if let q = quote {
                    if c == q { quote = nil }
                    continue
                }
                if c == "\"" || c == "'" { quote = c }
                else if c == "[" { depth += 1 }
                else if c == "]" { depth = max(0, depth - 1) }
                else if c == ">", depth == 0 { return .doctype(name: n) }
            }
            throw err("Unterminated DOCTYPE.")
        }

        func scanAttrValue() throws -> String {
            guard let q = peek(0), q == "\"" || q == "'" else { throw err("Expected attribute value.") }
            advance()
            var t = ""
            while let c = peek(0) {
                if c == q { advance(); return decodeEntities(t) }
                t.append(c); advance()
            }
            throw err("Unterminated attribute value.")
        }

        func isNameStart(_ c: Character) -> Bool {
            c.isLetter || c == "_" || c == ":"
        }

        func isNameChar(_ c: Character) -> Bool {
            isNameStart(c) || c.isNumber || c == "." || c == "-"
        }

        func scanName() throws -> String {
            guard let c = peek(0), isNameStart(c) else { throw err("Expected a name.") }
            var n = ""
            while let c = peek(0), isNameChar(c) { n.append(c); advance() }
            return n
        }

        /// Decodes the five predefined entities + character references.
        /// Unknown named entities are kept verbatim (tolerant).
        func decodeEntities(_ s: String) -> String {
            guard s.contains("&") else { return s }
            var out = ""
            var k = s.startIndex
            while k < s.endIndex {
                let c = s[k]
                if c != "&" { out.append(c); k = s.index(after: k); continue }
                guard let semi = s[k...].firstIndex(of: ";"), semi < s.index(k, offsetBy: 12, limitedBy: s.endIndex) ?? s.endIndex else {
                    out.append(c); k = s.index(after: k); continue
                }
                let ent = String(s[s.index(after: k)..<semi])
                let after = s.index(after: semi)
                if ent == "amp" { out.append("&") }
                else if ent == "lt" { out.append("<") }
                else if ent == "gt" { out.append(">") }
                else if ent == "quot" { out.append("\"") }
                else if ent == "apos" { out.append("'") }
                else if ent.hasPrefix("#x") || ent.hasPrefix("#X") {
                    if let cp = UInt32(ent.dropFirst(2), radix: 16), let sc = Unicode.Scalar(cp) {
                        out.unicodeScalars.append(sc)
                    } else { out.append("&" + ent + ";") }
                } else if ent.hasPrefix("#") {
                    if let cp = UInt32(ent.dropFirst()), let sc = Unicode.Scalar(cp) {
                        out.unicodeScalars.append(sc)
                    } else { out.append("&" + ent + ";") }
                } else {
                    out.append("&" + ent + ";") // unknown: verbatim
                }
                k = after
            }
            return out
        }

        // MARK: - Elements (positioned before `<name`)

        func readElement(depth: Int) throws -> Node {
            guard case let .start(name, attrs, empty, l) = try scanTag() else {
                throw err("Expected an element.")
            }
            return try elementBody(name: name, attrs: attrs, empty: empty, l: l, depth: depth)
        }

        func elementBody(name: String, attrs: [(String, String)], empty: Bool, l: Int, depth: Int) throws -> Node {
            last = nil
            count += 1
            if count > XmlTree.maxNodes || depth > XmlTree.maxDepth {
                trunc = true
                pending = []
                last = nil
                try skipElement(empty: empty)
                return Node(t: "s", v: "…", l: l)
            }
            let node = Node(t: "o", l: l, c: [], m: takePending())
            node.k = name
            for (an, av) in attrs {
                let at = Node(k: "@" + an, t: "s", v: av, l: l, m: takePending())
                node.c!.append(at)
                ate(at)
            }
            last = nil
            if empty {
                node.e = takePending()
                ate(node)
                return node
            }
            while true {
                guard peek(0) != nil else { throw err("Unexpected end of document.") }
                if peek(0) == "<" {
                    switch try scanTag() {
                    case let .comment(c): ateComment(c)
                    case let .pi(n, d): ateComment(XmlTree.piRemark(n, d))
                    case let .doctype(n): ateComment("<!DOCTYPE " + n + ">")
                    case let .cdata(t):
                        let tn = Node(k: "#text", t: "s", v: t, l: line, m: takePending())
                        node.c!.append(tn)
                        ate(tn)
                    case let .start(n, at, e, sl):
                        let child = try elementBody(name: n, attrs: at, empty: e, l: sl, depth: depth + 1)
                        node.c!.append(child)
                        ate(child)
                    case let .end(n):
                        if n != name { throw err("Mismatched end tag.") }
                        collapseText(node)
                        if let e = takePending() { node.e = (node.e ?? []) + e }
                        ate(node)
                        return node
                    }
                } else {
                    let tl = line
                    var t = ""
                    while let c = peek(0), c != "<" { t.append(c); advance() }
                    if t.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }
                    let tn = Node(k: "#text", t: "s", v: decodeEntities(t), l: tl, m: takePending())
                    node.c!.append(tn)
                    ate(tn)
                }
                if trunc, node.c!.count >= XmlTree.maxKidsAfterTrunc {
                    pending = []
                    last = nil
                    try skipRest()
                    return node
                }
            }
        }

        // MARK: - Truncation skips

        /// Skips the element whose start tag was just consumed.
        func skipElement(empty: Bool) throws {
            if empty { return }
            var d = 1
            while d > 0 {
                guard peek(0) != nil else { throw err("Unexpected end of document.") }
                if peek(0) == "<" {
                    switch try scanTag() {
                    case .start(_, _, let e, _): if !e { d += 1 }
                    case .end: d -= 1
                    default: break
                    }
                } else {
                    skipText()
                }
            }
        }

        func skipRest() throws {
            var d = 0
            while true {
                guard peek(0) != nil else { throw err("Unexpected end of document.") }
                if peek(0) == "<" {
                    switch try scanTag() {
                    case .start(_, _, let e, _): if !e { d += 1 }
                    case .end:
                        if d == 0 { return }
                        d -= 1
                    default: break
                    }
                } else {
                    skipText()
                }
            }
        }

        // Element with a single text child and no attributes reads as a leaf.
        func collapseText(_ node: Node) {
            guard let c = node.c, c.count == 1 else { return }
            let only = c[0]
            guard only.k == "#text", only.t == "s" else { return }
            if node.m == nil { node.m = only.m }
            else if let m = only.m { node.m! += m }
            node.t = "s"
            node.v = only.v
            node.c = nil
        }
    }
}

/// Tolerant XML scanner for edit mode (NSTextView), mirroring Windows'
/// XmlHighlight.cs: tags, attribute names/values, comments, CDATA and
/// processing instructions are painted; text content stays base.
enum XmlHighlight {
    static let maxChars = 300_000

    static func apply(to tv: NSTextView, baseFont: NSFont, dark: Bool) {
        guard let storage = tv.textStorage, !tv.hasMarkedText() else { return }
        let text = tv.string
        let ns = text as NSString
        let full = NSRange(location: 0, length: ns.length)
        tv.undoManager?.disableUndoRegistration()
        storage.beginEditing()
        storage.setAttributes([.font: baseFont, .foregroundColor: NSColor.textColor], range: full)
        if !text.isEmpty, text.count <= maxChars {
            let pal = SyntaxHighlight.Palette(dark: dark)
            let fm = NSFontManager.shared
            let italic = fm.convert(baseFont, toHaveTrait: .italicFontMask)
            let u16 = Array(text.utf16)
            let n = u16.count
            var ops: [(NSRange, NSColor, NSFont)] = []
            func starts(_ s: String, at i: Int) -> Bool {
                let w = Array(s.utf16)
                guard i + w.count <= n else { return false }
                for k in 0..<w.count where u16[i + k] != w[k] { return false }
                return true
            }
            var i = 0
            while i < n {
                if starts("<!--", at: i) {
                    let s = i
                    i += 4
                    var end = -1
                    var k = i
                    while k + 2 < n {
                        if u16[k] == 0x2D, u16[k + 1] == 0x2D, u16[k + 2] == 0x3E { end = k; break }
                        k += 1
                    }
                    i = end < 0 ? n : end + 3
                    ops.append((NSRange(location: s, length: i - s), pal.jsonComment, italic))
                } else if starts("<?", at: i) {
                    let s = i
                    i += 2
                    var end = -1
                    var k = i
                    while k + 1 < n {
                        if u16[k] == 0x3F, u16[k + 1] == 0x3E { end = k; break }
                        k += 1
                    }
                    i = end < 0 ? n : end + 2
                    ops.append((NSRange(location: s, length: i - s), pal.dim, baseFont))
                } else if starts("<![CDATA[", at: i) {
                    ops.append((NSRange(location: i, length: 9), pal.jsonKey, baseFont))
                    i += 9
                    let s = i
                    var end = -1
                    var k = i
                    while k + 2 < n {
                        if u16[k] == 0x5D, u16[k + 1] == 0x5D, u16[k + 2] == 0x3E { end = k; break }
                        k += 1
                    }
                    let ce = end < 0 ? n : end
                    if ce > s { ops.append((NSRange(location: s, length: ce - s), pal.jsonString, baseFont)) }
                    i = ce
                    if end >= 0 {
                        ops.append((NSRange(location: i, length: 3), pal.jsonKey, baseFont))
                        i += 3
                    }
                } else if starts("<!", at: i) {
                    let s = i
                    var k = i
                    while k < n, u16[k] != 0x3E { k += 1 }
                    i = k < n ? k + 1 : n
                    ops.append((NSRange(location: s, length: i - s), pal.dim, baseFont))
                } else if u16[i] == 0x3C { // <
                    i = paintTag(u16, n, i, pal, baseFont, &ops)
                } else {
                    var k = i
                    while k < n, u16[k] != 0x3C { k += 1 }
                    i = k // text content: base tone
                }
            }
            for (r, fg, font) in ops where r.length > 0 {
                let rr = NSIntersectionRange(r, full)
                if rr.length > 0 {
                    storage.setAttributes([.font: font, .foregroundColor: fg], range: rr)
                }
            }
        }
        storage.endEditing()
        tv.undoManager?.enableUndoRegistration()
        tv.typingAttributes = [.font: baseFont, .foregroundColor: NSColor.textColor]
    }

    // Paints one <...> tag from the '<', returns the index past its '>' (or n).
    private static func paintTag(_ u16: [UInt16], _ n: Int, _ i0: Int,
        _ pal: SyntaxHighlight.Palette, _ baseFont: NSFont,
        _ ops: inout [(NSRange, NSColor, NSFont)]) -> Int
    {
        var i = i0
        let s = i
        i += 1 // <
        if i < n, u16[i] == 0x2F { i += 1 } // /
        let ns = i
        while i < n, isNameChar(u16[i]) { i += 1 }
        if i > ns {
            ops.append((NSRange(location: s, length: 1), pal.jsonKey, baseFont)) // <
            ops.append((NSRange(location: ns, length: i - ns), pal.jsonKey, baseFont)) // tag name
        }
        while i < n {
            let c = u16[i]
            if c == 0x3E { // >
                ops.append((NSRange(location: i, length: 1), pal.jsonKey, baseFont))
                return i + 1
            }
            if c == 0x2F, i + 1 < n, u16[i + 1] == 0x3E { // />
                ops.append((NSRange(location: i, length: 2), pal.jsonKey, baseFont))
                return i + 2
            }
            if c == 0x22 || c == 0x27 { // quoted value
                let vs = i
                let q = c
                i += 1
                while i < n, u16[i] != q, u16[i] != 0x0A { i += 1 }
                if i < n, u16[i] == q { i += 1 }
                ops.append((NSRange(location: vs, length: i - vs), pal.jsonString, baseFont))
                continue
            }
            if isNameStart(u16[i]) {
                let astart = i
                while i < n, isNameChar(u16[i]) { i += 1 }
                let aend = i
                var j = aend
                while j < n, u16[j] == 0x20 || u16[j] == 0x09 || u16[j] == 0x0D || u16[j] == 0x0A { j += 1 }
                if j < n, u16[j] == 0x3D {
                    ops.append((NSRange(location: astart, length: aend - astart), pal.jsonBool, baseFont))
                }
                continue
            }
            i += 1
        }
        return i // unterminated tag (typing): paint what we have
    }

    private static func isNameStart(_ c: UInt16) -> Bool {
        c == 0x5F || c == 0x3A
            || (c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A)
    }

    private static func isNameChar(_ c: UInt16) -> Bool {
        isNameStart(c) || (c >= 0x30 && c <= 0x39) || c == 0x2E || c == 0x2D
    }
}
