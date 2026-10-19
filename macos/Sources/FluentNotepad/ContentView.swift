import AppKit
import SwiftUI

/// Main window: tab strip, native toolbar, editor/preview, find bar, status bar.
struct ContentView: View {
    @Environment(\.colorScheme) private var scheme
    @Environment(\.openSettings) private var openSettings
    @Bindable var store: Store

    @State private var findShown = false
    @State private var findText = ""
    @State private var renderTimer: Timer?

    private var dark: Bool {
        switch store.theme {
        case "light": false
        case "dark": true
        default: scheme == .dark
        }
    }

    private var doc: Doc? { store.selected }

    var body: some View {
        VStack(spacing: 0) {
            TabStrip(activeID: doc?.id, docs: store.docs,
                select: { store.selected = $0 }, close: { store.requestClose($0) },
                add: { _ = store.newTab() })
            Divider()
            editorArea
            Divider()
            statusBar
        }
        .navigationTitle(doc?.displayName ?? "FluentNotepad")
        .onOpenURL { url in store.open(url: url) }
        .toolbar { toolbarContent }
        .onChange(of: doc?.text ?? "") { scheduleRender() }
        .onChange(of: dark) { renderSelected() }
        .onChange(of: store.effectiveFontSize) { renderSelected() }
        .onChange(of: doc?.id) { renderSelected() }
        .onReceive(NotificationCenter.default.publisher(for: .toggleFind)) { _ in
            findShown.toggle()
        }
        .onReceive(NotificationCenter.default.publisher(for: .srcLineTapped)) { n in
            if let line = n.userInfo?["line"] as? Int { jumpToLine(line) }
        }
        .onReceive(NotificationCenter.default.publisher(for: .escapeToggle)) { _ in
            // ESC flips Edit/Preview (both directions). The find field keeps
            // its own Escape (close bar): the monitor below lets it through.
            // The tree preview's search owns ESC first (clears it, stays put).
            if let doc = store.selected, doc.canPreview {
                if doc.showPreview, doc.isJSON, !doc.treeQuery.isEmpty {
                    doc.treeQuery = ""
                    store.webRef.runJS("if(window.__fnClearSearch)window.__fnClearSearch();")
                } else {
                    showPreview(doc, !doc.showPreview)
                }
            }
        }
        .onDrop(of: [.fileURL], isTargeted: nil, perform: handleDrop)
    }

    // MARK: - Editor / preview area

    @ViewBuilder
    private var editorArea: some View {
        ZStack(alignment: .topTrailing) {
            if let doc {
                if doc.showPreview, doc.canPreview {
                    PreviewView(html: doc.renderedHTML, webRef: store.webRef,
                        onFileDrop: { store.open(url: $0) },
                        onTreeLine: { jumpToLine($0) },
                        onTreeCopy: {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString($0, forType: .string)
                        },
                        onTreeState: {
                            doc.treeOpen = Set($0)
                            doc.treeQuery = $1
                        })
                        .onAppear { render(doc) }
                } else {
                    EditorView(doc: doc, fontSize: store.effectiveFontSize, wrap: store.wrap, dark: dark,
                        onFileDrop: { store.open(url: $0) })
                }
            }
            if findShown {
                FindBarView(text: $findText, next: { find(up: false) },
                    prev: { find(up: true) }, close: { findShown = false })
            }
        }
    }

    @ViewBuilder
    private var statusBar: some View {
        if let doc {
            let (line, col) = lineCol(doc)
            HStack(spacing: 8) {
                Text(L("statusLine", line, col))
                Divider().frame(height: 12)
                Text(L("statusChars", doc.text.count))
                Divider().frame(height: 12)
                Text(doc.isMarkdown ? "Markdown" : doc.isJSON ? L("fileTypeJson") : L("fileTypePlain"))
                Spacer()
                Stepper(value: $store.zoom, in: 0.5 ... 3.0, step: 0.1) {
                    Text("\(Int(store.zoom * 100))%")
                }
                .controlSize(.small)
                Divider().frame(height: 12)
                Text(doc.lineEnding)
                Divider().frame(height: 12)
                Text(doc.fileEncoding)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 4)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        for p in providers {
            _ = p.loadObject(ofClass: URL.self) { url, _ in
                if let url { DispatchQueue.main.async { store.open(url: url) } }
            }
        }
        return true
    }

    // MARK: - Native toolbar (unified, glassy on Tahoe)

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            if let doc, doc.canPreview {
                Picker("", selection: Bindable(doc).showPreview) {
                    Text(L("edit")).tag(false)
                    Text(L("preview")).tag(true)
                }
                .pickerStyle(.segmented)
                .frame(width: 170)
                .onChange(of: doc.showPreview) { _, show in if show { render(doc) } }
            }
        }
        ToolbarItemGroup(placement: .secondaryAction) {
            if doc?.isMarkdown == true { fmtButtons }
        }
        ToolbarItemGroup(placement: .navigation) {
            Button { if let doc { store.save(doc) } } label: {
                Label(L("save"), systemImage: "square.and.arrow.down")
            }
            .help(L("save"))
            .disabled(doc?.dirty != true)
            Button { findShown.toggle() } label: {
                Label(L("find"), systemImage: "magnifyingglass")
            }
            .keyboardShortcut("f", modifiers: .command)
            Button { store.theme = dark ? "light" : "dark" } label: {
                Label(L("theme"), systemImage: dark ? "moon.fill" : "sun.max.fill")
            }
            Button { openSettings() } label: {
                Label(L("settings"), systemImage: "gearshape")
            }
        }
    }

    private var fmtButtons: some View {
        Group {
            Button("H") { wrapSel("## ", "", lineStart: true) }
                .help(L("fmtHeading")).bold()
            Button { wrapSel("**", "**") } label: { Label(L("fmtBold"), systemImage: "bold") }
            Button { wrapSel("*", "*") } label: { Label(L("fmtItalic"), systemImage: "italic") }
            Button { wrapSel("~~", "~~") } label: { Label(L("fmtStrike"), systemImage: "strikethrough") }
            Button { linkOp() } label: { Label(L("fmtLink"), systemImage: "link") }
            Button { prefixLines("> ") } label: { Label(L("fmtQuote"), systemImage: "text.quote") }
            Button { prefixLines("- ") } label: { Label(L("fmtBullets"), systemImage: "list.bullet") }
            Button { prefixLines("1. ") } label: { Label(L("fmtNumbers"), systemImage: "list.number") }
            Button { wrapSel("`", "`", placeholder: L("placeholderCode")) } label: {
                Label(L("fmtCode"), systemImage: "chevron.left.forwardslash.chevron.right")
            }
        }
        .help(L("formatting"))
    }

    // MARK: - Preview rendering (Timer debounce: no Sendable capture issues)

    private func scheduleRender() {
        renderTimer?.invalidate()
        guard let doc, doc.canPreview, doc.showPreview else { return }
        renderTimer = Timer.scheduledTimer(withTimeInterval: 0.4, repeats: false) { _ in render(doc) }
    }

    private func renderSelected() {
        if let doc, doc.canPreview, doc.showPreview { render(doc) }
    }

    private func render(_ doc: Doc) {
        if doc.isJSON {
            doc.renderedHTML = JsonTree.build(doc.text, dark: dark,
                fontSize: store.effectiveFontSize, title: doc.fileName, open: doc.treeOpen)
            return
        }
        let base = doc.path.map { URL(fileURLWithPath: $0).deletingLastPathComponent().path }
        doc.renderedHTML = MarkdownHTML.build(doc.text, baseDir: base, dark: dark,
            fontSize: store.effectiveFontSize, title: doc.fileName)
    }

    private func showPreview(_ doc: Doc, _ show: Bool) {
        guard doc.canPreview else { return }
        doc.showPreview = show
        if show { render(doc) }
    }

    // MARK: - Click-to-source: preview click -> editor caret on that line

    private func jumpToLine(_ line: Int) {
        guard let doc = store.selected, doc.canPreview, !doc.text.isEmpty else { return }
        showPreview(doc, false)
        doc.revealRange = NSRange(location: lineStartOffset(doc.text, line: line), length: 0)
    }

    /// UTF-16 offset of a 1-based line start, clamped. LineMap handles
    /// \n, \r\n and \r alike.
    private func lineStartOffset(_ text: String, line: Int) -> Int {
        guard line > 1 else { return 0 }
        let map = SyntaxHighlight.LineMap(text)
        if line - 1 < map.starts.count { return map.starts[line - 1] }
        return (text as NSString).length
    }

    // MARK: - Formatting (operates on the cached selection: toolbar clicks move focus)

    private func selRange(_ doc: Doc) -> NSRange {
        let len = (doc.text as NSString).length
        let s = min(max(doc.caret, 0), len)
        return NSRange(location: s, length: min(max(doc.selLen, 0), len - s))
    }

    private func editable() -> Doc? {
        guard let doc, !doc.showPreview else { return nil }
        return doc
    }

    private func wrapSel(_ before: String, _ after: String, placeholder: String? = nil, lineStart: Bool = false) {
        guard let doc = editable() else { return }
        let ns = doc.text as NSString
        if lineStart {
            var ls = selRange(doc).location
            while ls > 0, ns.character(at: ls - 1) != 10 { ls -= 1 }
            let r = NSRange(location: ls, length: 0)
            doc.pendingOp = .init(range: r, insert: before,
                select: NSRange(location: ls + (before as NSString).length, length: 0))
            return
        }
        let r = selRange(doc)
        let sel = r.length > 0 ? ns.substring(with: r) : (placeholder ?? L("placeholderText"))
        let ins = before + sel + after
        let newSel: NSRange? = r.length > 0 ? nil
            : NSRange(location: r.location + (before as NSString).length, length: (sel as NSString).length)
        doc.pendingOp = .init(range: r, insert: ins, select: newSel)
    }

    private func prefixLines(_ prefix: String) {
        guard let doc = editable() else { return }
        let ns = doc.text as NSString
        let r = selRange(doc)
        var ls = r.location
        while ls > 0, ns.character(at: ls - 1) != 10 { ls -= 1 }
        var le = r.location + r.length
        while le < ns.length, ns.character(at: le) != 10 { le += 1 }
        let chunk = ns.substring(with: NSRange(location: ls, length: le - ls))
        let lines = chunk.components(separatedBy: "\n").map { ln -> String in
            ln.hasPrefix(prefix) ? String(ln.dropFirst(prefix.count)) : prefix + ln
        }
        let joined = lines.joined(separator: "\n")
        doc.pendingOp = .init(range: NSRange(location: ls, length: le - ls), insert: joined,
            select: NSRange(location: ls, length: (joined as NSString).length))
    }

    private func linkOp() {
        guard let doc = editable() else { return }
        let ns = doc.text as NSString
        let r = selRange(doc)
        let sel = r.length > 0 ? ns.substring(with: r) : L("placeholderText")
        let ins = "[\(sel)](https://)"
        doc.pendingOp = .init(range: r, insert: ins,
            select: NSRange(location: r.location + 1, length: (sel as NSString).length))
    }

    // MARK: - Find

    private func find(up: Bool) {
        guard let doc, !findText.isEmpty else { return }
        if doc.showPreview { doc.showPreview = false }
        let ns = doc.text as NSString
        let r = selRange(doc)
        var opts: NSString.CompareOptions = .caseInsensitive
        if up { opts.insert(.backwards) }
        let range: NSRange
        if up {
            range = ns.range(of: findText, options: opts, range: NSRange(location: 0, length: r.location))
        } else {
            let from = r.location + r.length
            range = ns.range(of: findText, options: opts,
                range: NSRange(location: from, length: ns.length - from))
        }
        if range.location != NSNotFound {
            doc.revealRange = range
        } else {
            let wrap = ns.range(of: findText, options: opts)
            if wrap.location != NSNotFound { doc.revealRange = wrap }
        }
    }

    /// Caret line/column (1-based) via NSString line primitives: UTF-16
    /// consistent with NSTextView ranges, all line endings handled.
    private func lineCol(_ doc: Doc) -> (Int, Int) {
        let ns = doc.text as NSString
        let caret = min(max(doc.caret, 0), ns.length)
        var line = 1
        var search = 0
        while search < caret {
            var end = 0, contentsEnd = 0
            ns.getLineStart(nil, end: &end, contentsEnd: &contentsEnd,
                for: NSRange(location: search, length: 0))
            guard end > search else { break }
            // Past this line only when it has a terminator at/before caret.
            if end > contentsEnd, end <= caret { search = end; line += 1 } else { break }
        }
        return (line, caret - search + 1)
    }
}

// MARK: - Tab strip (Safari-style compact tabs)

private struct TabStrip: View {
    var activeID: UUID?
    var docs: [Doc]
    var select: (Doc) -> Void
    var close: (Doc) -> Void
    var add: () -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(docs) { doc in
                    tab(doc)
                }
                Button { add() } label: {
                    Image(systemName: "plus").font(.system(size: 13, weight: .medium))
                }
                .buttonStyle(.plain)
                .padding(6)
                .help(L("newTab"))
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private func tab(_ doc: Doc) -> some View {
        HStack(spacing: 4) {
            if doc.dirty { Circle().fill(.orange).frame(width: 7, height: 7) }
            Text(doc.fileName)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: 180)
            Button { close(doc) } label: {
                Image(systemName: "xmark").font(.system(size: 9, weight: .bold))
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background { tabBackground(doc) }
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .onTapGesture { select(doc) }
        .help(doc.path ?? doc.fileName)
    }

    @ViewBuilder
    private func tabBackground(_ doc: Doc) -> some View {
        if doc.id == activeID {
            Color(nsColor: .controlBackgroundColor).shadow(color: .black.opacity(0.12), radius: 1, y: 1)
        } else {
            Color.clear
        }
    }
}

// MARK: - Find bar

private struct FindBarView: View {
    @Binding var text: String
    var next: () -> Void
    var prev: () -> Void
    var close: () -> Void

    var body: some View {
        HStack(spacing: 4) {
            TextField(L("find"), text: $text)
                .textFieldStyle(.roundedBorder)
                .frame(width: 170)
                .onSubmit { next() }
            Button { prev() } label: { Image(systemName: "chevron.up") }
            Button { next() } label: { Image(systemName: "chevron.down") }
            Button { close() } label: { Image(systemName: "xmark") }
        }
        .buttonStyle(.plain)
        .padding(8)
        .background(.bar)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .shadow(radius: 3)
        .padding([.top, .trailing], 12)
    }
}
