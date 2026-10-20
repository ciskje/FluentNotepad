import AppKit
import Foundation

/// JSON/JSONC (.json/.jsonc) -> collapsible tree HTML for WKWebView, plus
/// tolerant edit-mode highlighting. Port of the Windows 2.8.0 set
/// (JsonPreviewHtml.cs + JsonHighlight.cs over the shared PreviewTree.cs
/// widget): same node schema (t/o/a/s/n/b/u, k/v/l/c, remarks m/e), same
/// CSS/JS, same caps (10 MB / 300k nodes / depth 200). Comments (`//`, `/**/`)
/// and trailing commas are accepted, so .jsonc needs no browser-side parsing.
enum JsonTree {
    // MARK: - Node (same schema as Windows' PreviewTree.TreeNode)

    final class Node: Codable {
        var k: String?
        var t: String
        var v: String?
        var l: Int
        var c: [Node]?
        var tr = false
        var m: [String]?
        var e: [String]?

        init(k: String? = nil, t: String, v: String? = nil, l: Int, c: [Node]? = nil,
            m: [String]? = nil, e: [String]? = nil)
        {
            self.k = k; self.t = t; self.v = v; self.l = l; self.c = c; self.m = m; self.e = e
        }

        enum CodingKeys: String, CodingKey { case k, t, v, l, c, tr, m, e }

        func encode(to enc: Encoder) throws {
            var out = enc.container(keyedBy: CodingKeys.self)
            try out.encodeIfPresent(k, forKey: .k)
            try out.encode(t, forKey: .t)
            try out.encodeIfPresent(v, forKey: .v)
            try out.encode(l, forKey: .l)
            try out.encodeIfPresent(c, forKey: .c)
            if tr { try out.encode(true, forKey: .tr) }
            try out.encodeIfPresent(m, forKey: .m)
            try out.encodeIfPresent(e, forKey: .e)
        }
    }

    typealias ParseError = PreviewTree.ParseError

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?, open: Set<String>) -> String {
        if text.count > PreviewTree.maxChars {
            return PreviewTree.tooLargePage(dark: dark, fontSize: fontSize, title: title ?? "JSON",
                message: L("jsonTooLarge"))
        }
        do {
            let (root, truncated) = try parse(text)
            let enc = JSONEncoder()
            return PreviewTree.treePage(dark: dark, fontSize: fontSize, title: title ?? "JSON",
                rootJSON: String(data: try enc.encode(root), encoding: .utf8) ?? "null",
                openJSON: String(data: try enc.encode(open.sorted()), encoding: .utf8) ?? "[]",
                truncated: truncated, truncText: L("jsonTruncated"), isXML: false)
        } catch let ex as ParseError {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("jsonInvalidTitle"),
                invalidLine: String(format: L("jsonInvalid"), ex.line, ex.column),
                details: ex.message, source: text, hint: L("jsonErrorHint"))
        } catch {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("jsonInvalidTitle"), invalidLine: "", details: "\(error)",
                source: text, hint: L("jsonErrorHint"))
        }
    }




    // MARK: - Parse (single pass, positions included)

    static func parse(_ text: String) throws -> (Node, Bool) {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ParseError(line: 1, column: 1, message: "The JSON document is empty.")
        }
        let p = Parser(text)
        p.ws()
        guard p.peek() != nil else {
            throw ParseError(line: 1, column: 1, message: "The JSON document is empty.")
        }
        let root = try p.readValue(depth: 0)
        p.ate(root)
        p.ws()
        if p.peek() != nil {
            throw ParseError(line: p.line, column: p.col, message: "Extra content after the JSON document.")
        }
        if !p.pending.isEmpty { root.e = (root.e ?? []) + p.takePending()! }
        return (root, p.trunc)
    }

    /// Hand-rolled recursive descent (not JSONSerialization) so .jsonc needs no
    /// preprocessing and every node keeps its 1-based source line. Comments ride
    /// along as leading (m) or trailing (e) remarks of the node they stick to.
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

        func peek() -> Character? { i < a.count ? a[i] : nil }

        @discardableResult
        func advance() -> Character? {
            guard i < a.count else { return nil }
            let ch = a[i]; i += 1
            if ch == "\n" { line += 1; col = 1 } else { col += 1 }
            return ch
        }

        func err(_ message: String) -> ParseError { ParseError(line: line, column: col, message: message) }

        func ate(_ n: Node) { last = n; lastLine = line }

        func ateComment(_ c: String, at startLine: Int) {
            if last != nil, startLine == lastLine { last!.e = (last!.e ?? []) + [c] }
            else { pending.append(c) }
        }

        func takePending() -> [String]? {
            guard !pending.isEmpty else { return nil }
            let l = pending; pending = []; return l
        }

        /// Whitespace + comments (attached, never dropped).
        func ws() {
            while let ch = peek() {
                if ch == " " || ch == "\t" || ch == "\r" || ch == "\n" { advance(); continue }
                if ch == "/", i + 1 < a.count, a[i + 1] == "/" {
                    let sl = line
                    advance(); advance()
                    var t = ""
                    while let d = peek(), d != "\n" { t.append(d); advance() }
                    ateComment("//" + t, at: sl)
                    continue
                }
                if ch == "/", i + 1 < a.count, a[i + 1] == "*" {
                    let sl = line
                    advance(); advance()
                    var t = ""
                    var closed = false
                    while peek() != nil {
                        if peek() == "*", i + 1 < a.count, a[i + 1] == "/" {
                            advance(); advance(); closed = true; break
                        }
                        t.append(peek()!); advance()
                    }
                    if !closed { break } // unterminated: the value reader reports it
                    ateComment("/*" + t + "*/", at: sl)
                    continue
                }
                break
            }
        }

        // Current token is the value's first token. Consumes through its end.
        func readValue(depth: Int) throws -> Node {
            last = nil
            count += 1
            ws()
            if count > PreviewTree.maxNodes || depth > PreviewTree.maxDepth {
                // Cap hit: swallow the whole value, leave one widow marker.
                trunc = true
                pending = []
                last = nil
                let l = line
                try skipValue()
                return Node(t: "s", v: "…", l: l)
            }
            guard let ch = peek() else { throw err("Unexpected end of document.") }
            switch ch {
            case "{": return try readObject(depth: depth)
            case "[": return try readArray(depth: depth)
            case "\"":
                let (v, l) = try readString()
                return Node(t: "s", v: v, l: l, m: takePending())
            case "-", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9":
                let (v, l) = try readNumber()
                return Node(t: "n", v: v, l: l, m: takePending())
            case "t":
                let l = line; try readLit("true")
                return Node(t: "b", v: "true", l: l, m: takePending())
            case "f":
                let l = line; try readLit("false")
                return Node(t: "b", v: "false", l: l, m: takePending())
            case "n":
                let l = line; try readLit("null")
                return Node(t: "u", l: l, m: takePending())
            default:
                throw err("Unexpected token.")
            }
        }

        func readObject(depth: Int) throws -> Node {
            let l = line
            advance() // {
            last = nil
            let node = Node(t: "o", l: l, c: [], m: takePending())
            while true {
                ws()
                guard let ch = peek() else { throw err("Unexpected end of document.") }
                if ch == "}" {
                    advance()
                    if let e = takePending() { node.e = (node.e ?? []) + e }
                    return node
                }
                guard ch == "\"" else { throw err("Expected a property name.") }
                let (key, _) = try readString()
                ws()
                guard peek() == ":" else { throw err("Expected ':'.") }
                advance()
                let child = try readValue(depth: depth + 1)
                child.k = key
                node.c!.append(child)
                ate(child)
                if trunc, node.c!.count >= PreviewTree.maxKidsAfterTrunc {
                    pending = []; last = nil
                    try skipRest(end: "}")
                    return node
                }
                ws()
                if peek() == "," {
                    advance()
                    ws()
                    if peek() == "}" { // trailing comma
                        advance()
                        if let e = takePending() { node.e = (node.e ?? []) + e }
                        return node
                    }
                    continue
                }
                // Loop head handles `}`; anything else is garbage.
                ws()
                if peek() == "}" { continue }
                if peek() == nil { throw err("Unexpected end of document.") }
                throw err("Expected ',' or '}'.")
            }
        }

        func readArray(depth: Int) throws -> Node {
            let l = line
            advance() // [
            last = nil
            let node = Node(t: "a", l: l, c: [], m: takePending())
            while true {
                ws()
                guard let ch = peek() else { throw err("Unexpected end of document.") }
                if ch == "]" {
                    advance()
                    if let e = takePending() { node.e = (node.e ?? []) + e }
                    return node
                }
                let child = try readValue(depth: depth + 1)
                node.c!.append(child)
                ate(child)
                if trunc, node.c!.count >= PreviewTree.maxKidsAfterTrunc {
                    pending = []; last = nil
                    try skipRest(end: "]")
                    return node
                }
                ws()
                if peek() == "," {
                    advance()
                    ws()
                    if peek() == "]" { // trailing comma
                        advance()
                        if let e = takePending() { node.e = (node.e ?? []) + e }
                        return node
                    }
                    continue
                }
                ws()
                if peek() == "]" { continue }
                if peek() == nil { throw err("Unexpected end of document.") }
                throw err("Expected ',' or ']'.")
            }
        }

        func readString() throws -> (String, Int) {
            let l = line
            advance() // opening quote
            var out = ""
            while let ch = peek() {
                if ch == "\"" { advance(); return (out, l) }
                if ch == "\n" { throw err("Unterminated string.") }
                if ch != "\\" { out.append(ch); advance(); continue }
                advance() // backslash
                guard let e = peek() else { throw err("Unexpected end of document.") }
                advance()
                switch e {
                case "\"": out.append("\"")
                case "\\": out.append("\\")
                case "/": out.append("/")
                case "b": out.append("\u{08}")
                case "f": out.append("\u{0C}")
                case "n": out.append("\n")
                case "r": out.append("\r")
                case "t": out.append("\t")
                case "u":
                    var hex = ""
                    for _ in 0..<4 {
                        guard let h = peek(), h.isHexDigit else { throw err("Bad \\u escape.") }
                        hex.append(h); advance()
                    }
                    guard let cp = UInt32(hex, radix: 16) else { throw err("Bad \\u escape.") }
                    if cp >= 0xD800, cp <= 0xDBFF {
                        // Surrogate pair.
                        guard peek() == "\\" else { throw err("Bad surrogate pair.") }
                        advance()
                        guard peek() == "u" else { throw err("Bad surrogate pair.") }
                        advance()
                        var hex2 = ""
                        for _ in 0..<4 {
                            guard let h = peek(), h.isHexDigit else { throw err("Bad surrogate pair.") }
                            hex2.append(h); advance()
                        }
                        guard let lo = UInt32(hex2, radix: 16), lo >= 0xDC00, lo <= 0xDFFF,
                            let s = Unicode.Scalar(0x10000 + (cp - 0xD800) * 0x400 + (lo - 0xDC00))
                        else { throw err("Bad surrogate pair.") }
                        out.unicodeScalars.append(s)
                    } else if let s = Unicode.Scalar(cp) {
                        out.unicodeScalars.append(s)
                    } else {
                        throw err("Bad \\u escape.")
                    }
                default:
                    throw err("Bad escape.")
                }
            }
            throw err("Unterminated string.")
        }

        func readNumber() throws -> (String, Int) {
            let l = line
            let s = i
            if peek() == "-" {
                advance()
                guard let d = peek(), d.isNumber else { throw err("Bad number.") }
                _ = d
            }
            guard let d = peek(), d.isNumber else { throw err("Bad number.") }
            _ = d
            while let d = peek(), d.isNumber { advance() }
            if peek() == "." {
                let dot = i
                advance()
                if peek()?.isNumber != true {
                    // Lone dot: rewind, the int part stands alone.
                    while i > dot { i -= 1 } // col approximate; error pages only
                    return (String(a[s..<i]), l)
                }
                while let d = peek(), d.isNumber { advance() }
            }
            if peek() == "e" || peek() == "E" {
                let epos = i
                advance()
                if peek() == "+" || peek() == "-" { advance() }
                if peek()?.isNumber == true {
                    while let d = peek(), d.isNumber { advance() }
                } else {
                    while i > epos { i -= 1 } // lone exponent: rewind
                }
            }
            return (String(a[s..<i]), l)
        }

        func readLit(_ word: String) throws {
            for ch in word {
                guard peek() == ch else { throw err("Invalid literal.") }
                advance()
            }
            // A longer identifier (e.g. "nullable") must not match the prefix.
            if let n = peek(), n.isLetter || n.isNumber || n == "_" {
                throw err("Invalid literal.")
            }
        }

        func skipValue() throws {
            ws()
            guard let ch = peek() else { return }
            if ch == "{" || ch == "[" {
                advance()
                var d = 1
                while d > 0 {
                    ws()
                    guard let c = peek() else { throw err("Unexpected end of document.") }
                    if c == "\"" { _ = try readString(); continue }
                    advance()
                    if c == "{" || c == "[" { d += 1 }
                    else if c == "}" || c == "]" { d -= 1 }
                }
            } else if ch == "\"" {
                _ = try readString()
            } else {
                while let c = peek(), c != ",", c != "}", c != "]" { advance() }
            }
        }

        func skipRest(end: Character) throws {
            var d = 0
            while true {
                ws()
                guard let c = peek() else { throw err("Unexpected end of document.") }
                if c == "\"" { _ = try readString(); continue }
                advance()
                if c == "{" || c == "[" { d += 1 }
                else if c == end, d == 0 { return }
                else if c == "}" || c == "]" { d -= 1 }
            }
        }
    }
}

/// Tolerant JSON scanner for edit mode (NSTextView), mirroring Windows'
/// JsonHighlight.cs: visual attributes only, the text stays plain. A
/// hand-rolled single pass (not a strict parser) so colors survive while
/// typing incomplete JSON. Keys are strings followed by ':'; `//` and `/**/`
/// comments are painted (JSONC). Skipped for huge docs.
enum JsonHighlight {
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
            var i = 0
            while i < n {
                let ch = u16[i]
                if ch == 0x22 { // string
                    let s = i
                    i += 1
                    var esc = false, closed = false
                    while i < n {
                        let d = u16[i]
                        if esc { esc = false; i += 1; continue }
                        if d == 0x5C { esc = true; i += 1; continue } // backslash
                        if d == 0x22 { i += 1; closed = true; break }
                        if d == 0x0A { break } // unterminated: don't paint across lines
                        i += 1
                    }
                    if !closed { continue } // incomplete string while typing: keep base text
                    var j = i
                    while j < n, u16[j] == 0x20 || u16[j] == 0x09 || u16[j] == 0x0D || u16[j] == 0x0A { j += 1 }
                    let isKey = j < n && u16[j] == 0x3A
                    ops.append((NSRange(location: s, length: i - s), isKey ? pal.jsonKey : pal.jsonString, baseFont))
                } else if ch == 0x2F, i + 1 < n, u16[i + 1] == 0x2F { // // comment
                    let s = i
                    i += 2
                    while i < n, u16[i] != 0x0A { i += 1 }
                    ops.append((NSRange(location: s, length: i - s), pal.jsonComment, italic))
                } else if ch == 0x2F, i + 1 < n, u16[i + 1] == 0x2A { // /* comment */
                    let s = i
                    i += 2
                    while i + 1 < n, !(u16[i] == 0x2A && u16[i + 1] == 0x2F) { i += 1 }
                    i = min(n, i + 2)
                    ops.append((NSRange(location: s, length: i - s), pal.jsonComment, italic))
                } else if ch == 0x2D || (ch >= 0x30 && ch <= 0x39) { // number
                    let s = i
                    if ch == 0x2D {
                        i += 1
                        if !(i < n && u16[i] >= 0x30 && u16[i] <= 0x39) { continue }
                    }
                    while i < n, u16[i] >= 0x30 && u16[i] <= 0x39 { i += 1 }
                    if i < n, u16[i] == 0x2E {
                        let dot = i
                        i += 1
                        if i < n, u16[i] >= 0x30 && u16[i] <= 0x39 {
                            while i < n, u16[i] >= 0x30 && u16[i] <= 0x39 { i += 1 }
                        } else {
                            ops.append((NSRange(location: s, length: dot - s), pal.jsonNumber, baseFont))
                            i = dot
                            continue
                        }
                    }
                    if i < n, (u16[i] == 0x65 || u16[i] == 0x45) {
                        let epos = i
                        i += 1
                        if i < n, (u16[i] == 0x2B || u16[i] == 0x2D) { i += 1 }
                        if i < n, u16[i] >= 0x30 && u16[i] <= 0x39 {
                            while i < n, u16[i] >= 0x30 && u16[i] <= 0x39 { i += 1 }
                        } else {
                            i = epos
                        }
                    }
                    ops.append((NSRange(location: s, length: i - s), pal.jsonNumber, baseFont))
                } else if matchWord(u16, i, "true") {
                    ops.append((NSRange(location: i, length: 4), pal.jsonBool, baseFont))
                    i += 4
                } else if matchWord(u16, i, "false") {
                    ops.append((NSRange(location: i, length: 5), pal.jsonBool, baseFont))
                    i += 5
                } else if matchWord(u16, i, "null") {
                    ops.append((NSRange(location: i, length: 4), pal.jsonNull, italic))
                    i += 4
                } else {
                    i += 1 // punctuation/whitespace: base colors
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

    private static func matchWord(_ u16: [UInt16], _ i: Int, _ word: String) -> Bool {
        let w = Array(word.utf16)
        guard i + w.count <= u16.count else { return false }
        for k in 0..<w.count where u16[i + k] != w[k] { return false }
        let after = i + w.count
        // A longer identifier (e.g. "nullable") must not match the prefix.
        if after < u16.count {
            let c = u16[after]
            if c == 0x5F || (c >= 0x30 && c <= 0x39) || (c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A) {
                return false
            }
        }
        return true
    }
}
