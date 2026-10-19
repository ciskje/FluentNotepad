import AppKit
import SwiftUI
import WebKit

/// WKWebView that opens dropped files as tabs instead of navigating to them
/// (same claim-it-here pattern as EditorTextView: no reliance on bubbling).
final class PreviewWebView: WKWebView {
    var onFileDrop: ((URL) -> Void)?
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        if sender.isFileDrop { return .copy }
        return super.draggingEntered(sender)
    }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        if sender.isFileDrop { return .copy }
        return super.draggingUpdated(sender)
    }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        if sender.isFileDrop {
            let files = sender.fileURLs
            for url in files { onFileDrop?(url) }
            return !files.isEmpty
        }
        return super.performDragOperation(sender)
    }
}

/// Native WKWebView preview. One instance for the active document (like WinUI).
/// Clicks on rendered blocks jump back to the source line (option 3):
/// the Markdown HTML carries data-src-line (see MarkdownHTML), a document-start
/// script forwards the nearest one, the coordinator posts .srcLineTapped.
/// The JSON tree preview instead talks through window.chrome.webview (a shim
/// below maps it onto the fnTree handler, same JSON protocol as Windows'
/// Preview_WebMessage: {op:line|copy|state}).
struct PreviewView: NSViewRepresentable {
    var html: String
    var webRef: WebRef
    /// A file dropped onto the preview opens as a tab instead of navigating.
    var onFileDrop: ((URL) -> Void)?
    /// Tree-preview protocol (JSON): leaf click -> source line.
    var onTreeLine: ((Int) -> Void)?
    /// Tree-preview protocol (JSON): copy path/value buttons.
    var onTreeCopy: ((String) -> Void)?
    /// Tree-preview protocol (JSON): open nodes + search text (per-tab state).
    var onTreeState: (([String], String) -> Void)?

    func makeCoordinator() -> C { C() }

    func makeNSView(context: Context) -> WKWebView {
        let ucc = WKUserContentController()
        ucc.addUserScript(WKUserScript(source: Self.clickJS, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        ucc.addUserScript(WKUserScript(source: Self.chromeShimJS, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        ucc.add(context.coordinator, name: "srcLine")
        ucc.add(context.coordinator, name: "fnTree")
        let cfg = WKWebViewConfiguration()
        cfg.userContentController = ucc
        let w = PreviewWebView(frame: .zero, configuration: cfg)
        w.navigationDelegate = context.coordinator
        w.onFileDrop = onFileDrop
        context.coordinator.onFileDrop = onFileDrop
        context.coordinator.onTreeLine = onTreeLine
        context.coordinator.onTreeCopy = onTreeCopy
        context.coordinator.onTreeState = onTreeState
        webRef.view = w
        if !html.isEmpty { w.loadHTMLString(html, baseURL: nil) }
        return w
    }

    func updateNSView(_ w: WKWebView, context: Context) {
        webRef.view = w
        context.coordinator.onFileDrop = onFileDrop
        context.coordinator.onTreeLine = onTreeLine
        context.coordinator.onTreeCopy = onTreeCopy
        context.coordinator.onTreeState = onTreeState
        (w as? PreviewWebView)?.onFileDrop = onFileDrop
        if context.coordinator.lastHTML != html {
            context.coordinator.lastHTML = html
            w.loadHTMLString(html, baseURL: nil)
        }
    }

    /// Click on a rendered block -> nearest data-src-line. preventDefault so
    /// links jump to source instead of navigating away; a real text selection
    /// (non-collapsed) is left alone so copy still works.
    private static let clickJS = """
    document.addEventListener('click', function(e) {
      try {
        if (window.getSelection && !window.getSelection().isCollapsed) return;
        var t = e.target;
        var el = (t && t.closest) ? t.closest('[data-src-line]') : null;
        if (el) {
          e.preventDefault();
          window.webkit.messageHandlers.srcLine.postMessage(el.getAttribute('data-src-line'));
        }
      } catch (err) {}
    }, false);
    """

    /// Maps window.chrome.webview.postMessage (the shared tree widget's only
    /// host call, same as WebView2 on Windows) onto the fnTree handler.
    private static let chromeShimJS = """
    window.chrome={webview:{postMessage:function(s){window.webkit.messageHandlers.fnTree.postMessage(s);}}};
    """

    final class C: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        var lastHTML = ""
        var onFileDrop: ((URL) -> Void)?
        var onTreeLine: ((Int) -> Void)?
        var onTreeCopy: ((String) -> Void)?
        var onTreeState: (([String], String) -> Void)?

        func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
            if message.name == "srcLine" {
                guard let body = message.body as? String,
                    let line = Int(body)
                else { return }
                NotificationCenter.default.post(name: .srcLineTapped, object: nil, userInfo: ["line": line])
                return
            }
            guard message.name == "fnTree", let body = message.body as? String,
                let data = body.data(using: .utf8),
                let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                let op = o["op"] as? String
            else { return }
            switch op {
            case "line":
                if let n = o["line"] as? NSNumber, n.intValue >= 1 { onTreeLine?(n.intValue) }
            case "copy":
                if let t = o["text"] as? String { onTreeCopy?(t) }
            case "state":
                let open = (o["open"] as? [String]) ?? []
                let q = (o["q"] as? String) ?? ""
                onTreeState?(open, q)
            default: break
            }
        }

        // The preview is built with loadHTMLString (about:blank) and links are
        // click-trapped in JS, so a file:// main-frame navigation can only be
        // a dropped file: open it as a tab instead of navigating away.
        func webView(_ w: WKWebView, decidePolicyFor action: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void)
        {
            if let url = action.request.url, url.isFileURL {
                onFileDrop?(url)
                decisionHandler(.cancel)
                return
            }
            decisionHandler(.allow)
        }
    }
}

/// Handle for printing the preview web view. Main-thread confined.
final class WebRef: @unchecked Sendable {
    weak var view: WKWebView?

    func print() {
        guard let w = view else { return }
        let info = NSPrintInfo.shared
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        let op = w.printOperation(with: info)
        op.run()
    }

    /// Fire-and-forget JS (tree search clear on ESC, like Windows).
    func runJS(_ script: String) {
        view?.evaluateJavaScript(script, completionHandler: nil)
    }
}
