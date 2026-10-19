import AppKit
import Foundation

/// TOML (.toml) -> collapsible tree HTML on the shared PreviewTree schema:
/// tables are objects, arrays of tables and arrays are arrays, dotted keys
/// are expanded (a.b.c = 1 nests). Values keep their raw literal. Port of
/// Windows' TomlPreviewHtml.cs + TomlHighlight.cs (2.10.0). Copy paths use
/// the $.a.b[2] form; subtree copy rebuilds JSON.
enum TomlTree {
    static let maxChars = 10_000_000
    static let maxNodes = 300_000
    static let maxDepth = 200
    static let maxKidsAfterTrunc = 1000

    typealias Node = JsonTree.Node
    typealias ParseError = PreviewTree.ParseError

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?, open: Set<String>) -> String {
        if text.count > maxChars {
            let pal = PreviewTree.Palette(dark: dark)
            return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
                + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
                + "<title>" + PreviewTree.esc(title ?? "TOML") + "</title>"
                + "<style>" + PreviewTree.css(pal, Int(fontSize)) + "</style></head><body>"
                + "<div class=err><div class=et>" + PreviewTree.esc(L("tomlTooLarge"))
                + "</div></div></body></html>"
        }
        do {
            let (root, truncated) = try parse(text)
            let enc = JSONEncoder()
            return PreviewTree.treePage(dark: dark, fontSize: fontSize, title: title ?? "TOML",
                rootJSON: String(data: try enc.encode(root), encoding: .utf8) ?? "null",
                openJSON: String(data: try enc.encode(open.sorted()), encoding: .utf8) ?? "[]",
                truncated: truncated, truncText: L("tomlTruncated"), isXML: false)
        } catch let ex as ParseError {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("tomlInvalidTitle"),
                invalidLine: String(format: L("tomlInvalid"), ex.line, ex.column),
                details: ex.message, source: text, hint: L("tomlErrorHint"))
        } catch {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("tomlInvalidTitle"), invalidLine: "", details: "\(error)",
                source: text, hint: L("tomlErrorHint"))
        }
    }

    // MARK: - Parse (cursor parser, 1-based positions like the editor)

    static func parse(_ text: String) throws -> (Node, Bool) {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ParseError(line: 1, column: 1, message: "The TOML document is empty.")
        }
        let w = Walker(text)
        let root = Node(t: "o", l: 1, c: [])
        var cur = root
        w.skipBlank()
        while !w.eof {
            if w.peek() == "[" {
                cur = try w.readTableHeader(root: root)
                try w.expectEol()
            } else {
                let (keys, val) = try w.readKeyValue()
                try w.assign(table: cur, keys: keys, val: val)
                try w.expectEol()
            }
            w.skipBlank()
        }
        if !w.pending.isEmpty { root.e = (root.e ?? []) + w.takePending()! }
        return (root, w.trunc)
    }

    final class Walker {
        let a: [Character]
        var starts: [Int] = [0]
        var pos = 0
        var count = 0
        var trunc = false
        var pending: [String] = []
        var last: Node?
        var lastLine = 0
        var definedTables = Set<String>()

        // Datetimes stay text (same shape as Windows' DateRe).
        static let dateRe = try! NSRegularExpression(
            pattern: #"^\d{4}-\d{2}-\d{2}([Tt ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?([Zz]|([+-]\d{2}:\d{2}))?)?|^\d{2}:\d{2}(:\d{2}(\.\d+)?)?"#)

        init(_ text: String) {
            a = Array(text)
            for i in 0..<a.count where a[i] == "\n" { starts.append(i + 1) }
        }

        var eof: Bool { pos >= a.count }
        func peek() -> Character { eof ? "\0" : a[pos] }

        func lineOf(_ p: Int) -> Int {
            var lo = 0, hi = starts.count - 1
            while lo < hi {
                let mid = (lo + hi + 1) / 2
                if starts[mid] <= p { lo = mid } else { hi = mid - 1 }
            }
            return lo + 1
        }

        func colOf(_ p: Int) -> Int { p - starts[lineOf(p) - 1] + 1 }
        func fail(_ msg: String) -> ParseError { ParseError(line: lineOf(pos), column: colOf(pos), message: msg) }
        func substr(_ s: Int, _ e: Int) -> String { String(a[s..<min(e, a.count)]) }

        func takePending() -> [String]? {
            guard !pending.isEmpty else { return nil }
            let l = pending; pending = []; return l
        }

        func ate(_ n: Node) { last = n; lastLine = lineOf(pos) }

        func ateComment(_ raw: String) {
            // Same-line comments trail the previous value, else lead the next.
            if last != nil, lineOf(pos) == lastLine { last!.e = (last!.e ?? []) + [raw] }
            else { pending.append(raw) }
        }

        func skipInline() {
            while !eof, a[pos] == " " || a[pos] == "\t" { pos += 1 }
        }

        /// Blank lines and full-line comments (attached as remarks).
        func skipBlank() {
            while !eof {
                skipInline()
                if eof { return }
                let c = a[pos]
                if c == "\n" { pos += 1; continue }
                if c == "\r" { pos += 1; if !eof, a[pos] == "\n" { pos += 1 }; continue }
                if c == "#" {
                    let s = pos
                    while !eof, a[pos] != "\n" { pos += 1 }
                    var raw = substr(s, pos)
                    if raw.hasSuffix("\r") { raw.removeLast() }
                    ateComment(raw)
                    continue
                }
                return
            }
        }

        /// Whitespace + comments between tokens (arrays, inline tables).
        func skipGap() {
            while !eof {
                skipInline()
                if eof { return }
                let c = a[pos]
                if c == "\n" || c == "\r" {
                    if c == "\r", pos + 1 < a.count, a[pos + 1] == "\n" { pos += 2 }
                    else { pos += 1 }
                    continue
                }
                if c == "#" {
                    let s = pos
                    while !eof, a[pos] != "\n" { pos += 1 }
                    var raw = substr(s, pos)
                    if raw.hasSuffix("\r") { raw.removeLast() }
                    ateComment(raw)
                    continue
                }
                return
            }
        }

        func expectEol() throws {
            skipInline()
            if eof { return }
            let c = a[pos]
            if c == "\n" { pos += 1; return }
            if c == "\r" {
                pos += 1
                if !eof, a[pos] == "\n" { pos += 1 }
                return
            }
            if c == "#" {
                let s = pos
                while !eof, a[pos] != "\n" { pos += 1 }
                var raw = substr(s, pos)
                if raw.hasSuffix("\r") { raw.removeLast() }
                ateComment(raw)
                try expectEol()
                return
            }
            throw fail("Expected a line break.")
        }

        // MARK: - Keys

        func readKeyPart() throws -> String {
            skipInline()
            if eof { throw fail("Expected a key.") }
            let c = a[pos]
            if c == "\"" { let (v, _) = try readBasicString(multiline: false); return v }
            if c == "'" { let (v, _) = try readLiteralString(multiline: false); return v }
            let s = pos
            while !eof, a[pos].isLetter || a[pos].isNumber || a[pos] == "_" || a[pos] == "-" { pos += 1 }
            if pos == s { throw fail("Expected a key.") }
            return substr(s, pos)
        }

        func readDottedKey() throws -> [(String, Int)] {
            var parts: [(String, Int)] = []
            while true {
                let l = lineOf(pos)
                parts.append((try readKeyPart(), l))
                skipInline()
                if !eof, a[pos] == "." { pos += 1; continue }
                return parts
            }
        }

        // MARK: - Strings

        func readBasicString(multiline: Bool) throws -> (String, Int) {
            precondition(!multiline)
            let line = lineOf(pos)
            pos += 1 // opening "
            var out = ""
            while true {
                if eof { throw fail("Unterminated string.") }
                let c = a[pos]
                if c == "\"" { pos += 1; return (out, line) }
                if c == "\n" { throw fail("Unterminated string.") }
                if c == "\\" {
                    pos += 1
                    if eof { throw fail("Unterminated escape.") }
                    let e = a[pos]
                    switch e {
                    case "b": out.append("\u{08}")
                    case "t": out.append("\t")
                    case "n": out.append("\n")
                    case "f": out.append("\u{0C}")
                    case "r": out.append("\r")
                    case "\"": out.append("\"")
                    case "\\": out.append("\\")
                    default: throw fail("Invalid escape '\\\(e)'.")
                    }
                    pos += 1
                    continue
                }
                if c.unicodeScalars.allSatisfy({ $0.value < 0x20 }) && c != "\t" {
                    throw fail("Control character in string.")
                }
                out.append(c)
                pos += 1
            }
        }

        func readMultilineBasic() throws -> (String, Int) {
            let line = lineOf(pos)
            pos += 3 // """
            if !eof, a[pos] == "\n" { pos += 1 }
            else if !eof, a[pos] == "\r" {
                pos += 1
                if !eof, a[pos] == "\n" { pos += 1 }
            }
            var out = ""
            while true {
                if eof { throw fail("Unterminated multi-line string.") }
                if a[pos] == "\"", pos + 2 < a.count, a[pos + 1] == "\"", a[pos + 2] == "\"" {
                    pos += 3
                    return (out, line)
                }
                // One or two quotes are content; three close.
                if a[pos] == "\"", pos + 1 < a.count, a[pos + 1] == "\"",
                    pos + 2 >= a.count || a[pos + 2] != "\""
                {
                    out.append("\"\"")
                    pos += 2
                    continue
                }
                if a[pos] == "\"", pos + 1 >= a.count || a[pos + 1] != "\"" {
                    out.append("\"")
                    pos += 1
                    continue
                }
                let c = a[pos]
                if c == "\\" {
                    // Line-ending backslash trims the break + leading whitespace.
                    var q = pos + 1
                    while q < a.count, a[q] == " " || a[q] == "\t" { q += 1 }
                    if q < a.count, a[q] == "\n" || a[q] == "\r" {
                        if a[q] == "\r", q + 1 < a.count, a[q + 1] == "\n" { q += 1 }
                        q += 1
                        while q < a.count, a[q] == " " || a[q] == "\t" || a[q] == "\n" || a[q] == "\r" { q += 1 }
                        pos = q
                        continue
                    }
                    pos += 1
                    if eof { throw fail("Unterminated escape.") }
                    let e = a[pos]
                    switch e {
                    case "b": out.append("\u{08}")
                    case "t": out.append("\t")
                    case "n": out.append("\n")
                    case "f": out.append("\u{0C}")
                    case "r": out.append("\r")
                    case "\"": out.append("\"")
                    case "\\": out.append("\\")
                    case "u": out.append(try readHex(digits: 4))
                    case "U": out.append(try readHex(digits: 8))
                    default: throw fail("Invalid escape '\\\(e)'.")
                    }
                    pos += 1
                    continue
                }
                out.append(c)
                pos += 1
            }
        }

        func readHex(digits: Int) throws -> String {
            if pos + digits > a.count { throw fail("Bad unicode escape.") }
            let hex = substr(pos, pos + digits)
            guard hex.allSatisfy({ $0.isHexDigit }) else { throw fail("Bad unicode escape.") }
            pos += digits - 1 // caller advances past the last digit
            guard let cp = UInt32(hex, radix: 16), cp <= 0x10FFFF,
                !(cp >= 0xD800 && cp <= 0xDFFF), let sc = Unicode.Scalar(cp)
            else { throw fail("Bad unicode escape.") }
            return String(sc)
        }

        func readLiteralString(multiline: Bool) throws -> (String, Int) {
            let line = lineOf(pos)
            if multiline {
                pos += 3 // '''
                if !eof, a[pos] == "\n" { pos += 1 }
                else if !eof, a[pos] == "\r" {
                    pos += 1
                    if !eof, a[pos] == "\n" { pos += 1 }
                }
                let s = pos
                while true {
                    if eof { throw fail("Unterminated multi-line literal.") }
                    if a[pos] == "'", pos + 2 < a.count, a[pos + 1] == "'", a[pos + 2] == "'" {
                        let v = substr(s, pos)
                        pos += 3
                        return (v, line)
                    }
                    pos += 1
                }
            }
            pos += 1 // '
            let st = pos
            while true {
                if eof { throw fail("Unterminated literal.") }
                if a[pos] == "'" {
                    let v = substr(st, pos)
                    pos += 1
                    return (v, line)
                }
                if a[pos] == "\n" { throw fail("Unterminated literal.") }
                pos += 1
            }
        }

        // MARK: - Values

        func readValue(depth: Int) throws -> Node {
            count += 1
            if count > TomlTree.maxNodes || depth > TomlTree.maxDepth {
                trunc = true
                pending = []
                last = nil
                let l = lineOf(pos)
                skipValue()
                return Node(t: "s", v: "…", l: l)
            }
            skipInline()
            if eof { throw fail("Expected a value.") }
            let line = lineOf(pos)
            let c = a[pos]
            if c == "\"" {
                let ml = pos + 2 < a.count && a[pos + 1] == "\"" && a[pos + 2] == "\""
                let (v, _) = try ml ? readMultilineBasic() : readBasicString(multiline: false)
                return Node(t: "s", v: v, l: line, m: takePending())
            }
            if c == "'" {
                let ml = pos + 2 < a.count && a[pos + 1] == "'" && a[pos + 2] == "'"
                let (v, _) = try readLiteralString(multiline: ml)
                return Node(t: "s", v: v, l: line, m: takePending())
            }
            if c == "[" { return try readArray(depth: depth) }
            if c == "{" { return try readInlineTable(depth: depth) }
            if c == "t", matchWord("true") {
                return Node(t: "b", v: "true", l: line, m: takePending())
            }
            if c == "f", matchWord("false") {
                return Node(t: "b", v: "false", l: line, m: takePending())
            }
            let window = substr(pos, pos + 40)
            let ns = window as NSString
            if let m = Walker.dateRe.firstMatch(in: window, range: NSRange(location: 0, length: ns.length)),
                m.range.location == 0, m.range.length > 0
            {
                let v = ns.substring(with: m.range)
                pos += m.range.length
                return Node(t: "s", v: v, l: line, m: takePending())
            }
            let num = try readNumber()
            return Node(t: "n", v: num, l: line, m: takePending())
        }

        func matchWord(_ word: String) -> Bool {
            let w = Array(word)
            if pos + w.count > a.count { return false }
            for k in 0..<w.count where a[pos + k] != w[k] { return false }
            let after = pos + w.count
            if after < a.count {
                let c = a[after]
                if c.isLetter || c.isNumber || c == "_" || c == "-" { return false }
            }
            pos += w.count
            return true
        }

        func readNumber() throws -> String {
            let s = pos
            if !eof, a[pos] == "+" || a[pos] == "-" { pos += 1 }
            if pos + 1 < a.count, a[pos] == "0",
                a[pos + 1] == "x" || a[pos + 1] == "X" || a[pos + 1] == "o" || a[pos + 1] == "O"
                    || a[pos + 1] == "b" || a[pos + 1] == "B"
            {
                let base = a[pos + 1]
                pos += 2
                let d = pos
                while !eof {
                    let c = a[pos]
                    let ok: Bool
                    switch base {
                    case "x", "X": ok = c.isHexDigit || c == "_"
                    case "o", "O": ok = (c >= "0" && c <= "7") || c == "_"
                    default: ok = c == "0" || c == "1" || c == "_"
                    }
                    if !ok { break }
                    pos += 1
                }
                if pos == d { throw fail("Bad number.") }
                return substr(s, pos)
            }
            if tryConsumeWord("inf") || tryConsumeWord("INF") || tryConsumeWord("nan") || tryConsumeWord("NAN") {
                return substr(s, pos)
            }
            let ds = pos
            while !eof, a[pos].isNumber || a[pos] == "_" { pos += 1 }
            if pos == ds { throw fail("Bad number.") }
            if !eof, a[pos] == "." {
                pos += 1
                let fs = pos
                while !eof, a[pos].isNumber || a[pos] == "_" { pos += 1 }
                if pos == fs { throw fail("Bad number.") }
            }
            if !eof, a[pos] == "e" || a[pos] == "E" {
                let e = pos
                pos += 1
                if !eof, a[pos] == "+" || a[pos] == "-" { pos += 1 }
                let xs = pos
                while !eof, a[pos].isNumber || a[pos] == "_" { pos += 1 }
                if pos == xs { pos = e } // lone 'e' ends the number here
            }
            return substr(s, pos)
        }

        func tryConsumeWord(_ word: String) -> Bool {
            var p = pos
            for ch in word {
                if p >= a.count { return false }
                if String(a[p]).lowercased() != String(ch).lowercased() { return false }
                p += 1
            }
            pos = p
            return true
        }

        func readArray(depth: Int) throws -> Node {
            let line = lineOf(pos)
            pos += 1 // [
            let node = Node(t: "a", l: line, c: [], m: takePending())
            skipGap()
            if !eof, a[pos] == "]" { pos += 1; node.e = takePending(); return node }
            while true {
                let child = try readValue(depth: depth + 1)
                node.c!.append(child)
                ate(child)
                if trunc, node.c!.count >= TomlTree.maxKidsAfterTrunc {
                    pending = []
                    last = nil
                    skipToClose(close: "]", open: "[")
                    return node
                }
                skipGap()
                if eof { throw fail("Unterminated array.") }
                if a[pos] == "]" { pos += 1; node.e = takePending(); return node }
                if a[pos] != "," { throw fail("Expected ',' or ']'.") }
                pos += 1 // ,
                skipGap()
                if !eof, a[pos] == "]" { pos += 1; node.e = takePending(); return node } // trailing comma
            }
        }

        func readInlineTable(depth: Int) throws -> Node {
            let line = lineOf(pos)
            pos += 1 // {
            let node = Node(t: "o", l: line, c: [], m: takePending())
            skipGap()
            if !eof, a[pos] == "}" { pos += 1; node.e = takePending(); return node }
            while true {
                let keys = try readDottedKey()
                skipInline()
                if eof || a[pos] != "=" { throw fail("Expected '='.") }
                pos += 1
                skipInline()
                let val = try readValue(depth: depth + 1)
                try assignInto(table: node, keys: keys, val: val)
                if trunc, node.c!.count >= TomlTree.maxKidsAfterTrunc {
                    pending = []
                    last = nil
                    skipToClose(close: "}", open: "{")
                    return node
                }
                skipGap()
                if eof { throw fail("Unterminated inline table.") }
                if a[pos] == "}" { pos += 1; node.e = takePending(); return node }
                if a[pos] != "," { throw fail("Expected ',' or '}'.") }
                pos += 1
                skipGap()
            }
        }

        func readKeyValue() throws -> ([(String, Int)], Node) {
            let keys = try readDottedKey()
            skipInline()
            if eof || a[pos] != "=" { throw fail("Expected '='.") }
            pos += 1
            skipInline()
            let val = try readValue(depth: 0)
            return (keys, val)
        }

        // MARK: - Tables

        func readTableHeader(root: Node) throws -> Node {
            pos += 1 // [
            let isArray = !eof && a[pos] == "["
            if isArray { pos += 1 }
            let parts = try readDottedKey()
            skipInline()
            if isArray {
                if eof || a[pos] != "]" || pos + 1 >= a.count || a[pos + 1] != "]" {
                    throw fail("Expected ']]'.")
                }
                pos += 2
                return try navigateArray(root: root, parts: parts)
            }
            if eof || a[pos] != "]" { throw fail("Expected ']'.") }
            pos += 1
            return try navigateTable(root: root, parts: parts, define: true)
        }

        func descend(node: Node, key: String) -> Node {
            if node.c == nil { node.c = [] }
            if let ch = node.c!.first(where: { $0.k == key }) { return ch }
            let n = Node(t: "o", l: lineOf(pos), c: [])
            n.k = key
            node.c!.append(n)
            return n
        }

        func navigateTable(root: Node, parts: [(String, Int)], define: Bool) throws -> Node {
            var node = root
            var prefix = ""
            for i in 0..<parts.count {
                let (key, keyLine) = parts[i]
                prefix += "/" + key.replacingOccurrences(of: "~", with: "~0").replacingOccurrences(of: "/", with: "~1")
                let isLast = i == parts.count - 1
                if node.t == "a" {
                    if node.c == nil || node.c!.isEmpty { throw fail("Array is empty.") }
                    node = node.c!.last!
                    continue
                }
                if node.t != "o" { throw fail("'\(key)' is already a value.") }
                var next = node.c?.first { $0.k == key }
                if next == nil {
                    next = Node(t: "o", l: keyLine, c: [], m: takePending())
                    next!.k = key
                    node.c!.append(next!)
                } else if next!.t == "a" {
                    if !isLast { node = next!; continue }
                    throw fail("'\(key)' is already an array.")
                } else if next!.t != "o" {
                    throw fail("'\(key)' is already a value.")
                }
                if isLast && define {
                    if definedTables.contains(prefix) { throw fail("Table '\(key)' redefined.") }
                    definedTables.insert(prefix)
                }
                node = next!
            }
            return node
        }

        func navigateArray(root: Node, parts: [(String, Int)]) throws -> Node {
            var node = root
            for i in 0..<parts.count {
                let (key, keyLine) = parts[i]
                let isLast = i == parts.count - 1
                if node.t == "a" {
                    if node.c == nil || node.c!.isEmpty { throw fail("Array is empty.") }
                    node = node.c!.last!
                    continue
                }
                if node.t != "o" { throw fail("'\(key)' is already defined.") }
                var next = node.c?.first { $0.k == key }
                if next == nil {
                    if !isLast { node = descend(node: node, key: key); continue }
                    next = Node(t: "a", l: keyLine, c: [])
                    next!.k = key
                    node.c!.append(next!)
                } else if next!.t != "a" {
                    throw fail("'\(key)' is already defined.")
                }
                if isLast {
                    let el = Node(t: "o", l: keyLine, c: [], m: takePending())
                    next!.c!.append(el)
                    ate(el)
                    return el
                }
                node = next!
            }
            throw fail("Bad array header.")
        }

        func assign(table: Node, keys: [(String, Int)], val: Node) throws {
            var node = table
            for i in 0..<(keys.count - 1) {
                let key = keys[i].0
                if node.t == "a" { throw fail("Cannot navigate into an array with a dotted key.") }
                if node.t != "o" { throw fail("'\(key)' is already a value.") }
                var next = node.c?.first { $0.k == key }
                if next == nil {
                    next = Node(t: "o", l: keys[i].1, c: [])
                    next!.k = key
                    node.c!.append(next!)
                } else if next!.t != "o" {
                    throw fail("'\(key)' is already defined.")
                }
                node = next!
            }
            let lastKey = keys.last!.0
            if node.t == "a" { throw fail("Cannot navigate into an array with a dotted key.") }
            if node.c != nil, node.c!.contains(where: { $0.k == lastKey }) {
                throw fail("Key '\(lastKey)' defined more than once.")
            }
            if node.c == nil { node.c = [] }
            val.k = lastKey
            node.c!.append(val)
            ate(val)
        }

        func assignInto(table: Node, keys: [(String, Int)], val: Node) throws {
            var node = table
            for i in 0..<(keys.count - 1) {
                let key = keys[i].0
                var next = node.c?.first { $0.k == key }
                if next == nil {
                    next = Node(t: "o", l: keys[i].1, c: [])
                    next!.k = key
                    node.c!.append(next!)
                } else if next!.t != "o" {
                    throw fail("'\(key)' is already defined.")
                }
                node = next!
            }
            let lastKey = keys.last!.0
            if node.c != nil, node.c!.contains(where: { $0.k == lastKey }) {
                throw fail("Key '\(lastKey)' defined more than once.")
            }
            if node.c == nil { node.c = [] }
            val.k = lastKey
            node.c!.append(val)
            ate(val)
        }

        /// Best effort: skip the current value when the node cap hits.
        func skipValue() {
            if eof { return }
            if pos + 2 < a.count,
                (a[pos] == "\"" && a[pos + 1] == "\"" && a[pos + 2] == "\"")
                    || (a[pos] == "'" && a[pos + 1] == "'" && a[pos + 2] == "'")
            {
                let q = a[pos]
                var k = pos + 3
                while k + 2 < a.count {
                    if a[k] == q, a[k + 1] == q, a[k + 2] == q {
                        pos = k + 3
                        return
                    }
                    k += 1
                }
                pos = a.count
                return
            }
            if a[pos] == "[" { skipToClose(close: "]", open: "["); if !eof { pos += 1 }; return }
            if a[pos] == "{" { skipToClose(close: "}", open: "{"); if !eof { pos += 1 }; return }
            while !eof, a[pos] != "\n" { pos += 1 }
        }

        func skipToClose(close: Character, open: Character) {
            var d = 0
            var inStr = false
            var q: Character = "\0"
            while !eof {
                let c = a[pos]
                if inStr {
                    if c == "\\" { pos += 1 }
                    else if c == q { inStr = false }
                } else if c == "\"" || c == "'" {
                    // Triple-quoted runs count as one opener.
                    if pos + 2 < a.count, a[pos + 1] == c, a[pos + 2] == c {
                        d += 1
                        let triple = String([c, c, c])
                        let rest = String(a[(pos + 3)...])
                        if let r = rest.range(of: triple) {
                            pos += 3 + rest.distance(from: rest.startIndex, to: r.upperBound)
                        } else {
                            pos = a.count
                        }
                        continue
                    } else {
                        inStr = true
                        q = c
                    }
                } else if c == open {
                    d += 1
                } else if c == close {
                    if d == 0 { return }
                    d -= 1
                } else if c == "#" {
                    while !eof, a[pos] != "\n" { pos += 1 }
                    continue
                }
                pos += 1
            }
        }
    }
}

/// Tolerant TOML scanner for edit mode (NSTextView), mirroring Windows'
/// TomlHighlight.cs (2.10.0): bare/quoted keys, table headers, strings
/// (incl. multi-line), numbers, booleans, datetime-ish text and # comments.
enum TomlHighlight {
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
            func scanString(_ i0: Int, _ q: UInt16) -> Int {
                var i = i0 + 1
                while i < n {
                    if u16[i] == 0x5C, q == 0x22 { i += 2; continue }
                    if u16[i] == q { return i + 1 }
                    if u16[i] == 0x0A { return i }
                    i += 1
                }
                return i
            }
            func scanTriple(_ i0: Int, _ q: UInt16) -> Int {
                var k = i0 + 3
                while k + 2 < n {
                    if u16[k] == q, u16[k + 1] == q, u16[k + 2] == q { return k + 3 }
                    k += 1
                }
                return n
            }
            func isBareStart(_ c: UInt16) -> Bool {
                c == 0x5F || (c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A)
                    || (c >= 0x30 && c <= 0x39)
            }
            func isBareChar(_ c: UInt16) -> Bool { isBareStart(c) || c == 0x2D }
            func matchWord(_ i: Int, _ w: String) -> Bool {
                let a = Array(w.utf16)
                guard i + a.count <= n else { return false }
                for k in 0..<a.count where u16[i + k] != a[k] { return false }
                let after = i + a.count
                if after < n {
                    let c = u16[after]
                    if c == 0x5F || c == 0x2D || (c >= 0x30 && c <= 0x39)
                        || (c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A) { return false }
                }
                return true
            }
            // Full-line table headers: [table] and [[array]] (always single-line).
            var ls = 0
            while ls < n {
                var le = ls
                while le < n, u16[le] != 0x0A { le += 1 }
                var p = ls
                while p < le, u16[p] == 0x20 || u16[p] == 0x09 { p += 1 }
                if p < le, u16[p] == 0x5B {
                    var q = le - 1
                    while q > p, u16[q] == 0x20 || u16[q] == 0x09 || u16[q] == 0x0D { q -= 1 }
                    if q > p, u16[q] == 0x5D {
                        var hasHash = false
                        var h = p
                        while h <= q {
                            if u16[h] == 0x23 { hasHash = true; break }
                            h += 1
                        }
                        if !hasHash {
                            ops.append((NSRange(location: p, length: q - p + 1), pal.jsonKey, baseFont))
                        }
                    }
                }
                ls = le + 1
            }
            var i = 0
            while i < n {
                let c = u16[i]
                if c == 0x23 { // #
                    let s = i
                    while i < n, u16[i] != 0x0A { i += 1 }
                    ops.append((NSRange(location: s, length: i - s), pal.jsonComment, italic))
                } else if c == 0x22 { // "
                    let s = i
                    if i + 2 < n, u16[i + 1] == 0x22, u16[i + 2] == 0x22 { i = scanTriple(i, 0x22) }
                    else { i = scanString(i, 0x22) }
                    if i > s { ops.append((NSRange(location: s, length: i - s), pal.jsonString, baseFont)) }
                } else if c == 0x27 { // '
                    let s = i
                    if i + 2 < n, u16[i + 1] == 0x27, u16[i + 2] == 0x27 {
                        var k = i + 3
                        var done = false
                        while k + 2 < n {
                            if u16[k] == 0x27, u16[k + 1] == 0x27, u16[k + 2] == 0x27 {
                                i = k + 3; done = true; break
                            }
                            k += 1
                        }
                        if !done { i = n }
                    } else {
                        i = scanString(i, 0x27)
                    }
                    if i > s { ops.append((NSRange(location: s, length: i - s), pal.jsonString, baseFont)) }
                } else if c == 0x5B || c == 0x5D || c == 0x7B || c == 0x7D { // brackets
                    ops.append((NSRange(location: i, length: 1), pal.jsonKey, baseFont))
                    i += 1
                } else if isBareStart(c) {
                    // Bare word (maybe dotted): a key when followed by '='.
                    let s = i
                    while true {
                        while i < n, isBareChar(u16[i]) { i += 1 }
                        var j = i
                        while j < n, u16[j] == 0x20 || u16[j] == 0x09 { j += 1 }
                        if j < n, u16[j] == 0x2E {
                            i = j + 1
                            while i < n, u16[i] == 0x20 || u16[i] == 0x09 { i += 1 }
                            continue
                        }
                        if j < n, u16[j] == 0x3D {
                            ops.append((NSRange(location: s, length: i - s), pal.jsonKey, baseFont))
                            i = j + 1 // consume '=' too: nothing to paint for it
                        }
                        break
                    }
                } else if matchWord(i, "true") || matchWord(i, "false") {
                    let len = c == 0x74 ? 4 : 5
                    ops.append((NSRange(location: i, length: len), pal.jsonBool, baseFont))
                    i += len
                } else if matchWord(i, "null") {
                    ops.append((NSRange(location: i, length: 4), pal.jsonNull, italic))
                    i += 4
                } else if (c >= 0x30 && c <= 0x39)
                    || ((c == 0x2B || c == 0x2D) && i + 1 < n
                        && (u16[i + 1] >= 0x30 && u16[i + 1] <= 0x39 || u16[i + 1] == 0x69 || u16[i + 1] == 0x6E))
                {
                    let s = i
                    if c == 0x2B || c == 0x2D { i += 1 }
                    let ds = i
                    while i < n {
                        let d = u16[i]
                        if (d >= 0x30 && d <= 0x39) || (d >= 0x41 && d <= 0x5A) || (d >= 0x61 && d <= 0x7A)
                            || d == 0x5F || d == 0x2E || d == 0x2B || d == 0x2D || d == 0x3A { i += 1 } else { break }
                    }
                    var hasDash = false, hasColon = false
                    var t = s
                    while t < i {
                        if u16[t] == 0x2D { hasDash = true }
                        if u16[t] == 0x3A { hasColon = true }
                        t += 1
                    }
                    // (leading +/- is part of the token only for datetimes here)
                    if hasDash || hasColon {
                        ops.append((NSRange(location: s, length: i - s), pal.jsonString, baseFont))
                    } else if i > ds {
                        ops.append((NSRange(location: s, length: i - s), pal.jsonNumber, baseFont))
                    }
                } else if matchWord(i, "inf") || matchWord(i, "nan") {
                    ops.append((NSRange(location: i, length: 3), pal.jsonNumber, baseFont))
                    i += 3
                } else {
                    i += 1
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
}
