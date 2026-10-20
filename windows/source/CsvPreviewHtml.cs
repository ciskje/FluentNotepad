using System.Text;
using System.Text.Json;
using static FluentNotepad.PreviewTree;

namespace FluentNotepad;

/// <summary>
/// CSV (.csv/.tsv) grid preview for WebView2: the delimiter is sniffed from
/// the content (comma, semicolon, tab, pipe), quotes follow RFC 4180
/// (doubled "" escapes, embedded newlines allowed). The first row is the
/// header; ragged rows are padded. Copy duplicates the raw line; clicking a
/// row jumps to its source line. Capped at MaxRows data rows.
/// </summary>
public static class CsvPreviewHtml
{
    public const int MaxRows = 5000;

    public static string Build(string text, bool dark, double fontSize, string? title)
    {
        var pal = new MarkdownShared.Palette(dark);
        string head = PageHead(dark, fontSize, title ?? "CSV", GridCss(pal, fontSize));
        if (text.Length > MaxChars)
            return head + TooLarge(MainWindow.Str("CsvTooLarge"));
        try
        {
            var table = Parse(text, out bool truncated, out int totalRows);
            if (table.Count == 0) throw new PreviewTree.TreeParseException("No rows found.", 1, 1);
            string page = head + Toolbar() +
                (truncated ? TruncBanner(string.Format(MainWindow.Str("CsvTruncated"), table.Count - 1, totalRows - 1)) : "") +
                "<div id=grid></div>" +
                "<script>var ROWS=__D0__;var RAWS=__R0__;var STR=__S0__;\n" + Js() + "</script></body></html>";
            var rowsJson = JsonSerializer.Serialize(table.ConvertAll(r => new { l = r.Line, c = r.Cells }));
            var rawsJson = JsonSerializer.Serialize(table.ConvertAll(r => r.Raw));
            return page
                .Replace("__D0__", rowsJson)
                .Replace("__R0__", rawsJson)
                .Replace("__S0__", JsonSerializer.Serialize(PreviewTree.Strings()));
        }
        catch (PreviewTree.TreeParseException ex)
        {
            return ErrorPage(dark, fontSize, title ?? "CSV",
                MainWindow.Str("CsvInvalidTitle"),
                string.Format(MainWindow.Str("CsvInvalid"), ex.Line, ex.Column),
                ex.Message, text, MainWindow.Str("CsvErrorHint"));
        }
    }

    private static string Toolbar() =>
        "<div class=tools>" +
        "<input id=q placeholder=\"" + Esc(MainWindow.Str("JsonSearch")) + "\"><span id=cnt></span></div>";

    private static string GridCss(MarkdownShared.Palette p, double fs) =>
        ".tbl{border-collapse:collapse;margin:6px 0 14px;font-size:" + ((int)(fs * 0.95)) + "px;max-width:100%}" +
        ".tbl th,.tbl td{border:1px solid " + p.CellBorder.Hex + ";padding:4px 10px;text-align:left;vertical-align:top;white-space:pre-wrap;word-break:break-word}" +
        ".tbl th{background:" + p.CodeBg.Hex + ";color:" + p.Heading.Hex + ";font-weight:700;position:sticky;top:52px}" +
        ".tbl tbody tr:nth-child(even){background:" + p.Zebra.Hex + "}" +
        ".tbl tbody tr:hover{background:" + p.Panel.Hex + "}" +
        ".tbl tbody tr.cur{outline:2px solid " + p.Marker.Hex + ";outline-offset:-2px}" +
        ".tbl .rn{color:" + p.Dim.Hex + ";text-align:right;white-space:nowrap}" +
        ".tbl .hit{background:#FFD866 !important;color:#111 !important}" +
        "@media print{.tools,.ic{display:none!important}}" +
        "@media print{body{background:#FFFFFF!important;color:#1E1E1E!important}" +
        ".tbl th{background:#F3F3F3!important;color:#0F6CBD!important}" +
        ".tbl th,.tbl td{border-color:#D4D4D4!important}" +
        ".tbl tbody tr:nth-child(even){background:#FAFAFA!important}mark{background:#FFD866!important;color:#111!important}}";

    private sealed class Row
    {
        public int Line;
        public List<string> Cells = new();
        public string Raw = "";
    }

    // MARK: - Parse (1-based lines)

    private static List<Row> Parse(string text, out bool truncated, out int totalRows)
    {
        bool trunc = false;
        int total = 0;
        string norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(norm)) throw new PreviewTree.TreeParseException("No rows found.", 1, 1);
        char delim = SniffDelimiter(norm);
        var rows = new List<Row>();
        int pos = 0, line = 1;
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false;
        int rowStart = 1, rowStartOff = 0;
        bool rowHasContent = false;

        void EndCell()
        {
            cells.Add(cur.ToString());
            cur.Clear();
        }
        void EndRow(bool force)
        {
            // Skip wholly empty lines (but keep newlines inside quotes: inQ).
            if (!force && !rowHasContent && cells.Count <= 1 && cells.TrueForAll(c => c == ""))
            {
                cells.Clear();
                rowHasContent = false;
                return;
            }
            total++;
            if (rows.Count < MaxRows)
                rows.Add(new Row { Line = rowStart, Cells = new List<string>(cells), Raw = norm.Substring(rowStartOff, System.Math.Max(0, pos - rowStartOff)) });
            else
                trunc = true;
            cells.Clear();
            rowHasContent = false;
        }

        while (pos <= norm.Length)
        {
            char c = pos < norm.Length ? norm[pos] : '\0';
            bool atEnd = pos >= norm.Length;
            if (inQ)
            {
                if (atEnd) throw new PreviewTree.TreeParseException("Unterminated quoted field.", rowStart, 1);
                if (c == '"')
                {
                    if (pos + 1 < norm.Length && norm[pos + 1] == '"') { cur.Append('"'); pos += 2; rowHasContent = true; continue; }
                    inQ = false;
                    pos++;
                    continue;
                }
                cur.Append(c);
                if (c == '\n') line++;
                if (c != ' ' && c != '\t') rowHasContent = true;
                pos++;
                continue;
            }
            if (atEnd || c == '\n')
            {
                EndCell();
                EndRow(force: false);
                rowStart = line + 1;
                rowStartOff = atEnd ? pos : pos + 1;
                if (!atEnd) { pos++; line++; }
                else break;
                continue;
            }
            if (c == '"')
            {
                // Quote only starts a quoted field at field start.
                if (cur.Length == 0) { inQ = true; rowHasContent = true; pos++; continue; }
                cur.Append(c);
                pos++;
                continue;
            }
            if (c == delim) { EndCell(); pos++; rowHasContent = true; continue; }
            cur.Append(c);
            if (c != ' ' && c != '\t' && c != '\r') rowHasContent = true;
            pos++;
        }
        // Ragged rows: pad to the widest kept row.
        int width = 0;
        foreach (var r in rows) width = Math.Max(width, r.Cells.Count);
        foreach (var r in rows) while (r.Cells.Count < width) r.Cells.Add("");
        truncated = trunc;
        totalRows = total;
        return rows;
    }

    private static char SniffDelimiter(string text)
    {
        int[] counts = new int[4];
        char[] cands = new[] { ',', ';', '\t', '|' };
        bool inQ = false;
        int lines = 0;
        for (int i = 0; i < text.Length && lines < 30; i++)
        {
            char c = text[i];
            if (c == '\n') { lines++; continue; }
            if (c == '"') { inQ = !inQ; continue; }
            if (inQ) continue;
            for (int k = 0; k < cands.Length; k++)
                if (c == cands[k]) counts[k]++;
        }
        int best = 0;
        for (int k = 1; k < cands.Length; k++)
            if (counts[k] > counts[best]) best = k;
        return counts[best] > 0 ? cands[best] : ',';
    }

    // MARK: - JS (grid: search/count/cycle, copy row, click-to-line)

    private static string Js() => PreviewTree.JsHelpers() + """
        (function(){
        "use strict";
        var uidc=0,byUid={},matches=[],cur=-1,qraw='';
        var host=document.getElementById('grid'),input=document.getElementById('q'),cnt=document.getElementById('cnt');
        var icoV='<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="4.5" y="4.5" width="6" height="6" rx="1" fill="none" stroke="currentColor"/><path d="M7.5 4.5v-2a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h2" fill="none" stroke="currentColor"/></svg>';
        function postState(){try{post({op:'state',open:[],q:qraw});}catch(e){}}
        function build(){
        host.innerHTML='';
        var t=E('table','tbl');
        var thead=E('thead'),hr=E('tr'),rh=E('th');rh.textContent='#';hr.appendChild(rh);
        var ncols=0;for(var i=0;i<ROWS.length;i++)ncols=Math.max(ncols,ROWS[i].c.length);
        for(var h=0;h<ncols;h++){var hc=E('th');hc.textContent='col '+(h+1);hr.appendChild(hc);}
        var hc2=E('th');hr.appendChild(hc2);thead.appendChild(hr);t.appendChild(thead);
        var tb=E('tbody');
        for(var r=1;r<ROWS.length;r++){var row=ROWS[r],u='u'+(++uidc);
        var tr=E('tr');tr.setAttribute('data-u',u);tr.setAttribute('data-l',row.l);
        var rn=E('td','rn');rn.textContent=String(r);tr.appendChild(rn);
        for(var c=0;c<ncols;c++){var td=E('td','c');td.textContent=disp(row.c[c]||'',300);tr.appendChild(td);}
        var cp=E('td');cp.innerHTML='<button class=ic title="'+esc(STR.cv)+'" tabindex="-1">'+icoV+'</button>';
        tr.appendChild(cp);tb.appendChild(tr);
        byUid[u]={el:tr,idx:r};}
        t.appendChild(tb);host.appendChild(t);
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
        """;
}
