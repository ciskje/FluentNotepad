import AppKit
import Foundation

/// CSV (.csv/.tsv) grid preview: the delimiter is sniffed from the content
/// (comma, semicolon, tab, pipe), quotes follow RFC 4180 (doubled "" escapes,
/// embedded newlines allowed). The first row is the header; ragged rows are
/// padded. Copy duplicates the raw line; clicking a row jumps to its source
/// line. Port of Windows' CsvPreviewHtml.cs (2.10.0): same parser, same grid
/// widget (the header row renders as the table head here, per the intent).
enum CsvGrid {
    static let maxChars = 10_000_000
    static let maxRows = 5000

    typealias ParseError = PreviewTree.ParseError

    struct Row: Encodable {
        var l: Int
        var c: [String]
        var raw: String

        enum CodingKeys: String, CodingKey { case l, c }
        // Raw lines travel in a parallel RAWS array (like Windows).
        func encode(to enc: Encoder) throws {
            var out = enc.container(keyedBy: CodingKeys.self)
            try out.encode(l, forKey: .l)
            try out.encode(c, forKey: .c)
        }
    }

    // MARK: - Build

    static func build(_ text: String, dark: Bool, fontSize: Double, title: String?) -> String {
        let pal = PreviewTree.Palette(dark: dark)
        let head = "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
            + "<title>" + PreviewTree.esc(title ?? "CSV") + "</title>"
            + "<style>" + PreviewTree.css(pal, Int(fontSize)) + gridCss(pal, fontSize)
            + "</style></head><body>"
        if text.count > maxChars {
            return head + "<div class=err><div class=et>" + PreviewTree.esc(L("csvTooLarge"))
                + "</div></div></body></html>"
        }
        do {
            let (table, truncated, totalRows) = try parse(text)
            if table.isEmpty { throw ParseError(line: 1, column: 1, message: "No rows found.") }
            let enc = JSONEncoder()
            struct CellRow: Encodable {
                var l: Int
                var c: [String]
            }
            let rowsJSON = String(
                data: try enc.encode(table.map { CellRow(l: $0.l, c: $0.c) }),
                encoding: .utf8) ?? "[]"
            let rawsJSON = String(data: try enc.encode(table.map(\.raw)), encoding: .utf8) ?? "[]"
            let truncText: String
            if truncated {
                truncText = String(format: L("csvTruncated"), table.count - 1, totalRows - 1)
            } else { truncText = "" }
            let page = head + toolbar()
                + (truncated ? "<div id=trunc>" + PreviewTree.esc(truncText) + "</div>" : "")
                + "<div id=grid></div>"
                + "<script>var ROWS=__D0__;var RAWS=__R0__;var STR=__S0__;\n" + js()
                + "</script></body></html>"
            return page
                .replacingOccurrences(of: "__D0__", with: rowsJSON)
                .replacingOccurrences(of: "__R0__", with: rawsJSON)
                .replacingOccurrences(of: "__S0__", with: PreviewTree.stringsJSON())
        } catch let ex as ParseError {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("csvInvalidTitle"),
                invalidLine: String(format: L("csvInvalid"), ex.line, ex.column),
                details: ex.message, source: text, hint: L("csvErrorHint"))
        } catch {
            return PreviewTree.errorPage(dark: dark, fontSize: fontSize, title: title,
                heading: L("csvInvalidTitle"), invalidLine: "", details: "\(error)",
                source: text, hint: L("csvErrorHint"))
        }
    }

    private static func toolbar() -> String {
        "<div class=tools>"
            + "<input id=q placeholder=\"" + PreviewTree.esc(L("treeSearch")) + "\"><span id=cnt></span></div>"
    }

    private static func gridCss(_ p: PreviewTree.Palette, _ fs: Double) -> String {
        ".tbl{border-collapse:collapse;margin:6px 0 14px;font-size:" + String(Int(fs * 0.95)) + "px;max-width:100%}"
            + ".tbl th,.tbl td{border:1px solid " + p.cellBorder + ";padding:4px 10px;text-align:left;vertical-align:top;white-space:pre-wrap;word-break:break-word}"
            + ".tbl th{background:" + p.codeBg + ";color:" + p.heading + ";font-weight:700;position:sticky;top:52px}"
            + ".tbl tbody tr:nth-child(even){background:" + p.zebra + "}"
            + ".tbl tbody tr:hover{background:" + p.panel + "}"
            + ".tbl tbody tr.cur{outline:2px solid " + p.marker + ";outline-offset:-2px}"
            + ".tbl .rn{color:" + p.dim + ";text-align:right;white-space:nowrap}"
            + ".tbl .hit{background:#FFD866 !important;color:#111 !important}"
            + "@media print{.tools,.ic{display:none!important}}"
            + "@media print{body{background:#FFFFFF!important;color:#1E1E1E!important}"
            + ".tbl th{background:#F3F3F3!important;color:#0F6CBD!important}"
            + ".tbl th,.tbl td{border-color:#D4D4D4!important}"
            + ".tbl tbody tr:nth-child(even){background:#FAFAFA!important}mark{background:#FFD866!important;color:#111!important}}"
    }

    // MARK: - Parse (1-based lines)

    static func parse(_ text: String) throws -> ([Row], Bool, Int) {
        var trunc = false
        var total = 0
        let norm = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        if norm.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ParseError(line: 1, column: 1, message: "No rows found.")
        }
        let delim = sniffDelimiter(norm)
        let a = Array(norm)
        var rows: [Row] = []
        var pos = 0, line = 1
        var cells: [String] = []
        var cur = ""
        var inQ = false
        var rowStart = 1, rowStartOff = 0
        var rowHasContent = false

        func endCell() { cells.append(cur); cur = "" }
        func endRow(force: Bool) {
            // Skip wholly empty lines (but keep newlines inside quotes: inQ).
            if !force, !rowHasContent, cells.allSatisfy({ $0.isEmpty }) {
                cells = []
                rowHasContent = false
                return
            }
            total += 1
            if rows.count < maxRows {
                let raw = String(a[rowStartOff..<min(pos, a.count)])
                rows.append(Row(l: rowStart, c: cells, raw: raw))
            } else {
                trunc = true
            }
            cells = []
            rowHasContent = false
        }

        while pos <= a.count {
            let atEnd = pos >= a.count
            let c: Character = atEnd ? "\0" : a[pos]
            if inQ {
                if atEnd { throw ParseError(line: rowStart, column: 1, message: "Unterminated quoted field.") }
                if c == "\"" {
                    if pos + 1 < a.count, a[pos + 1] == "\"" {
                        cur.append("\""); pos += 2; rowHasContent = true; continue
                    }
                    inQ = false
                    pos += 1
                    continue
                }
                cur.append(c)
                if c == "\n" { line += 1 }
                if c != " ", c != "\t" { rowHasContent = true }
                pos += 1
                continue
            }
            if atEnd || c == "\n" {
                endCell()
                endRow(force: false)
                rowStart = line + 1
                rowStartOff = atEnd ? pos : pos + 1
                if !atEnd { pos += 1; line += 1 } else { break }
                continue
            }
            if c == "\"" {
                // Quote only starts a quoted field at field start.
                if cur.isEmpty { inQ = true; rowHasContent = true; pos += 1; continue }
                cur.append(c)
                pos += 1
                continue
            }
            if c == delim { endCell(); pos += 1; rowHasContent = true; continue }
            cur.append(c)
            if c != " ", c != "\t", c != "\r" { rowHasContent = true }
            pos += 1
        }
        // Ragged rows: pad to the widest kept row.
        let width = rows.map(\.c.count).max() ?? 0
        for r in rows.indices { while rows[r].c.count < width { rows[r].c.append("") } }
        return (rows, trunc, total)
    }

    private static func sniffDelimiter(_ text: String) -> Character {
        let cands: [Character] = [",", ";", "\t", "|"]
        var counts = [0, 0, 0, 0]
        var inQ = false
        var lines = 0
        for c in text {
            if lines >= 30 { break }
            if c == "\n" { lines += 1; continue }
            if c == "\"" { inQ.toggle(); continue }
            if inQ { continue }
            for k in 0..<cands.count where c == cands[k] { counts[k] += 1 }
        }
        var best = 0
        for k in 1..<cands.count where counts[k] > counts[best] { best = k }
        return counts[best] > 0 ? cands[best] : ","
    }

    // MARK: - JS (grid: search/count/cycle, copy row, click-to-line)

    static func js() -> String {
        #"""
                (function(){
        "use strict";
        var uidc=0,byUid={},matches=[],cur=-1,qraw='';
        var host=document.getElementById('grid'),input=document.getElementById('q'),cnt=document.getElementById('cnt');
        var icoV='<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="4.5" y="4.5" width="6" height="6" rx="1" fill="none" stroke="currentColor"/><path d="M7.5 4.5v-2a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h2" fill="none" stroke="currentColor"/></svg>';
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function post(o){try{if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}
        function postState(){try{post({op:'state',open:[],q:qraw});}catch(e){}}
        function E(t,c){var e=document.createElement(t);if(c)e.className=c;return e;}
        function disp(s){s=String(s);return s.length>300?s.slice(0,300)+'…':s;}
        function build(){
        host.innerHTML='';
        var t=E('table','tbl');
        var thead=E('thead'),hr=E('tr'),rh=E('th');rh.textContent='#';hr.appendChild(rh);
        var ncols=0;for(var i=0;i<ROWS.length;i++)ncols=Math.max(ncols,ROWS[i].c.length);
        var hdr=ROWS.length?ROWS[0].c:[];
        for(var h=0;h<ncols;h++){var hc=E('th');hc.textContent=hdr[h]||('col '+(h+1));hr.appendChild(hc);}
        var hc2=E('th');hr.appendChild(hc2);thead.appendChild(hr);t.appendChild(thead);
        var tb=E('tbody');
        for(var r=1;r<ROWS.length;r++){var row=ROWS[r],u='u'+(++uidc);
        var tr=E('tr');tr.setAttribute('data-u',u);tr.setAttribute('data-l',row.l);
        var rn=E('td','rn');rn.textContent=String(r);tr.appendChild(rn);
        for(var c=0;c<ncols;c++){var td=E('td','c');td.textContent=disp(row.c[c]||'');tr.appendChild(td);}
        var cp=E('td');cp.innerHTML='<button class=ic title="'+esc(STR.cv)+'" tabindex="-1">'+icoV+'</button>';
        tr.appendChild(cp);tb.appendChild(tr);
        byUid[u]={el:tr,idx:r};}
        t.appendChild(tb);host.appendChild(t);
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
        host.querySelectorAll('td.c').forEach(function(td){if(td._o!==undefined){td.textContent=td._o;delete td._o;}});
        }
        function updateCnt(){
        if(!qraw){cnt.textContent='';return;}
        if(!matches.length){cnt.textContent=STR.none;return;}
        cnt.textContent=STR.match.replace('{0}',String(cur+1)).replace('{1}',String(matches.length));
        }
        function showCur(){
        host.querySelectorAll('.cur').forEach(function(r){r.classList.remove('cur');});
        if(cur<0||!matches.length)return;
        var rec=matches[cur];var row=rec.el;
        row.classList.add('cur');row.scrollIntoView({block:'nearest'});
        }
        function step(d){if(!matches.length)return;cur=(cur+d+matches.length)%matches.length;showCur();}
        function doSearch(s){
        clearMarks();qraw=s;var q=s.toLowerCase();
        matches=[];cur=-1;
        if(q){for(var u in byUid){var rec=byUid[u],hit=false;
        var cells=rec.el.querySelectorAll('td.c');
        for(var i=0;i<cells.length;i++){var tx=cells[i].textContent||'';
        if(tx.toLowerCase().indexOf(q)>=0){markIn(cells[i],q);hit=true;}}
        if(hit){rec.el.classList.add('hit');matches.push(rec);}}
        if(matches.length)cur=0;}
        showCur();updateCnt();postState();
        }
        host.addEventListener('click',function(e){
        var t=e.target;
        var b=t.closest?t.closest('button.ic'):null;
        if(b){e.stopPropagation();
        var row=b.closest('tr');if(!row)return;
        var u=row.getAttribute('data-u');var rec=byUid[u];if(!rec)return;
        post({op:'copy',text:RAWS[rec.idx]||''});return;}
        var row2=t.closest?t.closest('tr'):null;
        if(!row2||!row2.parentNode||row2.parentNode.tagName!=='TBODY')return;
        var u2=row2.getAttribute('data-u');var rec2=byUid[u2];if(!rec2)return;
        var ln=ROWS[rec2.idx].l;
        post({op:'line',line:ln});
        });
        host.addEventListener('keydown',function(e){
        if(e.target&&e.target.tagName==='INPUT')return;
        if(e.key!=='Enter')return;
        var row=e.target.closest?e.target.closest('tr'):null;
        if(!row||!row.parentNode||row.parentNode.tagName!=='TBODY')return;
        e.preventDefault();
        var u=row.getAttribute('data-u');var rec=byUid[u];if(!rec)return;
        post({op:'line',line:ROWS[rec.idx].l});
        });
        document.addEventListener('keydown',function(e){
        if(e.target===input&&e.key==='Enter'){e.preventDefault();step(e.shiftKey?-1:1);}
        });
        var sTimer=null;
        input.addEventListener('input',function(){clearTimeout(sTimer);sTimer=setTimeout(function(){doSearch(input.value);},120);});
        input.addEventListener('keydown',function(e){
        if(e.key==='Enter'){e.preventDefault();step(e.shiftKey?-1:1);}
        else if(e.key==='Escape'){input.value='';doSearch('');}
        });
        window.__fnExpandAll=function(){};
        window.__fnFocusSearch=function(){input.focus();input.select();};
        window.__fnClearSearch=function(){input.value='';doSearch('');};
        build();
        })();
        
        """#
    }
}
