import AppKit
import SwiftUI
import WebKit

/// WKWebView that opens dropped files as tabs instead of navigating to them
/// (same claim-it-here pattern as EditorTextView: no reliance on bubbling).
final class PreviewWebView: WKWebView {
    var onFileDrop: ((URL) -> Void)?
    private static func isFileDrop(_ sender: NSDraggingInfo) -> Bool {
        sender.draggingPasteboard.types?.contains(.fileURL) == true
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        if Self.isFileDrop(sender) { return .copy }
        return super.draggingEntered(sender)
    }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        if Self.isFileDrop(sender) { return .copy }
        return super.draggingUpdated(sender)
    }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        if Self.isFileDrop(sender) {
            let files = ((sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL]) ?? [])
                .filter(\.isFileURL)
            for url in files { onFileDrop?(url) }
            return !files.isEmpty
        }
        return super.performDragOperation(sender)
    }
}

/// Native WKWebView preview. One instance for the active document (like WinUI).
/// Clicks on rendered blocks jump back to the source line (option 3):
/// the HTML carries data-src-line (see MarkdownHTML), a document-start script
/// forwards the nearest one, the coordinator posts .srcLineTapped.
struct PreviewView: NSViewRepresentable {
    var html: String
    var webRef: WebRef
    /// A file dropped onto the preview opens as a tab instead of navigating.
    var onFileDrop: ((URL) -> Void)?

    func makeCoordinator() -> C { C(ref: webRef) }

    func makeNSView(context: Context) -> WKWebView {
        let ucc = WKUserContentController()
        ucc.addUserScript(WKUserScript(source: Self.clickJS, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        ucc.add(context.coordinator, name: "srcLine")
        let cfg = WKWebViewConfiguration()
        cfg.userContentController = ucc
        let w = PreviewWebView(frame: .zero, configuration: cfg)
        w.setValue(false, forKey: "drawsBackground")
        w.navigationDelegate = context.coordinator
        w.onFileDrop = onFileDrop
        context.coordinator.onFileDrop = onFileDrop
        context.coordinator.onFileDrop = onFileDrop
        webRef.view = w
        if !html.isEmpty { w.loadHTMLString(html, baseURL: nil) }
        return w
    }

    func updateNSView(_ w: WKWebView, context: Context) {
        webRef.view = w
        context.coordinator.onFileDrop = onFileDrop
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

    final class C: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        var lastHTML = ""
        var onFileDrop: ((URL) -> Void)?
        init(ref: WebRef) {}

        func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.name == "srcLine",
                let body = message.body as? String,
                let line = Int(body)
            else { return }
            NotificationCenter.default.post(name: .srcLineTapped, object: nil, userInfo: ["line": line])
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

/// Handle for print / JS calls on the preview web view. Main-thread confined.
final class WebRef: @unchecked Sendable {
    weak var view: WKWebView?

    func setTitle(_ title: String) {
        let data = (try? JSONSerialization.data(withJSONObject: [title])) ?? Data("[]".utf8)
        let js = String(data: data, encoding: .utf8).map { "document.title = \($0)[0];" } ?? ""
        view?.evaluateJavaScript(js, completionHandler: nil)
    }

    func print() {
        guard let w = view else { return }
        let info = NSPrintInfo.shared
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        let op = w.printOperation(with: info)
        op.run()
    }
}
