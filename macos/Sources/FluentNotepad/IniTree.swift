import AppKit
import Foundation

/// INI-like (.ini/.cfg/.inf/.properties/.env*) -> collapsible tree HTML on
/// the shared PreviewTree schema: sections are objects, keys are leaves
/// (values kept verbatim, uninterpreted). Port of Windows' IniPreviewHtml.cs +
/// IniHighlight.cs (2.10.0): `=`/`:` separators, `;`/`#` comments, `\` line
/// continuations, duplicate sections merged. Copy paths use the `$.a.b` form.
enum IniTree {
    typealias Node = JsonTree.Node
    typealias ParseError = PreviewTree.ParseError

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?, open: Set<String>) -> String {
        if text.count > PreviewTree.maxChars {
            return PreviewTree.tooLargePage(dark: dark, fontSize: fontSize, title: title ?? "INI",
                message: L("iniTooLarge"))
        }
        do {
            let (root, truncated) = try parse(text)
            let enc = JSONEncoder()
            return PreviewTree.treePage(dark: dark, fontSize: fontSize, title: title ?? "INI",
                rootJSON: String(data: try enc.encode(root), encoding: .utf8) ?? "null",
                openJSON: String(data: try enc.encode(open.sorted()), encoding: .utf8) ?? "[]",
                truncated: truncated, truncText: L("iniTruncated"), isXML: false)
        } catch let ex as ParseError {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("iniInvalidTitle"),
                invalidLine: String(format: L("iniInvalid"), ex.line, ex.column),
                details: ex.message, source: text, hint: L("iniErrorHint"))
        } catch {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("iniInvalidTitle"), invalidLine: "", details: "\(error)",
                source: text, hint: L("iniErrorHint"))
        }
    }

    // MARK: - Parse (line-based, 1-based lines)

    static func parse(_ text: String) throws -> (Node, Bool) {
        var truncated = false
        let norm = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        if norm.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ParseError(line: 1, column: 1, message: "The document is empty.")
        }
        let lines = norm.components(separatedBy: "\n")
        let root = Node(t: "o", l: 1, c: [])
        var cur = root
        var pending: [String] = []
        var last: Node?
        var lastLine = 0
        var count = 0

        func ate(_ n: Node, _ line: Int) { last = n; lastLine = line }
        func comment(_ raw: String, _ line: Int) {
            if last != nil, line == lastLine { last!.e = (last!.e ?? []) + [raw] }
            else { pending.append(raw) }
        }
        func endsWithContinuation(_ s: String) -> Bool {
            var backslashes = 0
            for c in s.reversed() {
                if c != "\\" { break }
                backslashes += 1
            }
            return backslashes % 2 == 1
        }
        func splitInlineComment(_ s: String) -> (String, String?) {
            var inS = false, inD = false
            let a = Array(s)
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS { if c == "'" { inS = false }; i += 1; continue }
                if inD {
                    if c == "\\" { i += 2; continue }
                    if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if (c == ";" || c == "#"), i > 0, (a[i - 1] == " " || a[i - 1] == "\t") {
                    return (String(a[..<i]).trimmingCharacters(in: .whitespaces), String(a[i...]).trimmingCharacters(in: .whitespaces))
                }
                i += 1
            }
            return (s, nil)
        }

        var i = 0
        while i < lines.count {
            var raw = lines[i]
            var line = i + 1
            i += 1
            // Continuation: trailing unescaped backslash joins the next line.
            while endsWithContinuation(raw), i < lines.count {
                raw = String(raw.dropLast()) + lines[i].trimmingCharacters(in: .whitespaces)
                line = i + 1
                i += 1
            }
            let t = raw.trimmingCharacters(in: .whitespaces)
            if t.isEmpty { continue }
            let f = t.first!
            if f == ";" || f == "#" { comment(t, line); continue }
            if f == "[" {
                guard let end = t.firstIndex(of: "]") else {
                    throw ParseError(line: line, column: 1, message: "Unclosed section header.")
                }
                let rest = String(t[t.index(after: end)...]).trimmingCharacters(in: .whitespaces)
                if !rest.isEmpty, rest.first! != ";", rest.first! != "#" {
                    throw ParseError(line: line, column: 1, message: "Unexpected content after section header.")
                }
                let name = String(t[t.index(after: t.startIndex)..<end]).trimmingCharacters(in: .whitespaces)
                if name.isEmpty {
                    throw ParseError(line: line, column: 1, message: "Empty section name.")
                }
                // Duplicate sections merge (union of keys).
                var sec = root.c!.first { $0.k == name && $0.t == "o" }
                if sec == nil {
                    sec = Node(k: name, t: "o", l: line, c: [])
                    if !pending.isEmpty { sec!.m = pending; pending = [] }
                    root.c!.append(sec!)
                }
                cur = sec!
                ate(sec!, line)
                if !rest.isEmpty { comment(rest, line) }
                continue
            }
            // key = value (first unquoted = or :)
            let a = Array(t)
            var sep = -1
            var inS = false, inD = false
            var k = 0
            while k < a.count {
                let c = a[k]
                if inS { if c == "'" { inS = false }; k += 1; continue }
                if inD {
                    if c == "\\" { k += 2; continue }
                    if c == "\"" { inD = false }
                    k += 1
                    continue
                }
                if c == "'" { inS = true; k += 1; continue }
                if c == "\"" { inD = true; k += 1; continue }
                if c == "=" || c == ":" { sep = k; break }
                k += 1
            }
            if sep < 0 { throw ParseError(line: line, column: 1, message: "Expected '=' or ':'.") }
            let key = String(a[..<sep]).trimmingCharacters(in: .whitespaces)
            if key.isEmpty { throw ParseError(line: line, column: 1, message: "Expected a key.") }
            let (val, trail) = splitInlineComment(String(a[(sep + 1)...]).trimmingCharacters(in: .whitespaces))
            let node = Node(t: "s", v: val, l: line)
            node.k = key
            if !pending.isEmpty { node.m = pending; pending = [] }
            if let trail { node.e = (node.e ?? []) + [trail] }
            if cur.c == nil { cur.c = [] }
            cur.c!.append(node)
            ate(node, line)
            count += 1
            if count > PreviewTree.maxNodes {
                truncated = true
                break
            }
        }
        if !pending.isEmpty { root.e = (root.e ?? []) + pending }
        return (root, truncated)
    }
}

/// Line-based INI scanner for edit mode (NSTextView): sections, keys, quoted
/// strings and ;/# comments. Values stay in the base tone. Port of Windows'
/// IniHighlight.cs (2.10.0).
enum IniHighlight {
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
            func isWS(_ c: UInt16) -> Bool { c == 0x20 || c == 0x09 }
            func quotedEnd(_ s: [UInt16], _ start: Int, _ end: Int) -> Int {
                let q = s[start]
                var i = start + 1
                while i < end {
                    if q == 0x22, s[i] == 0x5C { i += 2; continue }
                    if s[i] == q {
                        if q == 0x27, i + 1 < end, s[i + 1] == 0x27 { i += 2; continue }
                        return i + 1
                    }
                    i += 1
                }
                return i // unterminated: highlight to EOL
            }
            var ls = 0
            while ls < n {
                var le = ls
                while le < n, u16[le] != 0x0A { le += 1 }
                var p = ls
                while p < le, isWS(u16[p]) { p += 1 }
                if p < le, u16[p] == 0x3B || u16[p] == 0x23 { // ; #
                    ops.append((NSRange(location: p, length: le - p), pal.jsonComment, italic))
                } else if p < le, u16[p] == 0x5B { // [
                    var close = -1
                    var q = p
                    while q < le {
                        if u16[q] == 0x5D { close = q; break }
                        q += 1
                    }
                    if close >= 0 {
                        ops.append((NSRange(location: p, length: close - p + 1), pal.jsonKey, baseFont))
                        var r = close + 1
                        while r < le, isWS(u16[r]) { r += 1 }
                        if r < le, u16[r] == 0x3B || u16[r] == 0x23 {
                            ops.append((NSRange(location: r, length: le - r), pal.jsonComment, italic))
                        }
                    }
                } else if p < le {
                    var sep = -1
                    var q = p
                    var inS = false, inD = false
                    while q < le {
                        let c = u16[q]
                        if inS { if c == 0x27 { inS = false }; q += 1; continue }
                        if inD {
                            if c == 0x5C { q += 2; continue }
                            if c == 0x22 { inD = false }
                            q += 1
                            continue
                        }
                        if c == 0x27 { inS = true; q += 1; continue }
                        if c == 0x22 { inD = true; q += 1; continue }
                        if c == 0x3D || c == 0x3A { sep = q; break }
                        q += 1
                    }
                    if sep > p {
                        var ke = sep
                        while ke > p, isWS(u16[ke - 1]) { ke -= 1 }
                        ops.append((NSRange(location: p, length: ke - p), pal.jsonKey, baseFont))
                        var v = sep + 1
                        while v < le, isWS(u16[v]) { v += 1 }
                        if v < le, u16[v] == 0x22 || u16[v] == 0x27 {
                            let ve = quotedEnd(u16, v, le)
                            ops.append((NSRange(location: v, length: ve - v), pal.jsonString, baseFont))
                        } else {
                            var cp = -1
                            var qs = false, qd = false
                            var kk = v
                            while kk < le {
                                let c = u16[kk]
                                if qs { if c == 0x27 { qs = false }; kk += 1; continue }
                                if qd {
                                    if c == 0x5C { kk += 2; continue }
                                    if c == 0x22 { qd = false }
                                    kk += 1
                                    continue
                                }
                                if c == 0x27 { qs = true; kk += 1; continue }
                                if c == 0x22 { qd = true; kk += 1; continue }
                                if (c == 0x3B || c == 0x23), kk > v, isWS(u16[kk - 1]) { cp = kk; break }
                                kk += 1
                            }
                            if cp >= 0 {
                                ops.append((NSRange(location: cp, length: le - cp), pal.jsonComment, italic))
                            }
                        }
                    }
                }
                ls = le + 1
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
