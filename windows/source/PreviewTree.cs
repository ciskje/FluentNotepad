using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentNotepad;

/// <summary>
/// Shared collapsible-tree preview: page shell, CSS palette and JS widget used
/// by every tree preview (JSON, XML, YAML, TOML, INI) and the CSV grid. The page
/// embeds data nodes; all value text goes through textContent/esc, never raw
/// innerHTML from values.
/// </summary>
public static class PreviewTree
{
    // t: o = object/element, a = array, s = string, n = number (raw literal),
    // b = bool, u = null. m = leading remarks, e = trailing remarks.
    public sealed class TreeNode
    {
        public string? k { get; set; }
        public string t { get; set; } = "";
        public string? v { get; set; }
        public int l { get; set; }
        public List<TreeNode>? c { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool tr { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? m { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? e { get; set; }
    }

    // Toolbar + search strings (generic wording, shared by every tree preview).
    public static Dictionary<string, string> Strings() => new()
    {
        ["exp"] = MainWindow.Str("JsonExpandAll"),
        ["col"] = MainWindow.Str("JsonCollapseAll"),
        ["cp"] = MainWindow.Str("JsonCopyPath"),
        ["cv"] = MainWindow.Str("JsonCopyValue"),
        ["match"] = MainWindow.Str("JsonMatches"),
        ["none"] = MainWindow.Str("JsonNoMatch"),
    };

    /// <summary>
    /// Parse failure with a 1-based position (all tree parsers report lines
    /// starting at 1, like the editor shows them).
    /// </summary>
    public sealed class TreeParseException(string message, int line, int column) : Exception(message)
    {
        public int Line { get; } = line;
        public int Column { get; } = column;
    }

    // Caps shared by every preview builder (same values as the Mac).
    public const int MaxChars = 10_000_000;
    public const int MaxNodes = 300_000;
    public const int MaxDepth = 200;
    public const int MaxKidsAfterTrunc = 1000;

    public static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Page shell: doctype, color scheme, title, palette CSS.</summary>
    public static string PageHead(bool dark, double fontSize, string title, string extraCss = "", bool wrap = true) =>
        "<!DOCTYPE html><html><head><meta charset='utf-8'>" +
        "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>" +
        "<title>" + Esc(title) + "</title>" +
        "<style>" + Css(new MarkdownShared.Palette(dark), fontSize, wrap) + extraCss + "</style></head><body>";

    /// <summary>Standard tree toolbar (expand/collapse/search).</summary>
    public static string Toolbar() =>
        "<div class=tools><button id=bExp>" + Esc(MainWindow.Str("JsonExpandAll")) + "</button>" +
        "<button id=bCol>" + Esc(MainWindow.Str("JsonCollapseAll")) + "</button>" +
        "<input id=q placeholder=\"" + Esc(MainWindow.Str("JsonSearch")) + "\"><span id=cnt></span></div>";

    public static string TruncBanner(string message) =>
        "<div id=trunc>" + Esc(message) + "</div>";

    public static string TooLarge(string message) =>
        "<div class=err><div class=et>" + Esc(message) + "</div></div></body></html>";

    /// <summary>Full tree page: toolbar, truncation banner, tree host, payload.</summary>
    public static string TreePage(bool dark, double fontSize, string title, string? truncMessage,
        string rootJson, string openJson, bool xml, bool wrap = true)
    {
        string page = PageHead(dark, fontSize, title, "", wrap) + Toolbar() +
            (truncMessage == null ? "" : TruncBanner(truncMessage)) +
            "<div id=tree></div>" +
            "<script>var root=__D0__;var opns=__O0__;var STR=__S0__;var __XML__=" + (xml ? "true" : "false") + ";\n" + Js() + "</script></body></html>";
        return page
            .Replace("__D0__", rootJson)
            .Replace("__O0__", openJson)
            .Replace("__S0__", JsonSerializer.Serialize(Strings()));
    }

    /// <summary>
    /// Everything the invalid-document page needs: what failed, where, why,
    /// how to fix it and which actions the page offers. Builders fill this in
    /// so every format (JSON, XML, YAML, TOML, INI, CSV) gets the same
    /// meaningful screen instead of a bare "invalid" line.
    /// </summary>
    public sealed class ErrorInfo
    {
        public string? Title { get; init; }            // file/tab name
        public string Format { get; init; } = "";      // JSON, XML, ...
        public string Heading { get; init; } = "";     // "JSON non valido"
        public string InvalidLine { get; init; } = ""; // "JSON non valido (riga 3, colonna 7)"
        public string Message { get; init; } = "";     // parser message (cause)
        public string Hint { get; init; } = "";        // short footer note
        public string Source { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public IReadOnlyList<string> Fixes { get; init; } = Array.Empty<string>();
    }

    /// <summary>Invalid-document page shared by every preview builder.</summary>
    public static string ErrorPage(bool dark, double fontSize, ErrorInfo e, bool wrap = true)
    {
        string src = e.Source.Length > 100_000 ? e.Source[..100_000] + "\n…" : e.Source;
        string details = string.Join("\n", new[]
        {
            e.Heading,
            e.Title ?? "",
            e.InvalidLine,
            MainWindow.Str("ErrCause") + ": " + e.Message,
        });
        string fixes = e.Fixes.Count == 0 ? "" :
            "<div class=card><div class=lbl>" + Esc(MainWindow.Str("ErrHowToFix")) + "</div><ol class=fix>" +
            string.Join("", e.Fixes.Select(f => "<li>" + Esc(f) + "</li>")) + "</ol></div>";
        string frame = CodeFrame(e.Source, e.Line, e.Column);
        string position = e.Line is int ln
            ? "<div class=card><div class=lbl>" + Esc(MainWindow.Str("ErrPosition")) + "</div>" +
              "<div class=msg>" + Esc(string.Format(MainWindow.Str("ErrLineColumn"), ln, e.Column ?? 1)) + "</div>" +
              frame + "</div>"
            : "";
        string js = JsHelpers() + """
            (function(){
            "use strict";
            var det=__D0__;
            document.addEventListener('click',function(ev){
              var b=ev.target&&ev.target.closest?ev.target.closest('[data-op]'):null;
              if(!b)return;
              ev.preventDefault();
              var op=b.getAttribute('data-op');
              if(op==='copy'){post({op:'copy',text:det});}
              else{post({op:op});}
            },false);
            })();
            """.Replace("__D0__", JsonSerializer.Serialize(details));
        return PageHead(dark, fontSize, e.Title ?? e.Format, "", wrap) +
            "<div class=err><div class=ehd><span class=badge>" + Esc(e.Format) + "</span>" +
            "<span class=et>" + Esc(e.Heading) + "</span></div>" +
            (string.IsNullOrEmpty(e.Title) ? "" : "<div class=efile>" + Esc(e.Title!) + "</div>") +
            "<div class=em>" + Esc(e.InvalidLine) + "</div>" +
            "<div class=card><div class=lbl>" + Esc(MainWindow.Str("ErrCause")) + "</div>" +
            "<div class=msg>" + Esc(e.Message) + "</div></div>" +
            position + fixes +
            "<div class=acts>" +
            "<button class=pri data-op=edit>" + Esc(MainWindow.Str("ErrOpenEdit")) + "</button>" +
            "<button data-op=reload>" + Esc(MainWindow.Str("ErrReload")) + "</button>" +
            "<button data-op=text>" + Esc(MainWindow.Str("ErrOpenText")) + "</button>" +
            "<button data-op=copy>" + Esc(MainWindow.Str("ErrCopyDetails")) + "</button>" +
            "</div>" +
            (string.IsNullOrEmpty(e.Hint) ? "" : "<div class=eh>" + Esc(e.Hint) + "</div>") +
            "<details class=srcsum><summary>" + Esc(MainWindow.Str("ErrShowSource")) + "</summary>" +
            "<pre>" + Esc(src) + "</pre></details></div>" +
            "<script>" + js + "</script></body></html>";
    }

    /// <summary>
    /// 2 lines of context around the error with a caret under the column.
    /// Tabs are expanded to 4 spaces so the caret lines up in the browser.
    /// </summary>
    private static string CodeFrame(string source, int? line, int? column)
    {
        if (line is not int ln || ln < 1) return "";
        string norm = source.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = norm.Split('\n');
        if (ln > lines.Length) return "";
        int first = Math.Max(1, ln - 2), last = Math.Min(lines.Length, ln + 2);
        int width = last.ToString().Length;
        var sb = new StringBuilder();
        for (int i = first; i <= last; i++)
        {
            string text = ExpandTabs(lines[i - 1]);
            string gutter = i.ToString().PadLeft(width) + " | ";
            sb.Append("<span class=ln>").Append(Esc(gutter)).Append("</span>");
            sb.Append(i == ln
                ? "<span class=bad>" + Esc(text) + "</span>"
                : Esc(text));
            sb.Append('\n');
            if (i == ln && column is int col && col >= 1)
            {
                int caret = Math.Clamp(col - 1, 0, text.Length);
                sb.Append("<span class=ln>").Append(Esc(new string(' ', width) + " | ")).Append("</span>");
                sb.Append("<span class=caret>").Append(Esc(new string(' ', caret) + "^")).Append("</span>\n");
            }
        }
        return "<pre class=frame>" + sb + "</pre>";
    }

    private static string ExpandTabs(string s) => s.Replace("\t", "    ");
    // MARK: - CSS

    public static string Css(MarkdownShared.Palette p, double fs, bool wrap = true) =>
        "body{font-family:'Cascadia Mono','Cascadia Code',Consolas,monospace;font-size:" + ((int)fs) + "px;" +
        "line-height:1.5;margin:0;padding:10px 16px 24px;word-wrap:break-word;background:" + p.Body.Hex + ";color:" + p.BodyText.Hex + "}" +
        ".tools{position:sticky;top:0;display:flex;gap:8px;align-items:center;padding:8px 0;background:" + p.Body.Hex + ";z-index:2}" +
        ".tools button{font:inherit;font-size:12px;padding:3px 10px;border:1px solid " + p.PanelBorder.Hex + ";border-radius:4px;background:" + p.Panel.Hex + ";color:" + p.BodyText.Hex + ";cursor:pointer;white-space:nowrap}" +
        ".tools button:hover{border-color:" + p.Marker.Hex + "}" +
        "#q{font:inherit;font-size:13px;padding:3px 8px;flex:1;max-width:340px;border:1px solid " + p.PanelBorder.Hex + ";border-radius:4px;background:" + p.Body.Hex + ";color:" + p.BodyText.Hex + "}" +
        "#cnt{font-size:12px;color:" + p.Dim.Hex + ";white-space:nowrap}" +
        "#trunc{margin:6px 0 10px;padding:6px 12px;border:1px solid " + p.Marker.Hex + ";border-radius:6px;background:" + p.Panel.Hex + ";color:" + p.Marker.Hex + ";font-size:13px}" +
        ".nd{margin:1px 0}" +
        ".row{display:flex;align-items:center;gap:4px;padding:1px 4px;border-radius:4px;cursor:pointer;white-space:nowrap}" +
        ".row:hover{background:" + p.Panel.Hex + "}" +
        ".row:focus{outline:1px solid " + p.Marker.Hex + "}" +
        ".row.cur{outline:1px solid " + p.Marker.Hex + "}" +
        ".chev{width:14px;height:14px;flex:none;position:relative;opacity:.75}" +
        ".chev::before{content:'';position:absolute;left:4px;top:3px;border-left:6px solid currentColor;border-top:4px solid transparent;border-bottom:4px solid transparent;transition:transform .08s}" +
        ".nd.open>.row .chev::before{transform:rotate(90deg)}" +
        ".nochev{width:14px;flex:none}" +
        ".k{color:" + p.JsonKey.Hex + "}" +
        ".cm{color:" + p.JsonComment.Hex + ";font-style:italic;white-space:pre-wrap;word-break:break-word;margin:1px 4px}" +
        ".pu,.ix,.sum{color:" + p.Dim.Hex + "}" +
        ".rp{color:" + p.Marker.Hex + ";font-weight:700}" +
        ".vs{color:" + p.JsonString.Hex + "}" +
        ".vn{color:" + p.JsonNumber.Hex + "}" +
        ".vb{color:" + p.JsonBool.Hex + "}" +
        ".vl{color:" + p.JsonNull.Hex + ";font-style:italic}" +
        ".gap{flex:1}" +
        ".ic{opacity:0;border:0;background:none;color:" + p.Dim.Hex + ";cursor:pointer;font-size:13px;padding:0 4px;display:inline-flex;align-items:center}" +
        ".row:hover .ic,.row:focus-within .ic{opacity:.85}" +
        ".ic:hover{color:" + p.BodyText.Hex + "}" +
        "mark{background:#FFD866;color:#111;border-radius:2px;padding:0 1px}" +
        ".kids{margin-left:18px;display:none}" +
        ".nd.open>.kids{display:block}" +
        ".nd.open>.row .sum{display:none}" +
        ".err{max-width:840px;margin:32px auto;padding:0 2px}" +
        ".ehd{display:flex;align-items:center;gap:10px;flex-wrap:wrap}" +
        ".badge{font-size:11px;font-weight:700;letter-spacing:.07em;padding:2px 9px;border-radius:999px;border:1px solid " + p.PanelBorder.Hex + ";background:" + p.Panel.Hex + ";color:" + p.Marker.Hex + "}" +
        ".et{font-size:18px;font-weight:700;color:" + p.Heading.Hex + "}" +
        ".efile{font-size:12px;color:" + p.Dim.Hex + ";margin:4px 0 10px}" +
        ".em{margin-bottom:10px;color:" + p.BodyText.Hex + "}" +
        ".ed{font-size:13px;color:" + p.Dim.Hex + ";margin-bottom:12px}" +
        ".card{margin:12px 0;padding:12px 14px;border:1px solid " + p.PanelBorder.Hex + ";border-radius:8px;background:" + p.Panel.Hex + "}" +
        ".lbl{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.07em;color:" + p.Dim.Hex + ";margin-bottom:6px}" +
        ".msg{white-space:pre-wrap}" +
        ".fix{margin:0;padding-left:22px}.fix li{margin:3px 0}" +
        ".err pre{padding:12px 14px;border-radius:8px;overflow:auto;border:1px solid " + p.PanelBorder.Hex + ";background:" + p.Body.Hex + ";max-height:50vh}" +
        ".err pre.frame{max-height:220px;margin:8px 0 0}" +
        ".frame .ln{color:" + p.Dim.Hex + "}" +
        ".frame .bad{background:rgba(209,52,56,.18);border-radius:2px}" +
        ".frame .caret{color:" + p.JsonString.Hex + ";font-weight:700}" +
        ".acts{display:flex;gap:8px;flex-wrap:wrap;margin:16px 0 6px}" +
        ".acts button{font:inherit;font-size:13px;padding:6px 14px;border:1px solid " + p.PanelBorder.Hex + ";border-radius:5px;background:" + p.Panel.Hex + ";color:" + p.BodyText.Hex + ";cursor:pointer}" +
        ".acts button:hover{border-color:" + p.Marker.Hex + "}" +
        ".acts button.pri{background:" + p.Marker.Hex + ";border-color:" + p.Marker.Hex + ";color:#FFFFFF}" +
        ".eh{margin-top:10px;font-size:13px;color:" + p.Dim.Hex + "}" +
        ".srcsum{margin-top:14px;font-size:13px;color:" + p.Dim.Hex + "}" +
        ".srcsum summary{cursor:pointer}" +
        "@media print{.tools,.ic{display:none!important}*{print-color-adjust:exact!important;-webkit-print-color-adjust:exact!important}" +
        "body{background:#FFFFFF!important;color:#1E1E1E!important}" +
        ".row:hover{background:none!important}.kids{display:block!important}.nd.open>.row .sum{display:none!important}" +
        ".k{color:#001080!important}.pu,.ix,.sum{color:#808080!important}.rp{color:#0F6CBD!important}" +
        ".vs{color:#A31515!important}.vn{color:#098658!important}.vb{color:#0F6CBD!important}.vl{color:#808080!important}" +
        ".cm{color:#008000!important}" +
        "mark{background:#FFD866!important;color:#111!important}}" +
        WrapCss(wrap);

    /// <summary>
    /// "A capo automatico" applied to the preview too: on, tree rows and values
    /// break at the container edge; off, nothing wraps and the page scrolls
    /// horizontally (print always wraps, paper has no horizontal scroll).
    /// </summary>
    private static string WrapCss(bool wrap) =>
        wrap
        ? ".row{white-space:normal}.k,.vs,.vn,.vb,.vl{min-width:0;word-break:break-word}"
        : "body{white-space:nowrap;word-wrap:normal}" +
          // Directly-applied rules beat the inherited body value, so every
          // text-bearing span restates nowrap. Error chrome instead always
          // wraps inside its cards (a spilling message looks broken);
          // only source blocks (pre) scroll.
          ".k,.vs,.vn,.vb,.vl,.pu,.ix,.sum{white-space:nowrap;word-break:normal}" +
          ".cm{white-space:pre;word-break:normal}" +
          ".et,.em,.efile,.msg,.lbl,.fix,.eh{white-space:normal;overflow-wrap:break-word}" +
          "@media print{body{white-space:normal;word-wrap:break-word}.row{white-space:normal}" +
          ".k,.vs,.vn,.vb,.vl,.pu,.ix,.sum{white-space:normal}" +
          ".cm{white-space:pre-wrap}.vs,.vn,.vb,.vl{word-break:break-word}.msg{white-space:pre-wrap}}";

    // MARK: - JS (tree widget; all value text via textContent/esc, never raw innerHTML)

    /// <summary>JS helpers shared by the tree widget and the CSV grid.</summary>
    public static string JsHelpers() => """
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function post(o){try{if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}
        function E(t,c){var e=document.createElement(t);if(c)e.className=c;return e;}
        function disp(s,max){s=String(s);return s.length>max?s.slice(0,max)+'…':s;}
        function markIn(el,q){
        if(el._o===undefined)el._o=el.textContent;
        var t=el._o,low=t.toLowerCase(),h='',i=0,k;
        while((k=low.indexOf(q,i))>=0){h+=esc(t.slice(i,k))+'<mark>'+esc(t.slice(k,k+q.length))+'</mark>';i=k+q.length;}
        el.innerHTML=h+esc(t.slice(i));
        }
        """;

    public static string Js() => JsHelpers() + """
        (function(){
        "use strict";
        var uidc=0,byUid={},byPtr={},matches=[],cur=-1,qraw='';
        var open={};(opns||[]).forEach(function(p){open[p]=1;});
        var host=document.getElementById('tree'),input=document.getElementById('q'),cnt=document.getElementById('cnt');
        var icoV='<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="4.5" y="4.5" width="6" height="6" rx="1" fill="none" stroke="currentColor"/><path d="M7.5 4.5v-2a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h2" fill="none" stroke="currentColor"/></svg>';
        function postState(){try{var a=[];for(var k in open)if(open[k])a.push(k);post({op:'state',open:a,q:qraw});}catch(e){}}
        function segEsc(s){return s.replace(/~/g,'~0').replace(/\//g,'~1');}
        function segUn(s){return s.replace(/~1/g,'/').replace(/~0/g,'~');}
        function isC(n){return (n.t==='o'||n.t==='a')&&n.c&&n.c.length>0;}
        function childPtr(p,isArr,key,idx){return p+'/'+(isArr?idx:segEsc(key));}
        function childJ(j,isArr,key,idx){if(typeof __XML__!=='undefined'&&__XML__)return j+'/'+key+(idx>1?'['+idx+']':'');if(isArr)return j+'['+idx+']';if(/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key))return j+'.'+key;return j+'['+JSON.stringify(key)+']';}
        function sumHtml(n){var c=n.c?n.c.length:0;if(n.t==='o')return '<span class=sum>'+(c?'{ '+c+' }':'{ }')+'</span>';return '<span class=sum>'+(c?'[ '+c+' ]':'[ ]')+'</span>';}
        function leafHtml(n){
        if(n.t==='s')return '<span class="vv vs">'+esc('"'+disp(n.v,500)+'"')+'</span>';
        if(n.t==='n')return '<span class="vv vn">'+esc(n.v)+'</span>';
        if(n.t==='b')return '<span class="vv vb">'+esc(n.v)+'</span>';
        return '<span class=vl>null</span>';
        }
        function buildNd(n,p,j,key,isIdx,isRoot){
        var u='u'+(++uidc);n._u=u;
        var nd=E('div','nd');nd.setAttribute('data-p',p);
        if(n.m)for(var mi=0;mi<n.m.length;mi++){var cm0=E('div','cm');cm0.textContent=disp(n.m[mi],500);nd.appendChild(cm0);}
        var row=E('div','row');row.setAttribute('data-u',u);row.setAttribute('data-l',n.l);row.tabIndex=0;
        var h=isC(n)?'<span class=chev></span>':'<span class=nochev></span>';
        if(isRoot)h+='<span class=rp>$</span> ';
        else if(key!==null&&key!==undefined)h+=isIdx?'<span class=ix>'+esc(key)+':</span> ':'<span class=k>'+esc('"'+key+'"')+'</span><span class=pu>:</span> ';
        h+=isC(n)?sumHtml(n):leafHtml(n);
        h+='<span class=gap></span>';
        h+='<button class=ic title="'+esc(STR.cp)+'" data-w=p tabindex="-1">$</button>';
        h+='<button class=ic title="'+esc(STR.cv)+'" data-w=v tabindex="-1">'+icoV+'</button>';
        row.innerHTML=h;nd.appendChild(row);nd.appendChild(E('div','kids'));
        if(n.e)for(var ei=0;ei<n.e.length;ei++){var cm1=E('div','cm');cm1.textContent=disp(n.e[ei],500);nd.appendChild(cm1);}
        byUid[u]={n:n,el:nd,p:p,j:j};(byPtr[p]||(byPtr[p]=[])).push(u);
        return nd;
        }
        function renderKids(ndEl,rec){
        var kids=ndEl.querySelector(':scope > .kids');
        if(kids.hasChildNodes())return;
        var n=rec.n,isArr=n.t==='a';
        var isX=typeof __XML__!=='undefined'&&__XML__,total=null,occ=null;
        if(isX){total={};occ={};for(var t=0;t<n.c.length;t++){var ck=n.c[t].k;total[ck]=(total[ck]||0)+1;}}
        for(var i=0;i<n.c.length;i++){var c=n.c[i];
        var key=isArr?String(i):c.k,oi=i;
        if(isX){occ[key]=(occ[key]||0)+1;oi=occ[key];if(total[key]<=1)oi=0;}
        kids.appendChild(buildNd(c,childPtr(rec.p,isX?true:isArr,c.k,i),childJ(rec.j,isArr,key,isX?oi:i),key,isArr&&!isX,false));}
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
        var isX=typeof __XML__!=='undefined'&&__XML__;
        var ix=parseInt(segs[i],10);
        if(isArr){if(ix>=0&&ix<node.c.length)ci=ix;}
        else if(isX&&ix>=0&&ix<node.c.length&&String(ix)===segs[i])ci=ix;
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
        function xesc(s){if(s==null)return '';return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function xmlCopy(n){
        if(n.t==='s'){
        if(!n.k||n.k==='#text'||n.k.charAt(0)==='@')return xesc(n.v);
        return '<'+n.k+'>'+xesc(n.v)+'</'+n.k+'>';
        }
        var attrs='',inner='';
        for(var k=0;k<(n.c||[]).length;k++){var c=n.c[k];
        if(c.k&&c.k.charAt(0)==='@')attrs+=' '+c.k.slice(1)+'="'+xesc(c.v)+'"';
        else inner+=xmlCopy(c);}
        var tag=n.k||'';
        if(!tag)return inner;
        if(!inner)return '<'+tag+attrs+'/>';
        return '<'+tag+attrs+'>'+inner+'</'+tag+'>';
        }
        function valText(n){
        if(typeof __XML__!=='undefined'&&__XML__)return xmlCopy(n);
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
        function walkN(n,p,key,cb){cb(n,p,key);if(n.c){var isArr=n.t==='a';var isX=typeof __XML__!=='undefined'&&__XML__;for(var i=0;i<n.c.length;i++){var c=n.c[i];walkN(c,childPtr(p,isX?true:isArr,c.k,i),isArr?null:c.k,cb);}}}
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
        var rootJ=(typeof __XML__!=='undefined'&&__XML__)?'':'$';
        var r=buildNd(root,'',rootJ,null,false,true);
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
        """;
}
