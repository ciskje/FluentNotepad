import AppKit
import SwiftUI

/// NSTextView that opens dropped files as tabs (see below).
final class EditorTextView: NSTextView {
    /// File drops open as tabs right here (claim them: relying on the drop
    /// bubbling up to another handler leaves some surfaces with nowhere to go
    /// and the drag bounces back). Everything else keeps NSTextView behavior.
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
/// Native NSTextView editor. System colors follow light/dark automatically;
/// undo, services and the Edit menu come from AppKit for free.
struct EditorView: NSViewRepresentable {
    var doc: Doc
    var fontSize: Double
    var wrap: Bool
    var dark: Bool
    var onFileDrop: ((URL) -> Void)?

    static func baseFont(size: Double) -> NSFont {
        NSFont.monospacedSystemFont(ofSize: size, weight: .regular)
    }

    static func refreshHighlight(_ tv: NSTextView, fontSize: Double, dark: Bool, coord: C) {
        // One hash (O(n), non-cryptographic): skip when nothing relevant changed.
        let markdown = coord.doc.isMarkdown
        let key = "\(tv.string.count)-\(tv.string.hashValue)-\(fontSize)-\(dark)-\(markdown)"
        guard coord.lastKey != key else { return }
        coord.lastKey = key
        SyntaxHighlight.apply(to: tv, baseFont: baseFont(size: fontSize), dark: dark, markdown: markdown)
    }

    func makeCoordinator() -> C { C(doc: doc) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder
        let tv = EditorTextView()
        tv.delegate = context.coordinator
        tv.textStorage?.delegate = context.coordinator
        tv.onFileDrop = onFileDrop
        tv.isRichText = false
        tv.importsGraphics = false
        tv.allowsUndo = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.textContainerInset = NSSize(width: 14, height: 10)
        tv.usesFindBar = true
        // Static appearance: system colors auto-adapt to light/dark, so set once.
        // (Re-setting .font/.textColor here or in style() wipes highlight
        // attributes — font is set only when its size changes, see updateNSView.)
        tv.font = Self.baseFont(size: fontSize)
        tv.textColor = .textColor
        tv.backgroundColor = .textBackgroundColor
        tv.insertionPointColor = .textColor
        context.coordinator.lastFontSize = fontSize
        scroll.documentView = tv
        context.coordinator.view = tv
        context.coordinator.scroll = scroll
        tv.string = doc.text
        style(tv, scroll)
        Self.refreshHighlight(tv, fontSize: fontSize, dark: dark, coord: context.coordinator)
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let tv = scroll.documentView as? NSTextView else { return }
        let c = context.coordinator
        tv.textStorage?.delegate = c
        if c.lastFontSize != fontSize {
            c.lastFontSize = fontSize
            tv.font = Self.baseFont(size: fontSize)
            c.lastKey = "" // font assignment wipes attributes: force re-highlight below
        }
        style(tv, scroll)
        c.fontSize = fontSize
        c.dark = dark
        (tv as? EditorTextView)?.onFileDrop = onFileDrop
        if let op = doc.pendingOp {
            doc.pendingOp = nil
            c.applying = true
            if let range = Range(op.range, in: tv.string) {
                let ns = NSRange(range, in: tv.string)
                if tv.shouldChangeText(in: ns, replacementString: op.insert) {
                    tv.textStorage?.replaceCharacters(in: ns, with: op.insert)
                    tv.didChangeText()
                }
            }
            if let sel = op.select, let r = Range(sel, in: tv.string) {
                tv.setSelectedRange(NSRange(r, in: tv.string))
            }
            c.applying = false
            doc.text = tv.string
        } else if tv.string != doc.text, !c.applying {
            c.applying = true
            let sel = tv.selectedRange()
            tv.string = doc.text
            let max = (tv.string as NSString).length
            tv.setSelectedRange(NSRange(location: min(sel.location, max), length: 0))
            c.applying = false
        }
        if let r = doc.revealRange {
            doc.revealRange = nil
            tv.setSelectedRange(r)
            tv.scrollRangeToVisible(r)
            tv.showFindIndicator(for: r)
            tv.window?.makeFirstResponder(tv)
        }
        Self.refreshHighlight(tv, fontSize: fontSize, dark: dark, coord: c)
    }

    private func style(_ tv: NSTextView, _ scroll: NSScrollView) {
        // Wrap geometry only: never touch font/colors here, it wipes highlighting.
        if wrap {
            tv.textContainer?.widthTracksTextView = true
            tv.isHorizontallyResizable = false
            tv.autoresizingMask = [.width]
            tv.textContainer?.size = NSSize(
                width: max(scroll.contentSize.width, 100), height: CGFloat.greatestFiniteMagnitude)
        } else {
            tv.textContainer?.widthTracksTextView = false
            tv.isHorizontallyResizable = true
            tv.autoresizingMask = [.width, .height]
            tv.textContainer?.size = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
            tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        }
    }

    final class C: NSObject, NSTextViewDelegate, NSTextStorageDelegate {
        var doc: Doc
        var applying = false
        var lastKey = ""
        var lastFontSize = -1.0
        var fontSize = 15.0
        var dark = false
        weak var view: NSTextView?
        weak var scroll: NSScrollView?
        init(doc: Doc) { self.doc = doc }

        func textDidChange(_ notification: Notification) {
            guard !applying, let tv = view else { return }
            doc.text = tv.string
            EditorView.refreshHighlight(tv, fontSize: fontSize, dark: dark, coord: self)
        }

        /// Undo/redo bypass textDidChange (AppKit notifies only key/paste
        /// edits): without this the Doc keeps pre-undo text and the next
        /// refresh snaps the view back. Attribute-only passes (our own
        /// highlight) are harmless: same text, same highlight key.
        func textStorage(_ ts: NSTextStorage,
            didProcessEditing editedMask: NSTextStorageEditActions,
            range editedRange: NSRange, changeInLength delta: Int)
        {
            guard !applying, let tv = view, ts === tv.textStorage else { return }
            doc.text = tv.string
            EditorView.refreshHighlight(tv, fontSize: fontSize, dark: dark, coord: self)
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let tv = view else { return }
            let r = tv.selectedRange()
            doc.caret = r.location
            doc.selLen = r.length
        }
    }
}
