import AppKit
import Foundation

/// YAML (.yaml/.yml) -> collapsible tree HTML on the shared PreviewTree
/// schema. Pragmatic indentation-based subset like Windows' YamlPreviewHtml.cs
/// (2.10.0, ported here in full): block mappings/sequences/scalars, flow
/// collections (even multi-line), comments, plain and quoted scalars,
/// documents. Anchors/aliases/tags are stripped, not resolved; merge keys
/// (`<<`) stay plain keys; complex (`?` ...) keys and tabs in indentation
/// are rejected with a line number. Copy paths use the $.a.b[2] form.
enum YamlTree {
    typealias Node = JsonTree.Node
    typealias ParseError = PreviewTree.ParseError

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?, open: Set<String>) -> String {
        if text.count > PreviewTree.maxChars {
            return PreviewTree.tooLargePage(dark: dark, fontSize: fontSize, title: title ?? "YAML",
                message: L("yamlTooLarge"))
        }
        do {
            let (root, truncated) = try parse(text)
            let enc = JSONEncoder()
            return PreviewTree.treePage(dark: dark, fontSize: fontSize, title: title ?? "YAML",
                rootJSON: String(data: try enc.encode(root), encoding: .utf8) ?? "null",
                openJSON: String(data: try enc.encode(open.sorted()), encoding: .utf8) ?? "[]",
                truncated: truncated, truncText: L("yamlTruncated"), isXML: false)
        } catch let ex as ParseError {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("yamlInvalidTitle"),
                invalidLine: String(format: L("yamlInvalid"), ex.line, ex.column),
                details: ex.message, source: text, hint: L("yamlErrorHint"))
        } catch {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("yamlInvalidTitle"), invalidLine: "", details: "\(error)",
                source: text, hint: L("yamlErrorHint"))
        }
    }

    // MARK: - Parse (line-based, 1-based lines)

    static func parse(_ text: String) throws -> (Node, Bool) {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ParseError(line: 1, column: 1, message: "The YAML document is empty.")
        }
        let norm = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        let w = Walker(norm.components(separatedBy: "\n"))
        var docs: [Node] = []
        try w.skipBlank()
        if w.eof { throw ParseError(line: 1, column: 1, message: "The YAML document is empty.") }
        while !w.eof {
            if let doc = try w.parseDoc() { docs.append(doc) }
            try w.skipBlank()
        }
        if docs.isEmpty { throw ParseError(line: 1, column: 1, message: "The YAML document is empty.") }
        if !w.pending.isEmpty {
            docs[docs.count - 1].e = (docs[docs.count - 1].e ?? []) + w.takePending()!
        }
        if docs.count == 1 { return (docs[0], w.trunc) }
        let root = Node(t: "o", l: 1, c: [])
        for d in docs { root.c!.append(d) }
        return (root, w.trunc)
    }

    final class Walker {
        static let blockHeadRe = try! NSRegularExpression(pattern: #"^[|>]\s*([+-]?\d+|\d+[+-]?)?\s*(#.*)?$"#)

        var lines: [String]
        var idx = 0
        var count = 0
        var trunc = false
        var pending: [String] = []
        var last: Node?
        var lastLine = 0

        init(_ lines: [String]) { self.lines = lines }

        var eof: Bool { idx >= lines.count }
        var line: Int { idx + 1 } // 1-based
        var cur: String { eof ? "" : lines[idx] }

        func fail(_ msg: String, _ line: Int? = nil) -> ParseError {
            ParseError(line: line ?? self.line, column: 1, message: msg)
        }

        func takePending() -> [String]? {
            guard !pending.isEmpty else { return nil }
            let l = pending; pending = []; return l
        }

        func ate(_ n: Node, _ line: Int) {
            last = n
            lastLine = line
            count += 1
            if count > PreviewTree.maxNodes {
                trunc = true
                pending = []
                last = nil
                idx = lines.count // abandon the rest; levels unwind with partial trees
            }
        }

        func ateComment(_ raw: String, _ line: Int) {
            if last != nil, line == lastLine { last!.e = (last!.e ?? []) + [raw] }
            else { pending.append(raw) }
        }

        static func indentOf(_ s: String) -> Int {
            var i = 0
            for c in s {
                if c != " " { break }
                i += 1
            }
            return i
        }

        static func isBlank(_ s: String) -> Bool {
            s.allSatisfy { $0 == " " || $0 == "\t" }
        }

        static func isCommentLine(_ s: String) -> Bool {
            let a = Array(s)
            let i = indentOf(s)
            return i < a.count && a[i] == "#"
        }

        /// Blank lines and full-line comments (attached as remarks).
        func skipBlank() throws {
            while !eof {
                let s = cur
                if Self.isBlank(s) { idx += 1; continue }
                let ind = Self.indentOf(s)
                let a = Array(s)
                if ind < a.count, a[ind] == "\t" { throw fail("Tabs are not allowed for indentation.") }
                if Self.isCommentLine(s) { ateComment(s.trimmingCharacters(in: .whitespaces), line); idx += 1; continue }
                if ind == 0, s.hasPrefix("%") { idx += 1; continue } // directive: skip
                return
            }
        }

        // MARK: - Documents

        func parseDoc() throws -> Node? {
            // Optional explicit start (content after --- belongs to the doc).
            if !eof, let rest = Self.docMarker(cur, "---") {
                if rest == nil { idx += 1; try skipBlank(); if eof { return nil } }
                else { lines[idx] = rest! } // reprocess the remainder as content
            }
            if eof { return nil }
            if let restEnd = Self.docMarker(cur, "...") {
                if restEnd != nil { throw fail("Unexpected content after '...'.") }
                idx += 1
                return nil
            }
            let node = try parseBlock(parentIndent: -1, depth: 0)
            if !eof, let restEnd2 = Self.docMarker(cur, "...") {
                if restEnd2 != nil { throw fail("Unexpected content after '...'.") }
                idx += 1
            }
            return node
        }

        /// Returns "" when the marker stands alone, the remainder after it,
        /// or nil when this line is no marker.
        static func docMarker(_ s: String, _ mark: String) -> String?? {
            let t = s.trimmingCharacters(in: .whitespaces)
            if t == mark { return .some(nil) }
            if t.hasPrefix(mark + " ") || t.hasPrefix(mark + "\t") {
                return .some(String(t.dropFirst(mark.count + 1)))
            }
            return nil
        }

        // MARK: - Blocks

        func parseBlock(parentIndent: Int, depth: Int = 0) throws -> Node {
            if depth > PreviewTree.maxDepth { throw ParseError(line: line, column: 1, message: "Too deeply nested.") }
            try skipBlank()
            if eof { throw fail("Unexpected end of document.") }
            let first = cur
            let ind = Self.indentOf(first)
            if parentIndent >= 0, ind <= parentIndent { throw fail("Bad indentation.") }
            try checkNoTabs(first)
            let seq = Self.isSeqItem(first, indent: ind)
            let node = Node(t: seq ? "a" : "o", l: line, c: [], m: takePending())
            let expect = ind
            var wantSeq: Bool?
            while true {
                try skipBlank()
                if eof { break }
                let s = cur
                let ci = Self.indentOf(s)
                try checkNoTabs(s)
                if ci < expect { break }
                if ci > expect { throw fail("Bad indentation.") }
                let isSeq = Self.isSeqItem(s, indent: ci)
                if wantSeq == nil { wantSeq = isSeq }
                else if wantSeq != isSeq { throw fail("Cannot mix mapping and sequence entries.") }
                if isSeq { try parseSeqItem(seq: node, dashIndent: ci, depth: depth) }
                else { try parseMapEntry(map: node, keyIndent: ci, depth: depth) }
                if trunc { return node }
            }
            node.e = takePending()
            return node
        }

        func checkNoTabs(_ s: String) throws {
            let a = Array(s)
            let ind = Self.indentOf(s)
            for i in 0..<ind where a[i] == "\t" { throw fail("Tabs are not allowed for indentation.") }
        }

        static func isSeqItem(_ s: String, indent: Int) -> Bool {
            let a = Array(s)
            if indent >= a.count || a[indent] != "-" { return false }
            if indent + 1 >= a.count { return true } // lone "-"
            let n = a[indent + 1]
            return n == " " || n == "\t"
        }

        func parseSeqItem(seq: Node, dashIndent: Int, depth: Int) throws {
            let s = cur
            let line = self.line
            let a = Array(s)
            var p = dashIndent + 1
            while p < a.count, a[p] == " " || a[p] == "\t" { p += 1 }
            let rest0 = String(a[p...]).trimmingCharacters(in: .whitespaces)
            idx += 1 // consume the dash line
            var rest = rest0
            let (restNC, restComment) = Self.splitComment(rest)
            rest = restNC
            func attachRest(_ n: Node) {
                if let c = restComment { n.e = (n.e ?? []) + [c] }
            }
            if isFlowStart(rest) { rest = try readFlowLines(first: rest, line: line) }
            if rest.isEmpty {
                // Nested block (or null).
                if let c = restComment { ateComment(c, line) }
                try skipBlank()
                if eof || Self.indentOf(cur) <= dashIndent {
                    let nul = Node(t: "u", l: line, m: takePending())
                    seq.c!.append(nul)
                    ate(nul, line)
                    return
                }
                let child = try parseBlock(parentIndent: dashIndent, depth: depth + 1)
                seq.c!.append(child)
                ate(child, line)
                return
            }
            if Self.isBlockHead(rest) {
                let txt = try readBlockScalar(keyIndent: dashIndent, header: rest, line: line)
                let lit = Node(t: "s", v: txt, l: line, m: takePending())
                seq.c!.append(lit)
                ate(lit, line)
                attachRest(lit)
                return
            }
            if rest == "-" || rest.hasPrefix("- ") {
                // Nested sequence on one line: "- - a".
                let sub = Node(t: "a", l: line, c: [], m: takePending())
                seq.c!.append(sub)
                try parseSeqInline(sub: sub, rest: rest, dashIndent: dashIndent, line: line, depth: depth)
                ate(sub, line)
                attachRest(sub)
                return
            }
            let (kind, key, raw) = try splitInline(rest: rest, line: line)
            if kind == .mapping {
                if Self.isBlockHead(raw) {
                    let txt = try readBlockScalar(keyIndent: dashIndent, header: raw, line: line)
                    let map = Node(t: "o", l: line, c: [], m: takePending())
                    map.c!.append(Node(k: key, t: "s", v: txt, l: line))
                    seq.c!.append(map)
                    ate(map, line)
                    attachRest(map)
                    try skipBlank()
                    return
                }
                let map2 = Node(t: "o", l: line, c: [], m: takePending())
                let leaf = try scalarNode(raw: raw, line: line)
                leaf.k = key
                map2.c!.append(leaf)
                ate(leaf, line)
                try checkDup(map: map2, key: key, line: line)
                seq.c!.append(map2)
                attachRest(map2)
                try skipBlank()
                if !eof, Self.indentOf(cur) > dashIndent, !Self.isSeqItem(cur, indent: Self.indentOf(cur)) {
                    try continueMapping(map: map2, expect: Self.indentOf(cur), depth: depth)
                } else {
                    ate(map2, line)
                }
                return
            }
            let leaf2 = try scalarNode(raw: raw, line: line)
            leaf2.m = takePending()
            seq.c!.append(leaf2)
            ate(leaf2, line)
            attachRest(leaf2)
        }

        /// "- - a" then following deeper lines belong to the same sub-sequence.
        func parseSeqInline(sub: Node, rest: String, dashIndent: Int, line: Int, depth: Int) throws {
            var first = String(rest.dropFirst()).trimmingCharacters(in: .whitespaces)
            if Self.isBlockHead(first) {
                let txt = try readBlockScalar(keyIndent: dashIndent, header: first, line: line)
                let lit = Node(t: "s", v: txt, l: line)
                sub.c!.append(lit)
                ate(lit, line)
            } else {
                let (kind, key, raw) = try splitInline(rest: first, line: line)
                if kind == .mapping {
                    let map = Node(t: "o", l: line, c: [])
                    let leaf = try scalarNode(raw: raw, line: line)
                    leaf.k = key
                    map.c!.append(leaf)
                    ate(leaf, line)
                    try checkDup(map: map, key: key, line: line)
                    sub.c!.append(map)
                } else {
                    let leaf = try scalarNode(raw: raw, line: line)
                    sub.c!.append(leaf)
                    ate(leaf, line)
                }
            }
            try skipBlank()
            while !eof, Self.indentOf(cur) > dashIndent {
                let s = cur
                let ci = Self.indentOf(s)
                if !Self.isSeqItem(s, indent: ci) { break }
                let sa = Array(s)
                var r2 = String(sa[(ci + 1)...]).trimmingCharacters(in: .whitespaces)
                idx += 1
                let (r2n, r2c) = Self.splitComment(r2)
                r2 = r2n
                if r2.isEmpty {
                    if let c = r2c { ateComment(c, line) }
                    try skipBlank()
                    if !eof, Self.indentOf(cur) > dashIndent {
                        let cn = try parseBlock(parentIndent: dashIndent, depth: depth + 1)
                        sub.c!.append(cn)
                        ate(cn, line)
                    } else {
                        let un = Node(t: "u", l: line)
                        sub.c!.append(un)
                        ate(un, line)
                    }
                    continue
                }
                if isFlowStart(r2) { r2 = try readFlowLines(first: r2, line: line) }
                let (k2, key2, raw2) = try splitInline(rest: r2, line: line)
                if k2 == .mapping {
                    let map = Node(t: "o", l: line, c: [])
                    let leaf = try scalarNode(raw: raw2, line: line)
                    leaf.k = key2
                    map.c!.append(leaf)
                    try checkDup(map: map, key: key2, line: line)
                    sub.c!.append(map)
                    ate(map, line)
                    if let c = r2c { map.e = (map.e ?? []) + [c] }
                } else {
                    let sl = try scalarNode(raw: raw2, line: line)
                    sub.c!.append(sl)
                    ate(sl, line)
                    if let c = r2c { sl.e = (sl.e ?? []) + [c] }
                }
            }
        }

        func continueMapping(map: Node, expect: Int, depth: Int) throws {
            while true {
                try skipBlank()
                if eof { break }
                let s = cur
                let ci = Self.indentOf(s)
                if ci < expect { break }
                if ci > expect { throw fail("Bad indentation.") }
                if Self.isSeqItem(s, indent: ci) { throw fail("Cannot mix mapping and sequence entries.") }
                try parseMapEntry(map: map, keyIndent: ci, depth: depth)
                if trunc { return }
            }
            if let extra = takePending() { map.e = (map.e ?? []) + extra }
        }

        func parseMapEntry(map: Node, keyIndent: Int, depth: Int) throws {
            let s = cur
            let line = self.line
            // Trailing comment on the key line belongs to the value being defined.
            let (_, keyComment) = Self.splitComment(String(Array(s)[keyIndent...]))
            func attachComment() {
                if let c = keyComment, last != nil { last!.e = (last!.e ?? []) + [c] }
            }
            let (key, after) = try splitKey(s: s, keyIndent: keyIndent)
            idx += 1 // consume the key line
            if after == nil {
                // No value on this line: nested block or null.
                try skipBlank()
                if !eof, Self.indentOf(cur) > keyIndent,
                    Self.docMarker(cur, "---") == nil, Self.docMarker(cur, "...") == nil
                {
                    let child = try parseBlock(parentIndent: keyIndent, depth: depth + 1)
                    child.k = key
                    map.c!.append(child)
                    ate(child, line)
                    attachComment()
                    try checkDup(map: map, key: key, line: line)
                    return
                }
                let n = Node(t: "u", l: line, m: takePending())
                n.k = key
                map.c!.append(n)
                ate(n, line)
                attachComment()
                try checkDup(map: map, key: key, line: line)
                return
            }
            if Self.isBlockHead(after!) {
                let txt = try readBlockScalar(keyIndent: keyIndent, header: after!, line: line)
                let n = Node(t: "s", v: txt, l: line, m: takePending())
                n.k = key
                map.c!.append(n)
                ate(n, line)
                attachComment()
                try checkDup(map: map, key: key, line: line)
                return
            }
            let flow = try readFlowLines(first: after!, line: line)
            let leaf = try scalarNode(raw: flow, line: line)
            leaf.k = key
            leaf.m = takePending()
            map.c!.append(leaf)
            ate(leaf, line)
            attachComment()
            try checkDup(map: map, key: key, line: line)
        }

        func checkDup(map: Node, key: String, line: Int) throws {
            guard let c = map.c else { return }
            var n = 0
            for ch in c where ch.k == key {
                n += 1
                if n > 1 { throw ParseError(line: line, column: 1, message: "Duplicate key '\(key)'.") }
            }
        }

        enum InlineKind { case scalar, mapping }

        /// Dash-line remainder: either a scalar/flow or the first pair of a mapping.
        func splitInline(rest: String, line: Int) throws -> (InlineKind, String, String) {
            let t = rest.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("[") || t.hasPrefix("{") { return (.scalar, "", rest) }
            if let colon = Self.colonOutside(rest) {
                let k = unquote(String(rest[..<colon]).trimmingCharacters(in: .whitespaces))
                if k.isEmpty || k.hasPrefix("?") { throw fail("Unsupported mapping key.", line) }
                return (.mapping, k, String(rest[rest.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
            }
            return (.scalar, "", rest)
        }

        /// Splits "key: value" at the key colon. Returns (key, value-or-nil).
        func splitKey(s: String, keyIndent: Int) throws -> (String, String?) {
            let a = Array(s)
            var p = keyIndent
            var inS = false, inD = false
            var depth = 0
            var i = p
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "[" || c == "{" { depth += 1; i += 1; continue }
                if c == "]" || c == "}" { depth -= 1; i += 1; continue }
                if c == ":", depth == 0 {
                    let next = i + 1 < a.count ? a[i + 1] : "\0"
                    if next == " " || next == "\t" || next == "\r" {
                        let key = stripRefs(String(a[p..<i]).trimmingCharacters(in: .whitespaces))
                        if key.isEmpty || key == "?" || key.hasPrefix("? ") {
                            throw fail("Unsupported mapping key.")
                        }
                        var rest = String(a[(i + 1)...]).trimmingCharacters(in: .whitespaces)
                        if rest.hasPrefix("#") { rest = "" } else { rest = Self.stripComment(rest) }
                        return (unquote(key), rest.isEmpty ? nil : rest)
                    }
                    if i + 1 == a.count {
                        let key = stripRefs(String(a[p..<i]).trimmingCharacters(in: .whitespaces))
                        if key.isEmpty { throw fail("Expected a key.") }
                        return (unquote(key), nil)
                    }
                }
                i += 1
            }
            throw fail("Expected ':'.")
        }

        /// First ':' outside quotes/flow followed by space/EOL (or at EOL).
        static func colonOutside(_ s: String) -> String.Index? {
            let a = Array(s)
            var inS = false, inD = false
            var depth = 0
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "[" || c == "{" { depth += 1; i += 1; continue }
                if c == "]" || c == "}" { depth -= 1; i += 1; continue }
                if c == ":", depth == 0 {
                    if i + 1 >= a.count || a[i + 1] == " " || a[i + 1] == "\t" || a[i + 1] == "\r" {
                        return s.index(s.startIndex, offsetBy: i)
                    }
                }
                i += 1
            }
            return nil
        }

        /// Strips a trailing " #comment" outside quotes.
        static func stripComment(_ s: String) -> String {
            let a = Array(s)
            var inS = false, inD = false
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "#", i > 0, a[i - 1] == " " || a[i - 1] == "\t" {
                    return String(a[..<i]).trimmingCharacters(in: .whitespaces)
                }
                i += 1
            }
            return s
        }

        /// Splits value and trailing comment (quote-aware). The comment keeps '#'.
        static func splitComment(_ s: String) -> (String, String?) {
            let a = Array(s)
            var inS = false, inD = false
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "#", i == 0 || a[i - 1] == " " || a[i - 1] == "\t" {
                    return (
                        String(a[..<i]).trimmingCharacters(in: .whitespaces),
                        String(a[i...]).trimmingCharacters(in: .whitespaces)
                    )
                }
                i += 1
            }
            return (s, nil)
        }

        /// Removes #...\n and trailing #... segments outside quotes (flow values).
        static func stripFlowComments(_ s: String) -> String {
            let a = Array(s)
            var out = ""
            var inS = false, inD = false
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    out.append(c)
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { out.append("'"); i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    out.append(c)
                    if c == "\\", i + 1 < a.count { out.append(a[i + 1]); i += 2; continue }
                    if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; out.append(c); i += 1; continue }
                if c == "\"" { inD = true; out.append(c); i += 1; continue }
                if c == "#" { while i < a.count, a[i] != "\n" { i += 1 }; continue }
                out.append(c)
                i += 1
            }
            return out
        }

        static func isBlockHead(_ v: String) -> Bool {
            let t = v.trimmingCharacters(in: .whitespaces)
            let ns = t as NSString
            return blockHeadRe.firstMatch(in: t, range: NSRange(location: 0, length: ns.length)) != nil
        }

        /// Strips leading tag/anchor/alias refs (!x, &x, *x). Returns cleaned value.
        func stripRefs(_ s: String) -> String {
            var t = s.trimmingCharacters(in: .whitespaces)
            while let f = t.first, f == "!" || f == "&" || f == "*" {
                var i = t.index(after: t.startIndex)
                while i < t.endIndex, t[i] != " ", t[i] != "\t" { i = t.index(after: i) }
                t = String(t[i...]).trimmingCharacters(in: .whitespaces)
            }
            return t
        }

        func unquote(_ s: String) -> String {
            var s = s.trimmingCharacters(in: .whitespaces)
            if s.count >= 2, s.first == "\"", s.last == "\"" {
                var out = ""
                let a = Array(s)
                var i = 1
                while i < a.count - 1 {
                    if a[i] == "\\", i + 1 < a.count - 1 {
                        i += 1
                        switch a[i] {
                        case "n": out.append("\n")
                        case "t": out.append("\t")
                        case "\"": out.append("\"")
                        case "\\": out.append("\\")
                        default: out.append("\\"); out.append(a[i])
                        }
                    } else if a[i] == "\"", i + 1 < a.count - 1, a[i + 1] == "\"" {
                        out.append("\""); i += 1
                    } else {
                        out.append(a[i])
                    }
                    i += 1
                }
                return out
            }
            if s.count >= 2, s.first == "'", s.last == "'" {
                s.removeFirst(); s.removeLast()
                return s.replacingOccurrences(of: "''", with: "'")
            }
            return s
        }

        /// If a flow collection opens here but doesn't close on this line,
        /// consume following lines (joined with \n) until balanced.
        func isFlowStart(_ s: String) -> Bool {
            let t = s.trimmingCharacters(in: .whitespaces)
            return t.hasPrefix("[") || t.hasPrefix("{")
        }

        func readFlowLines(first: String, line: Int) throws -> String {
            let t = first.trimmingCharacters(in: .whitespaces)
            if (!t.hasPrefix("[") && !t.hasPrefix("{")) || Self.flowBalanced(t) {
                return first
            }
            var sb = first
            let startIdx = line - 1
            while idx < lines.count {
                let ln = lines[idx]
                // Whole-line comments inside a flow: keep them as remarks.
                if ln.trimmingCharacters(in: .whitespaces).hasPrefix("#") {
                    ateComment(ln.trimmingCharacters(in: .whitespaces), idx + 1)
                    idx += 1
                    continue
                }
                sb += "\n" + ln
                idx += 1
                if Self.flowBalanced(Self.stripFlowComments(sb)) { break }
                if idx - startIdx > 500 { throw fail("Unbalanced flow collection.", line) }
            }
            if !Self.flowBalanced(Self.stripFlowComments(sb)) {
                throw fail("Unbalanced flow collection.", line)
            }
            return sb
        }

        static func flowBalanced(_ s: String) -> Bool {
            let a = Array(s)
            var inS = false, inD = false
            var depth = 0
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "[" || c == "{" { depth += 1 }
                else if c == "]" || c == "}" {
                    depth -= 1
                    if depth < 0 { return false }
                }
                i += 1
            }
            return depth == 0
        }

        // MARK: - Scalar values

        func scalarNode(raw: String, line: Int) throws -> Node {
            let t0 = raw.trimmingCharacters(in: .whitespaces)
            if t0.hasPrefix("*") {
                return Node(t: "s", v: t0, l: line) // unresolved alias: kept verbatim
            }
            let v = stripRefs(t0)
            if v.isEmpty || v == "~" { return Node(t: "u", l: line) }
            if v.count >= 2, (v.first == "\"" && v.last == "\"") || (v.first == "'" && v.last == "'") {
                return Node(t: "s", v: unquote(v), l: line)
            }
            if v.first == "[" || v.first == "{" { return try parseFlow(s: v, line: line) }
            let low = v.lowercased()
            if ["true", "false", "yes", "no", "on", "off"].contains(low) {
                return Node(t: "b", v: ["true", "yes", "on"].contains(low) ? "true" : "false", l: line)
            }
            if low == "null" || low == "nul" { return Node(t: "u", l: line) }
            if [".inf", ".infinity", "-.inf", "-.infinity", "+.inf", "+.infinity", ".nan"].contains(low) {
                return Node(t: "n", v: v, l: line)
            }
            if Self.isInt(v) || Self.isFloat(v) { return Node(t: "n", v: v, l: line) }
            return Node(t: "s", v: v, l: line)
        }

        static func isInt(_ v: String) -> Bool {
            let a = Array(v)
            if a.isEmpty { return false }
            var i = 0
            if a[0] == "+" || a[0] == "-" {
                if a.count == 1 { return false }
                i = 1
            }
            if a.count - i > 2, a[i] == "0", a[i + 1] == "x" || a[i + 1] == "X" {
                let rest = String(a[(i + 2)...]).trimmingCharacters(in: CharacterSet(charactersIn: "_"))
                return !rest.isEmpty && rest.allSatisfy { $0.isHexDigit || $0 == "_" }
            }
            if a.count - i > 2, a[i] == "0", a[i + 1] == "o" || a[i + 1] == "O" {
                let rest = String(a[(i + 2)...]).trimmingCharacters(in: CharacterSet(charactersIn: "_"))
                return !rest.isEmpty && rest.allSatisfy { ($0 >= "0" && $0 <= "7") || $0 == "_" }
            }
            var d = false
            while i < a.count {
                let c = a[i]
                if c.isNumber { d = true }
                else if c != "_" { return false }
                i += 1
            }
            return d
        }

        static func isFloat(_ v: String) -> Bool {
            let a = Array(v)
            if a.isEmpty { return false }
            var i = 0
            if a[0] == "+" || a[0] == "-" {
                if a.count == 1 { return false }
                i = 1
            }
            var digit = false, dot = false, exp = false
            while i < a.count {
                let c = a[i]
                if c.isNumber { digit = true }
                else if c == "_" {}
                else if c == ".", !dot, !exp { dot = true }
                else if (c == "e" || c == "E"), !exp, digit {
                    exp = true
                    digit = false
                    if i + 1 < a.count, a[i + 1] == "+" || a[i + 1] == "-" { i += 1 }
                } else { return false }
                i += 1
            }
            return digit && (dot || exp)
        }

        // MARK: - Flow collections

        func parseFlow(s: String, line: Int) throws -> Node {
            let s = Self.stripFlowComments(s)
            let a = Array(s)
            if a[0] == "[" {
                guard let end = Self.matchBracket(s, open: "[", close: "]") else {
                    throw fail("Unbalanced flow collection.", line)
                }
                let node = Node(t: "a", l: line, c: [], m: takePending())
                for item in Self.splitTop(String(a[1..<end]), sep: ",") {
                    let it = Self.stripComment(item.trimmingCharacters(in: .whitespaces))
                    if it.isEmpty { continue }
                    let child = try scalarNode(raw: it, line: line)
                    node.c!.append(child)
                    ate(child, line)
                    if trunc, node.c!.count >= PreviewTree.maxKidsAfterTrunc {
                        pending = []
                        last = nil
                        return node
                    }
                }
                node.e = takePending()
                attachFlowComment(node: node, s: s, end: end)
                return node
            } else {
                guard let end = Self.matchBracket(s, open: "{", close: "}") else {
                    throw fail("Unbalanced flow collection.", line)
                }
                let node = Node(t: "o", l: line, c: [], m: takePending())
                for item in Self.splitTop(String(a[1..<end]), sep: ",") {
                    let it = Self.stripComment(item.trimmingCharacters(in: .whitespaces))
                    if it.isEmpty { continue }
                    guard let colon = Self.colonOutside(it) else {
                        throw fail("Expected ':' in flow mapping.", line)
                    }
                    let k = unquote(String(it[..<colon]).trimmingCharacters(in: .whitespaces))
                    let val = try scalarNode(raw: String(it[it.index(after: colon)...]).trimmingCharacters(in: .whitespaces), line: line)
                    val.k = k
                    val.m = takePending()
                    if node.c!.contains(where: { $0.k == k }) {
                        throw fail("Duplicate key '\(k)'.", line)
                    }
                    node.c!.append(val)
                    ate(val, line)
                    if trunc, node.c!.count >= PreviewTree.maxKidsAfterTrunc {
                        pending = []
                        last = nil
                        return node
                    }
                }
                node.e = takePending()
                attachFlowComment(node: node, s: s, end: end)
                return node
            }
        }

        /// Trailing "# comment" after the closing bracket belongs to the node.
        func attachFlowComment(node: Node, s: String, end: Int) {
            let a = Array(s)
            var p = end + 1
            while p < a.count, a[p] == " " || a[p] == "\t" { p += 1 }
            if p < a.count, a[p] == "#" {
                node.e = (node.e ?? []) + [String(a[p...]).trimmingCharacters(in: .whitespacesAndNewlines)]
            }
        }

        static func matchBracket(_ s: String, open: Character, close: Character) -> Int? {
            let a = Array(s)
            var inS = false, inD = false
            var depth = 0
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "[" || c == "{" { depth += 1 }
                else if c == "]" || c == "}" {
                    depth -= 1
                    if depth == 0 { return i }
                }
                i += 1
            }
            return nil
        }

        static func splitTop(_ s: String, sep: Character) -> [String] {
            let a = Array(s)
            var out: [String] = []
            var inS = false, inD = false
            var depth = 0, start = 0
            var i = 0
            while i < a.count {
                let c = a[i]
                if inS {
                    if c == "'" {
                        if i + 1 < a.count, a[i + 1] == "'" { i += 1 } else { inS = false }
                    }
                    i += 1
                    continue
                }
                if inD {
                    if c == "\\" { i += 1 } else if c == "\"" { inD = false }
                    i += 1
                    continue
                }
                if c == "'" { inS = true; i += 1; continue }
                if c == "\"" { inD = true; i += 1; continue }
                if c == "[" || c == "{" { depth += 1 }
                else if c == "]" || c == "}" { depth -= 1 }
                else if c == sep, depth == 0 {
                    out.append(String(a[start..<i]))
                    start = i + 1
                }
                i += 1
            }
            out.append(String(a[start...]))
            return out
        }

        // MARK: - Block scalars

        func readBlockScalar(keyIndent: Int, header: String, line: Int) throws -> String {
            let h = Array(header)
            var p = 0
            let style = h[p]
            p += 1
            var chomp: Character = "\0"
            var explicitIndent = -1
            while p < h.count {
                let c = h[p]
                if (c == "+" || c == "-"), chomp == "\0" { chomp = c; p += 1 }
                else if c.isNumber, explicitIndent < 0 {
                    var q = p
                    while q < h.count, h[q].isNumber { q += 1 }
                    explicitIndent = (Int(String(h[p..<q])) ?? 1) - 1 // relative to parent
                    p = q
                } else if c == " " || c == "\t" { p += 1 }
                else if c == "#" { break } // trailing comment
                else { throw fail("Bad block scalar header.", line) }
            }
            var raw: [String] = []
            var contentIndent = -1
            var i = idx
            while i < lines.count {
                let s = lines[i]
                if Self.isBlank(s) { raw.append(""); i += 1; continue }
                let ind = Self.indentOf(s)
                if ind <= keyIndent { break }
                if contentIndent < 0 {
                    contentIndent = explicitIndent >= 0 ? keyIndent + 1 + explicitIndent : ind
                    if ind < contentIndent { throw ParseError(line: i + 1, column: 1, message: "Bad block scalar indentation.") }
                }
                if ind < contentIndent { break }
                let sa = Array(s)
                raw.append(contentIndent < sa.count ? String(sa[contentIndent...]) : "")
                i += 1
            }
            idx = i
            while raw.last == "" { raw.removeLast() }
            if style == "|" {
                let body = raw.joined(separator: "\n")
                switch chomp {
                case "-": return body
                case "+": return body + "\n" + String(repeating: "\n", count: countTrailingBlanks())
                default: return body + (body.isEmpty ? "" : "\n")
                }
            } else {
                var paras: [String] = []
                var cur = ""
                for ln in raw {
                    if ln.isEmpty {
                        if !cur.isEmpty { paras.append(cur); cur = "" }
                        paras.append("")
                    } else {
                        if !cur.isEmpty { cur.append(" ") }
                        cur.append(ln)
                    }
                }
                if !cur.isEmpty { paras.append(cur) }
                let body = paras.joined(separator: "\n")
                switch chomp {
                case "-": return trimEndNewlines(body)
                case "+": return body + "\n" + String(repeating: "\n", count: countTrailingBlanks())
                default: return body + (body.isEmpty || body.hasSuffix("\n") ? "" : "\n")
                }
            }

            func trimEndNewlines(_ s: String) -> String {
                var s = s
                while s.hasSuffix("\n") { s.removeLast() }
                return s
            }

            func countTrailingBlanks() -> Int {
                var n = 0
                var j = idx - 1
                while j >= 0, Self.isBlank(lines[j]) { n += 1; j -= 1 }
                return n
            }
        }
    }
}

/// Tolerant YAML scanner for edit mode (NSTextView), mirroring Windows'
/// YamlHighlight.cs (2.10.0): mapping keys, quoted strings, block-scalar
/// content, `-` bullets, tags/anchors/aliases and # comments.
enum YamlHighlight {
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
            func isBlockHeader(_ s: [UInt16], _ from: Int, _ to: Int) -> Bool {
                var i = from
                while i < to, u16[i] == 0x20 || u16[i] == 0x09 { i += 1 }
                if i >= to || (u16[i] != 0x7C && u16[i] != 0x3E) { return false }
                i += 1
                while i < to {
                    let c = u16[i]
                    if c == 0x2B || c == 0x2D || (c >= 0x30 && c <= 0x39) || c == 0x20 || c == 0x09 { i += 1 }
                    else if c == 0x23 { return true }
                    else { return false }
                }
                return true
            }
            func paintScalar(_ from: Int, _ to: Int) {
                var s = from
                while s < to, u16[s] == 0x20 || u16[s] == 0x09 { s += 1 }
                var e = s
                while e < to, u16[e] != 0x0A, u16[e] != 0x0D, u16[e] != 0x23 { e += 1 }
                while e > s, u16[e - 1] == 0x20 || u16[e - 1] == 0x09 { e -= 1 }
                if e <= s { return }
                if u16[s] == 0x22 || u16[s] == 0x27 { return } // quoted: the string painter owns it
                let v = String(utf16CodeUnits: Array(u16[s..<e]), count: e - s).lowercased()
                if ["true", "false", "yes", "no", "on", "off"].contains(v) {
                    ops.append((NSRange(location: s, length: e - s), pal.jsonBool, baseFont))
                    return
                }
                if v == "null" || v == "~" || v == "nul" {
                    ops.append((NSRange(location: s, length: e - s), pal.jsonNull, italic))
                    return
                }
                if isNum(v) {
                    ops.append((NSRange(location: s, length: e - s), pal.jsonNumber, baseFont))
                }
            }
            func isNum(_ v: String) -> Bool {
                let a = Array(v)
                if a.isEmpty { return false }
                var i = 0
                if a[0] == "+" || a[0] == "-" {
                    if a.count == 1 { return false }
                    i = 1
                }
                var digit = false, dot = false, exp = false
                while i < a.count {
                    let c = a[i]
                    if c.isNumber { digit = true }
                    else if c == "_" {}
                    else if c == ".", !dot, !exp { dot = true }
                    else if (c == "e" || c == "E"), !exp, digit {
                        exp = true
                        digit = false
                        if i + 1 < a.count, a[i + 1] == "+" || a[i + 1] == "-" { i += 1 }
                    } else { return false }
                    i += 1
                }
                return digit && (dot || exp)
            }
            var depth = 0
            var blockIndent = -1
            var i = 0
            while i < n {
                let lineStart = i == 0 || u16[i - 1] == 0x0A
                var indent = 0
                if lineStart {
                    while i + indent < n, u16[i + indent] == 0x20 { indent += 1 }
                    // Block scalar content: more-indented lines after a | or > header.
                    if blockIndent >= 0 {
                        var blank = true
                        var k = i
                        while k < n, u16[k] != 0x0A {
                            if u16[k] != 0x20, u16[k] != 0x09 { blank = false; break }
                            k += 1
                        }
                        if blank || indent > blockIndent {
                            var e = i
                            while e < n, u16[e] != 0x0A { e += 1 }
                            if e > i { ops.append((NSRange(location: i, length: e - i), pal.jsonString, baseFont)) }
                            i = e
                            continue
                        }
                        blockIndent = -1
                    }
                }
                let c = u16[i]
                if c == 0x27 { // '
                    let s = i
                    i += 1
                    while i < n {
                        if u16[i] == 0x27 {
                            if i + 1 < n, u16[i + 1] == 0x27 { i += 2; continue }
                            i += 1
                            break
                        }
                        i += 1
                    }
                    if i > s { ops.append((NSRange(location: s, length: i - s), pal.jsonString, baseFont)) }
                    continue
                }
                if c == 0x22 { // "
                    let s = i
                    i += 1
                    while i < n {
                        if u16[i] == 0x5C, i + 1 < n { i += 2; continue }
                        if u16[i] == 0x22 { i += 1; break }
                        i += 1
                    }
                    if i > s { ops.append((NSRange(location: s, length: i - s), pal.jsonString, baseFont)) }
                    continue
                }
                if c == 0x23, lineStart || (i > 0 && (u16[i - 1] == 0x20 || u16[i - 1] == 0x09)) {
                    let s = i
                    while i < n, u16[i] != 0x0A { i += 1 }
                    ops.append((NSRange(location: s, length: i - s), pal.jsonComment, italic))
                    continue
                }
                if c == 0x5B || c == 0x7B { depth += 1; i += 1; continue }
                if (c == 0x5D || c == 0x7D), depth > 0 { depth -= 1; i += 1; continue }
                if c == 0x26 || c == 0x2A || (c == 0x21 && i + 1 < n && u16[i + 1] != 0x20) {
                    let s = i
                    i += 1
                    while i < n {
                        let d = u16[i]
                        if (d >= 0x30 && d <= 0x39) || (d >= 0x41 && d <= 0x5A) || (d >= 0x61 && d <= 0x7A)
                            || d == 0x5F || d == 0x2D || d == 0x2E || d == 0x2F { i += 1 } else { break }
                    }
                    if i > s { ops.append((NSRange(location: s, length: i - s), pal.dim, baseFont)) }
                    continue
                }
                if lineStart, depth == 0 {
                    var p = i + indent
                    if p < n, u16[p] == 0x2D {
                        let q = p + 1
                        if q < n, u16[q] == 0x20 || u16[q] == 0x09 {
                            ops.append((NSRange(location: p, length: 1), pal.dim, baseFont))
                            p = q + 1
                            while p < n, u16[p] == 0x20 || u16[p] == 0x09 { p += 1 }
                        } else if q >= n || u16[q] == 0x0A || u16[q] == 0x0D {
                            ops.append((NSRange(location: p, length: 1), pal.dim, baseFont))
                            i = q
                            continue
                        }
                    } else {
                        p = i + indent
                        while p < n, u16[p] == 0x20 || u16[p] == 0x09 { p += 1 }
                    }
                    if p < n, u16[p] != 0x22, u16[p] != 0x27, u16[p] != 0x23, u16[p] != 0x0A {
                        var ke = p
                        while ke < n, u16[ke] != 0x3A, u16[ke] != 0x0A, u16[ke] != 0x23 { ke += 1 }
                        var kt = ke
                        while kt > p, u16[kt - 1] == 0x20 || u16[kt - 1] == 0x09 { kt -= 1 }
                        if ke < n, u16[ke] == 0x3A,
                            ke + 1 >= n || u16[ke + 1] == 0x20 || u16[ke + 1] == 0x09
                            || u16[ke + 1] == 0x0A || u16[ke + 1] == 0x0D
                        {
                            let key = String(utf16CodeUnits: Array(u16[p..<kt]), count: kt - p)
                            if !key.isEmpty, key != "?", !key.hasPrefix("? ") {
                                ops.append((NSRange(location: p, length: kt - p), pal.jsonKey, baseFont))
                                var eol = ke + 1
                                while eol < n, u16[eol] != 0x0A { eol += 1 }
                                if isBlockHeader(u16, ke + 1, eol) { blockIndent = indent }
                                // Whole-line scalar value: paint numbers/bools/null.
                                else { paintScalar(ke + 1, eol) }
                                i = ke + 1
                                continue
                            }
                        }
                    }
                }
                i += 1
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
