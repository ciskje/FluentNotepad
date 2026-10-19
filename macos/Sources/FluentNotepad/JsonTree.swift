import AppKit
import Foundation

/// JSON/JSONC (.json/.jsonc) -> collapsible tree HTML for WKWebView, plus
/// tolerant edit-mode highlighting. Port of the Windows 2.8.0 set
/// (JsonPreviewHtml.cs + JsonHighlight.cs over the shared PreviewTree.cs
/// widget): same node schema (t/o/a/s/n/b/u, k/v/l/c, remarks m/e), same
/// CSS/JS, same caps (10 MB / 300k nodes / depth 200). Comments (`//`, `/**/`)
/// and trailing commas are accepted, so .jsonc needs no browser-side parsing.
enum JsonTree {
    static let maxChars = 10_000_000
    static let maxNodes = 300_000
    static let maxDepth = 200
    static let maxKidsAfterTrunc = 1000

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

    struct ParseError: Error {
        var line: Int
        var column: Int
        var message: String
    }

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?, open: Set<String>) -> String {
        let pal = TreePalette(dark: dark)
        let head = "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
            + "<title>" + esc(title ?? "JSON") + "</title>"
            + "<style>" + css(pal, Int(fontSize)) + "</style></head><body>"
        if text.count > maxChars {
            return head + "<div class=err><div class=et>" + esc(L("jsonTooLarge"))
                + "</div></div></body></html>"
        }
        do {
            let (root, truncated) = try parse(text)
            let enc = JSONEncoder()
            let rootJS = String(data: try enc.encode(root), encoding: .utf8) ?? "null"
            let openJS = String(data: try enc.encode(open.sorted()), encoding: .utf8) ?? "[]"
            let strJS = String(data: try enc.encode(strings()), encoding: .utf8) ?? "{}"
            // Tokens (__D0__ etc.) exist only in this template: user content
            // arrives via the replacement VALUES, which never get rescanned.
            // Foundation's encoder escapes `/` (neutralizes `</script>`).
            let page = head + toolbar()
                + (truncated ? "<div id=trunc>" + esc(L("jsonTruncated")) + "</div>" : "")
                + "<div id=tree></div>"
                + "<script>var root=__D0__;var opns=__O0__;var STR=__S0__;var __XML__=__X0__;\n" + js()
                + "</script></body></html>"
            return page
                .replacingOccurrences(of: "__D0__", with: rootJS)
                .replacingOccurrences(of: "__O0__", with: openJS)
                .replacingOccurrences(of: "__S0__", with: strJS)
                .replacingOccurrences(of: "__X0__", with: "false")
        } catch let ex as ParseError {
            let src = text.count > 100_000 ? String(text.prefix(100_000)) + "\n…" : text
            return head + "<div class=err><div class=et>" + esc(L("jsonInvalidTitle")) + "</div>"
                + "<div class=em>" + esc(String(format: L("jsonInvalid"), ex.line, ex.column)) + "</div>"
                + "<div class=ed>" + esc(ex.message) + "</div>"
                + "<pre>" + esc(src) + "</pre>"
                + "<div class=eh>" + esc(L("jsonErrorHint")) + "</div></div></body></html>"
        } catch {
            return head + "<div class=err><div class=et>" + esc(L("jsonInvalidTitle"))
                + "</div></div></body></html>"
        }
    }

    private static func strings() -> [String: String] {
        ["exp": L("treeExpandAll"), "col": L("treeCollapseAll"), "cp": L("treeCopyPath"),
         "cv": L("treeCopyValue"), "match": L("treeMatches"), "none": L("treeNoMatch")]
    }

    private static func toolbar() -> String {
        "<div class=tools><button id=bExp>" + esc(L("treeExpandAll")) + "</button>"
            + "<button id=bCol>" + esc(L("treeCollapseAll")) + "</button>"
            + "<input id=q placeholder=\"" + esc(L("treeSearch")) + "\"><span id=cnt></span></div>"
    }

    private static func esc(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
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
            if count > JsonTree.maxNodes || depth > JsonTree.maxDepth {
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
                if trunc, node.c!.count >= JsonTree.maxKidsAfterTrunc {
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
                if trunc, node.c!.count >= JsonTree.maxKidsAfterTrunc {
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

    // MARK: - Palette (same VS tones as Windows' MarkdownShared)

    struct TreePalette {
        let body, bodyText, panel, panelBorder, marker, dim: String
        let heading, jsonKey, jsonString, jsonNumber, jsonBool, jsonNull, jsonComment: String
        init(dark: Bool) {
            if dark {
                body = "#1E1E1E"; bodyText = "#D4D4D4"; panel = "#252526"; panelBorder = "#3F3F46"
                marker = "#569CD6"; dim = "#808080"; heading = "#569CD6"
                jsonKey = "#9CDCFE"; jsonString = "#CE9178"; jsonNumber = "#B5CEA8"
                jsonBool = "#569CD6"; jsonNull = "#808080"; jsonComment = "#6A9950"
            } else {
                body = "#FFFFFF"; bodyText = "#1E1E1E"; panel = "#F6F6F6"; panelBorder = "#E5E5E5"
                marker = "#0F6CBD"; dim = "#808080"; heading = "#0F6CBD"
                jsonKey = "#001080"; jsonString = "#A31515"; jsonNumber = "#098658"
                jsonBool = "#0F6CBD"; jsonNull = "#808080"; jsonComment = "#008000"
            }
        }
    }

    // MARK: - CSS (verbatim port of Windows' PreviewTree.Css)

    static func css(_ p: TreePalette, _ fs: Int) -> String {
        "body{font-family:'Cascadia Mono','Cascadia Code',Consolas,monospace;font-size:" + String(fs) + "px;"
            + "line-height:1.5;margin:0;padding:10px 16px 24px;word-wrap:break-word;background:" + p.body + ";color:" + p.bodyText + "}"
            + ".tools{position:sticky;top:0;display:flex;gap:8px;align-items:center;padding:8px 0;background:" + p.body + ";z-index:2}"
            + ".tools button{font:inherit;font-size:12px;padding:3px 10px;border:1px solid " + p.panelBorder + ";border-radius:4px;background:" + p.panel + ";color:" + p.bodyText + ";cursor:pointer;white-space:nowrap}"
            + ".tools button:hover{border-color:" + p.marker + "}"
            + "#q{font:inherit;font-size:13px;padding:3px 8px;flex:1;max-width:340px;border:1px solid " + p.panelBorder + ";border-radius:4px;background:" + p.body + ";color:" + p.bodyText + "}"
            + "#cnt{font-size:12px;color:" + p.dim + ";white-space:nowrap}"
            + "#trunc{margin:6px 0 10px;padding:6px 12px;border:1px solid " + p.marker + ";border-radius:6px;background:" + p.panel + ";color:" + p.marker + ";font-size:13px}"
            + ".nd{margin:1px 0}"
            + ".row{display:flex;align-items:center;gap:4px;padding:1px 4px;border-radius:4px;cursor:pointer;white-space:nowrap}"
            + ".row:hover{background:" + p.panel + "}"
            + ".row:focus{outline:1px solid " + p.marker + "}"
            + ".row.cur{outline:1px solid " + p.marker + "}"
            + ".chev{width:14px;height:14px;flex:none;position:relative;opacity:.75}"
            + ".chev::before{content:'';position:absolute;left:4px;top:3px;border-left:6px solid currentColor;border-top:4px solid transparent;border-bottom:4px solid transparent;transition:transform .08s}"
            + ".nd.open>.row .chev::before{transform:rotate(90deg)}"
            + ".nochev{width:14px;flex:none}"
            + ".k{color:" + p.jsonKey + "}"
            + ".cm{color:" + p.jsonComment + ";font-style:italic;white-space:pre-wrap;word-break:break-word;margin:1px 4px}"
            + ".pu,.ix,.sum{color:" + p.dim + "}"
            + ".rp{color:" + p.marker + ";font-weight:700}"
            + ".vs{color:" + p.jsonString + "}"
            + ".vn{color:" + p.jsonNumber + "}"
            + ".vb{color:" + p.jsonBool + "}"
            + ".vl{color:" + p.jsonNull + ";font-style:italic}"
            + ".gap{flex:1}"
            + ".ic{opacity:0;border:0;background:none;color:" + p.dim + ";cursor:pointer;font-size:13px;padding:0 4px;display:inline-flex;align-items:center}"
            + ".row:hover .ic,.row:focus-within .ic{opacity:.85}"
            + ".ic:hover{color:" + p.bodyText + "}"
            + "mark{background:#FFD866;color:#111;border-radius:2px;padding:0 1px}"
            + ".kids{margin-left:18px;display:none}"
            + ".nd.open>.kids{display:block}"
            + ".nd.open>.row .sum{display:none}"
            + ".err{max-width:760px;margin:40px auto}"
            + ".et{font-size:16px;font-weight:700;color:" + p.heading + ";margin-bottom:8px}"
            + ".em{margin-bottom:6px}"
            + ".ed{font-size:13px;color:" + p.dim + ";margin-bottom:12px}"
            + ".err pre{padding:12px 14px;border-radius:8px;overflow:auto;border:1px solid " + p.panelBorder + ";background:" + p.panel + ";max-height:50vh}"
            + ".eh{margin-top:10px;font-size:13px;color:" + p.dim + "}"
            + "@media print{.tools,.ic{display:none!important}*{print-color-adjust:exact!important;-webkit-print-color-adjust:exact!important}"
            + "body{background:#FFFFFF!important;color:#1E1E1E!important}"
            + ".row:hover{background:none!important}.kids{display:block!important}.nd.open>.row .sum{display:none!important}"
            + ".k{color:#001080!important}.pu,.ix,.sum{color:#808080!important}.rp{color:#0F6CBD!important}"
            + ".vs{color:#A31515!important}.vn{color:#098658!important}.vb{color:#0F6CBD!important}.vl{color:#808080!important}"
            + ".cm{color:#008000!important}"
            + "mark{background:#FFD866!important;color:#111!important}}"
    }

    // MARK: - JS (verbatim port of Windows' PreviewTree.Js; post() reaches
    // Swift through the window.chrome.webview shim installed by PreviewView)

    static func js() -> String {
        """
        (function(){
        "use strict";
        var uidc=0,byUid={},byPtr={},matches=[],cur=-1,qraw='';
        var open={};(opns||[]).forEach(function(p){open[p]=1;});
        var host=document.getElementById('tree'),input=document.getElementById('q'),cnt=document.getElementById('cnt');
        var icoV='<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="4.5" y="4.5" width="6" height="6" rx="1" fill="none" stroke="currentColor"/><path d="M7.5 4.5v-2a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h2" fill="none" stroke="currentColor"/></svg>';
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function post(o){try{if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}
        function postState(){try{var a=[];for(var k in open)if(open[k])a.push(k);post({op:'state',open:a,q:qraw});}catch(e){}}
        function segEsc(s){return s.replace(/~/g,'~0').replace(/\\//g,'~1');}
        function segUn(s){return s.replace(/~1/g,'/').replace(/~0/g,'~');}
        function E(t,c){var e=document.createElement(t);if(c)e.className=c;return e;}
        function isC(n){return (n.t==='o'||n.t==='a')&&n.c&&n.c.length>0;}
        function childPtr(p,isArr,key,idx){return p+'/'+(isArr?idx:segEsc(key));}
        function childJ(isArr,key,idx){if(isArr)return '['+idx+']';if(/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key))return '.'.concat(key);return '['+JSON.stringify(key)+']';}
        function sumHtml(n){var c=n.c?n.c.length:0;if(n.t==='o')return '<span class=sum>'+(c?'{ '+c+' }':'{ }')+'</span>';return '<span class=sum>'+(c?'[ '+c+' ]':'[ ]')+'</span>';}
        function disp(s){s=String(s);return s.length>500?s.slice(0,500)+'…':s;}
        function leafHtml(n){
        if(n.t==='s')return '<span class="vv vs">'+esc('"'+disp(n.v)+'"')+'</span>';
        if(n.t==='n')return '<span class="vv vn">'+esc(n.v)+'</span>';
        if(n.t==='b')return '<span class="vv vb">'+esc(n.v)+'</span>';
        return '<span class=vl>null</span>';
        }
        function buildNd(n,p,j,key,isIdx,isRoot){
        var u='u'+(++uidc);n._u=u;
        var nd=E('div','nd');nd.setAttribute('data-p',p);
        if(n.m)for(var mi=0;mi<n.m.length;mi++){var cm0=E('div','cm');cm0.textContent=disp(n.m[mi]);nd.appendChild(cm0);}
        var row=E('div','row');row.setAttribute('data-u',u);row.tabIndex=0;
        var h=isC(n)?'<span class=chev></span>':'<span class=nochev></span>';
        if(isRoot)h+='<span class=rp>$</span> ';
        else if(key!==null&&key!==undefined)h+=isIdx?'<span class=ix>'+esc(key)+':</span> ':'<span class=k>'+esc('"'+key+'"')+'</span><span class=pu>:</span> ';
        h+=isC(n)?sumHtml(n):leafHtml(n);
        h+='<span class=gap></span>';
        h+='<button class=ic title="'+esc(STR.cp)+'" data-w=p tabindex="-1">$</button>';
        h+='<button class=ic title="'+esc(STR.cv)+'" data-w=v tabindex="-1">'+icoV+'</button>';
        row.innerHTML=h;nd.appendChild(row);nd.appendChild(E('div','kids'));
        if(n.e)for(var ei=0;ei<n.e.length;ei++){var cm1=E('div','cm');cm1.textContent=disp(n.e[ei]);nd.appendChild(cm1);}
        byUid[u]={n:n,el:nd,p:p,j:j};(byPtr[p]||(byPtr[p]=[])).push(u);
        return nd;
        }
        function renderKids(ndEl,rec){
        var kids=ndEl.querySelector(':scope > .kids');
        if(kids.hasChildNodes())return;
        var n=rec.n,isArr=n.t==='a';
        for(var i=0;i<n.c.length;i++){var c=n.c[i];
        var key=isArr?String(i):c.k;
        kids.appendChild(buildNd(c,childPtr(rec.p,isArr,c.k,i),childJ(isArr,key,i),key,isArr,false));}
        }
        function toggle(rec){
        var nd=rec.el,p=rec.p;
        if(nd.classList.contains('open')){nd.classList.remove('open');delete open[p];}
        else{var kids=nd.querySelector(':scope > .kids');
        if(!kids.hasChildNodes()&&isC(rec.n))renderKids(nd,rec);
        nd.classList.add('open');open[p]=1;}
        postState();
        }
        function matPath(p){
        if(p==='')return;
        var segs=p.split('/').slice(1).map(segUn);
        var node=root,rec=byUid[rootUid],acc='';
        for(var i=0;i<segs.length;i++){
        var isArr=node.t==='a',ci=-1;
        var ix=parseInt(segs[i],10);
        if(isArr){if(ix>=0&&ix<node.c.length)ci=ix;}
        else{for(var k=0;k<node.c.length;k++)if(node.c[k].k===segs[i]){ci=k;break;}}
        if(ci<0)return;
        var c=node.c[ci];
        acc=childPtr(acc,node.t==='a',c.k,ci);
        var rec2=byUid[c._u];if(!rec2)return;
        var kids=rec2.el.querySelector(':scope > .kids');
        if(!kids.hasChildNodes()&&isC(c))renderKids(rec2.el,rec2);
        rec2.el.classList.add('open');open[acc]=1;
        node=c;
        }
        }
        function valText(n){
        if(n.t==='s')return JSON.stringify(n.v);
        if(n.t==='n'||n.t==='b')return n.v;
        if(n.t==='u')return 'null';
        var parts=[];
        if(n.t==='a'){for(var i=0;i<n.c.length;i++)parts.push(valText(n.c[i]));return '['+parts.join(',')+']';}
        for(var k=0;k<n.c.length;k++)parts.push(JSON.stringify(n.c[k].k)+':'+valText(n.c[k]));
        return '{'+parts.join(',')+'}';
        }
        function doCopy(b){
        var row=b.closest('.row');if(!row)return;
        var rec=byUid[row.getAttribute('data-u')];if(!rec)return;
        post({op:'copy',text:b.getAttribute('data-w')==='p'?rec.j:valText(rec.n)});
        }
        function matchN(n,key,q){
        if(key&&key.toLowerCase().indexOf(q)>=0)return true;
        if(n.m)for(var i=0;i<n.m.length;i++)if(String(n.m[i]).toLowerCase().indexOf(q)>=0)return true;
        if(n.e)for(var k=0;k<n.e.length;k++)if(String(n.e[k]).toLowerCase().indexOf(q)>=0)return true;
        if(n.t==='s')return String(n.v).toLowerCase().indexOf(q)>=0;
        if(n.t==='n'||n.t==='b')return String(n.v).toLowerCase().indexOf(q)>=0;
        if(n.t==='u')return 'null'.indexOf(q)>=0;
        return false;
        }
        function walkN(n,p,key,cb){cb(n,p,key);if(n.c){var isArr=n.t==='a';for(var i=0;i<n.c.length;i++){var c=n.c[i];walkN(c,childPtr(p,isArr,c.k,i),isArr?null:c.k,cb);}}}
        function doSearch(s){
        clearMarks();qraw=s;var q=s.toLowerCase();
        matches=[];cur=-1;
        if(q){walkN(root,'',null,function(n,p,key){if(matchN(n,key,q)){matches.push(p);matPath(p);}});
        matches.forEach(function(p){(byPtr[p]||[]).forEach(function(u){markRow(byUid[u],q);});});
        if(matches.length)cur=0;}
        showCur();updateCnt();postState();
        }
        function markRow(rec,q){
        if(!rec)return;
        var row=rec.el.querySelector(':scope > .row');if(!row)return;
        row.classList.add('hit');
        var ts=rec.el.querySelectorAll(':scope > .row .k,:scope > .row .vv,:scope > .cm');
        for(var i=0;i<ts.length;i++)markIn(ts[i],q);
        }
        function markIn(el,q){
        if(el._o===undefined)el._o=el.textContent;
        var t=el._o,low=t.toLowerCase(),h='',i=0,k;
        while((k=low.indexOf(q,i))>=0){h+=esc(t.slice(i,k))+'<mark>'+esc(t.slice(k,k+q.length))+'</mark>';i=k+q.length;}
        el.innerHTML=h+esc(t.slice(i));
        }
        function clearMarks(){
        host.querySelectorAll('.hit').forEach(function(r){r.classList.remove('hit');});
        host.querySelectorAll('.cur').forEach(function(r){r.classList.remove('cur');});
        for(var u in byUid){var nd=byUid[u].el;if(!nd)continue;
        var ts=nd.querySelectorAll(':scope > .row .k,:scope > .row .vv,:scope > .cm');
        for(var i=0;i<ts.length;i++)if(ts[i]._o!==undefined){ts[i].textContent=ts[i]._o;delete ts[i]._o;}}
        }
        function updateCnt(){
        if(!qraw){cnt.textContent='';return;}
        if(!matches.length){cnt.textContent=STR.none;return;}
        cnt.textContent=STR.match.replace('{0}',String(cur+1)).replace('{1}',String(matches.length));
        }
        function showCur(){
        host.querySelectorAll('.cur').forEach(function(r){r.classList.remove('cur');});
        if(cur<0||!matches.length)return;
        (byPtr[matches[cur]]||[]).forEach(function(u){var rec=byUid[u];if(!rec)return;
        var row=rec.el.querySelector(':scope > .row');row.classList.add('cur');row.scrollIntoView({block:'nearest'});});
        }
        function step(d){if(!matches.length)return;cur=(cur+d+matches.length)%matches.length;showCur();}
        function expandAll(on){
        walkN(root,'',null,function(n,p){
        if(p===''||!isC(n))return;
        if(on)open[p]=1;else delete open[p];
        (byPtr[p]||[]).forEach(function(u){var rec=byUid[u];if(!rec)return;
        var nd=rec.el,kids=nd.querySelector(':scope > .kids');
        if(on){if(!kids.hasChildNodes())renderKids(nd,rec);nd.classList.add('open');}
        else nd.classList.remove('open');});
        });
        postState();
        }
        var rootUid=null;
        function init(){
        host.innerHTML='';
        var r=buildNd(root,'','$',null,false,true);
        var row0=r.querySelector(':scope > .row');
        rootUid=row0.getAttribute('data-u');
        host.appendChild(r);
        r.classList.add('open');
        if(isC(root))renderKids(r,byUid[rootUid]);
        for(var p in open)matPath(p);
        }
        host.addEventListener('click',function(e){
        var t=e.target;
        var b=t.closest?t.closest('button.ic'):null;
        if(b){e.stopPropagation();doCopy(b);return;}
        var row=t.closest?t.closest('.row'):null;
        if(!row)return;
        var rec=byUid[row.getAttribute('data-u')];
        if(!rec)return;
        if(isC(rec.n))toggle(rec);
        else post({op:'line',line:rec.n.l});
        });
        host.addEventListener('keydown',function(e){
        if((e.key!=='Enter'&&e.key!==' ')||(e.target&&e.target.tagName==='INPUT'))return;
        var t=e.target,row=t.closest?t.closest('.row'):null;
        if(!row||!host.contains(row))return;
        e.preventDefault();
        var rec=byUid[row.getAttribute('data-u')];
        if(!rec)return;
        if(isC(rec.n))toggle(rec);
        else post({op:'line',line:rec.n.l});
        });
        document.getElementById('bExp').addEventListener('click',function(){expandAll(true);});
        document.getElementById('bCol').addEventListener('click',function(){expandAll(false);});
        var sTimer=null;
        input.addEventListener('input',function(){clearTimeout(sTimer);sTimer=setTimeout(function(){doSearch(input.value);},120);});
        input.addEventListener('keydown',function(e){
        if(e.key==='Enter'){e.preventDefault();step(e.shiftKey?-1:1);}
        else if(e.key==='Escape'){input.value='';doSearch('');}
        });
        window.__fnExpandAll=function(){expandAll(true);};
        window.__fnFocusSearch=function(){input.focus();input.select();};
        window.__fnClearSearch=function(){input.value='';doSearch('');};
        init();
        })();
        """
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
