import Foundation

/// Shared collapsible-tree widget: VS palette + CSS + JS used by the JSON and
/// XML previews (verbatim port of Windows' PreviewTree.cs). The page embeds
/// data nodes; all value text goes through textContent/esc, never raw innerHTML
/// from values. post() reaches Swift through the window.chrome.webview shim
/// installed by PreviewView. Rows also carry data-l (source line) so the
/// click-to-source blink finds tree rows like Markdown data-src-line rows.
enum PreviewTree {
    /// Parse failure with a 1-based position (all tree parsers report lines
    /// starting at 1, like the editor shows them).
    struct ParseError: Error {
        var line: Int
        var column: Int
        var message: String
    }

    // Caps shared by every preview builder (same values as Windows).
    static let maxChars = 10_000_000
    static let maxNodes = 300_000
    static let maxDepth = 200
    static let maxKidsAfterTrunc = 1000

    /// Full-size-limit page, shared by every preview builder. The CSV grid
    /// passes its own CSS through `extraCss`.
    static func tooLargePage(dark: Bool, fontSize: Double, title: String?, message: String,
        extraCss: String = "") -> String
    {
        let pal = Palette(dark: dark)
        return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
            + "<title>" + esc(title ?? "Preview") + "</title>"
            + "<style>" + css(pal, Int(fontSize)) + extraCss + "</style></head><body>"
            + "<div class=err><div class=et>" + esc(message) + "</div></div></body></html>"
    }

    /// Full tree-preview page (JSON/XML/YAML/TOML/INI): toolbar, truncation
    /// banner, tree host, payload script. User content arrives via the
    /// replacement VALUES, which the template tokens never rescan.
    /// Foundation's encoder escapes `/` (neutralizes `</script>`).
    static func treePage(dark: Bool, fontSize: Double, title: String?,
        rootJSON: String, openJSON: String, truncated: Bool, truncText: String, isXML: Bool) -> String
    {
        let pal = Palette(dark: dark)
        let head = "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
            + "<title>" + esc(title) + "</title>"
            + "<style>" + css(pal, Int(fontSize)) + "</style></head><body>"
        let page = head + toolbar()
            + (truncated ? "<div id=trunc>" + esc(truncText) + "</div>" : "")
            + "<div id=tree></div>"
            + "<script>var root=__D0__;var opns=__O0__;var STR=__S0__;var __XML__=__X0__;\n" + js()
            + "</script></body></html>"
        return page
            .replacingOccurrences(of: "__D0__", with: rootJSON)
            .replacingOccurrences(of: "__O0__", with: openJSON)
            .replacingOccurrences(of: "__S0__", with: stringsJSON())
            .replacingOccurrences(of: "__X0__", with: isXML ? "true" : "false")
    }

    /// Generic invalid-document page (mirrors Windows' PreviewTree.ErrorPage).
    static func errorPage(dark: Bool, fontSize: Double, title: String?,
        heading: String, invalidLine: String, details: String, source: String, hint: String) -> String
    {
        let pal = Palette(dark: dark)
        let src = source.count > 100_000 ? String(source.prefix(100_000)) + "\n…" : source
        return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
            + "<meta name='color-scheme' content='" + (dark ? "dark" : "light") + "'>"
            + "<title>" + esc(title ?? "Preview") + "</title>"
            + "<style>" + css(pal, Int(fontSize)) + "</style></head><body>"
            + "<div class=err><div class=et>" + esc(heading) + "</div>"
            + "<div class=em>" + esc(invalidLine) + "</div>"
            + "<div class=ed>" + esc(details) + "</div>"
            + "<pre>" + esc(src) + "</pre>"
            + "<div class=eh>" + esc(hint) + "</div></div></body></html>"
    }

    static func toolbar() -> String {
        "<div class=tools><button id=bExp>" + esc(L("treeExpandAll")) + "</button>"
            + "<button id=bCol>" + esc(L("treeCollapseAll")) + "</button>"
            + "<input id=q placeholder=\"" + esc(L("treeSearch")) + "\"><span id=cnt></span></div>"
    }

    static func treeStrings() -> [String: String] {
        ["exp": L("treeExpandAll"), "col": L("treeCollapseAll"), "cp": L("treeCopyPath"),
         "cv": L("treeCopyValue"), "match": L("treeMatches"), "none": L("treeNoMatch")]
    }

    static func stringsJSON() -> String {
        String(data: (try? JSONEncoder().encode(treeStrings())) ?? Data("{}".utf8), encoding: .utf8) ?? "{}"
    }

    static func esc(_ s: String?) -> String {
        (s ?? "").replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }
    // MARK: - Palette (same VS tones as Windows' MarkdownShared)

    struct Palette {
        let body, bodyText, panel, panelBorder, marker, dim: String
        let heading, jsonKey, jsonString, jsonNumber, jsonBool, jsonNull, jsonComment: String
        let codeBg, cellBorder, zebra: String
        init(dark: Bool) {
            if dark {
                body = "#1E1E1E"; bodyText = "#D4D4D4"; panel = "#252526"; panelBorder = "#3F3F46"
                marker = "#569CD6"; dim = "#808080"; heading = "#569CD6"
                jsonKey = "#9CDCFE"; jsonString = "#CE9178"; jsonNumber = "#B5CEA8"
                jsonBool = "#569CD6"; jsonNull = "#808080"; jsonComment = "#6A9950"
                codeBg = "#2D2D30"; cellBorder = "#3F3F46"; zebra = "#232326"
            } else {
                body = "#FFFFFF"; bodyText = "#1E1E1E"; panel = "#F6F6F6"; panelBorder = "#E5E5E5"
                marker = "#0F6CBD"; dim = "#808080"; heading = "#0F6CBD"
                jsonKey = "#001080"; jsonString = "#A31515"; jsonNumber = "#098658"
                jsonBool = "#0F6CBD"; jsonNull = "#808080"; jsonComment = "#008000"
                codeBg = "#F3F3F3"; cellBorder = "#D4D4D4"; zebra = "#FAFAFA"
            }
        }
    }

    // MARK: - CSS (verbatim port of Windows' PreviewTree.Css)

    static func css(_ p: Palette, _ fs: Int) -> String {
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

    // MARK: - JS (verbatim port of Windows' PreviewTree.Js)

    static func js() -> String {
        #"""
                (function(){
        "use strict";
        var uidc=0,byUid={},byPtr={},matches=[],cur=-1,qraw='';
        var open={};(opns||[]).forEach(function(p){open[p]=1;});
        var host=document.getElementById('tree'),input=document.getElementById('q'),cnt=document.getElementById('cnt');
        var icoV='<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="4.5" y="4.5" width="6" height="6" rx="1" fill="none" stroke="currentColor"/><path d="M7.5 4.5v-2a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h2" fill="none" stroke="currentColor"/></svg>';
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function post(o){try{if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}
        function postState(){try{var a=[];for(var k in open)if(open[k])a.push(k);post({op:'state',open:a,q:qraw});}catch(e){}}
        function segEsc(s){return s.replace(/~/g,'~0').replace(/\//g,'~1');}
        function segUn(s){return s.replace(/~1/g,'/').replace(/~0/g,'~');}
        function E(t,c){var e=document.createElement(t);if(c)e.className=c;return e;}
        function isC(n){return (n.t==='o'||n.t==='a')&&n.c&&n.c.length>0;}
        function childPtr(p,isArr,key,idx){return p+'/'+(isArr?idx:segEsc(key));}
        function childJ(j,isArr,key,idx){if(typeof __XML__!=='undefined'&&__XML__)return j+'/'+key+(idx>1?'['+idx+']':'');if(isArr)return j+'['+idx+']';if(/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key))return j+'.'+key;return j+'['+JSON.stringify(key)+']';}
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
        var row=E('div','row');row.setAttribute('data-u',u);row.setAttribute('data-l',n.l);row.tabIndex=0;
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
        
        """#
    }
}
