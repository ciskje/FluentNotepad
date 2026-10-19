import AppKit
import Foundation
import UniformTypeIdentifiers

/// Single-window store (Notepad model: tabs live inside one window).
/// Main-thread confined.
@Observable
final class Store: @unchecked Sendable {
    static let shared = Store()

    var docs: [Doc] = []
    var selectedID: UUID?

    var selected: Doc? {
        get { docs.first { $0.id == selectedID } ?? docs.last }
        set { selectedID = newValue?.id }
    }

    var dirtyDocs: [Doc] { docs.filter(\.dirty) }

    // MARK: - Standard dialogs (NSAlert sheets, HIG button order)

    enum SaveChoice { case save, dontSave, cancel }

    /// Window to attach sheets to (the red-dot/quit paths pass their own).
    var keyWindow: NSWindow? {
        NSApp.keyWindow ?? NSApp.mainWindow ?? NSApp.windows.first(where: \.isVisible)
    }

    /// The standard macOS save-changes dialog: app icon, "Save … ?" wording,
    /// Don't Save | Cancel | Save(default). Sheet when a window is given.
    func confirmSaveSheet(_ doc: Doc, window: NSWindow?, completion: @escaping (SaveChoice) -> Void) {
        let a = NSAlert()
        a.icon = NSApp.applicationIconImage
        a.messageText = L("saveTitle", doc.fileName)
        a.informativeText = L("saveInfo")
        a.addButton(withTitle: L("save"))
        a.addButton(withTitle: L("cancel"))
        a.addButton(withTitle: L("dontSave"))
        let map: (NSApplication.ModalResponse) -> SaveChoice = { r in
            switch r {
            case .alertFirstButtonReturn: return .save
            case .alertThirdButtonReturn: return .dontSave
            default: return .cancel
            }
        }
        if let w = window {
            a.beginSheetModal(for: w) { r in completion(map(r)) }
        } else {
            completion(map(a.runModal()))
        }
    }

    // MARK: - Settings (UserDefaults = the native settings.json)

    // Stored (not computed): @Observable only notifies for stored properties.
    // A computed UserDefaults wrapper would save the value but never refresh the UI.
    var theme: String = UserDefaults.standard.string(forKey: "theme") ?? "system" {
        didSet { UserDefaults.standard.set(theme, forKey: "theme") }
    }

    var fontSize: Double = {
        let v = UserDefaults.standard.double(forKey: "fontSize"); return v > 0 ? v : 15
    }() {
        didSet { UserDefaults.standard.set(fontSize, forKey: "fontSize") }
    }

    var wrap: Bool = UserDefaults.standard.object(forKey: "wrap") as? Bool ?? true {
        didSet { UserDefaults.standard.set(wrap, forKey: "wrap") }
    }

    var zoom: Double = 1.0

    var effectiveFontSize: Double { fontSize * zoom }

    /// This window's live preview handle (print needs the WKWebView).
    let webRef = WebRef()

    // MARK: - Tabs

    @discardableResult
    func newTab(path: String? = nil, content: String = "") -> Doc {
        let d = Doc()
        d.path = path
        d.text = content
        d.savedText = content
        if let p = path {
            d.lineEnding = Doc.detectLineEnding(content)
            d.fileEncoding = Doc.detectEncoding(URL(fileURLWithPath: p))
            if d.canPreview { d.showPreview = true }
        }
        docs.append(d)
        selected = d
        return d
    }

    func open(url: URL) {
        let path = url.path
        // Already open: just select it.
        if let existing = docs.first(where: { $0.path == path }) {
            selected = existing
            return
        }
        guard let content = try? String(contentsOf: url) else { return }
        // Replace the pristine empty tab instead of leaving it behind.
        if let cur = selected, cur.path == nil, !cur.dirty, cur.text.isEmpty,
            let i = docs.firstIndex(where: { $0.id == cur.id })
        {
            docs.remove(at: i)
        }
        newTab(path: path, content: content)
    }

    func requestClose(_ doc: Doc) {
        guard doc.dirty else { close(doc); return }
        let win = WindowStores.window(for: self) ?? keyWindow
        confirmSaveSheet(doc, window: win) { [self] choice in
            switch choice {
            case .save: if save(doc) { close(doc) }
            case .dontSave: close(doc)
            case .cancel: break
            }
        }
    }

    func close(_ doc: Doc) {
        guard let i = docs.firstIndex(where: { $0.id == doc.id }) else { return }
        let wasSelected = doc.id == selected?.id
        docs.remove(at: i)
        if docs.isEmpty { newTab() } else if wasSelected {
            selected = docs[min(i, docs.count - 1)]
        }
    }

    // MARK: - Panels (native AppKit, no custom dialogs)

    /// Markdown extensions for the save panel (open accepts any plain text).
    static var mdTypes: [UTType] {
        ["md", "markdown"].compactMap { UTType(filenameExtension: $0) }
    }

    /// Open accepts plain text + JSON (.json is public.text, not plain-text,
    /// so it needs listing; .jsonc has no system UTI and is declared below).
    static var openTypes: [UTType] {
        var t: [UTType] = [.plainText, .json]
        if let j = UTType(filenameExtension: "jsonc", conformingTo: .text) { t.append(j) }
        return t
    }

    func openPanel() {
        let p = NSOpenPanel()
        p.allowsMultipleSelection = false
        p.allowedContentTypes = Self.openTypes
        if p.runModal() == .OK, let url = p.url { open(url: url) }
    }

    @discardableResult
    func save(_ doc: Doc) -> Bool {
        if doc.path == nil {
            let p = NSSavePanel()
            p.nameFieldStringValue = L("untitled") + (doc.isMarkdown ? ".md" : ".txt")
            p.allowedContentTypes = doc.isJSON ? [.json] : doc.isMarkdown ? Self.mdTypes : [UTType.plainText]
            guard p.runModal() == .OK, let url = p.url else { return false }
            doc.path = url.path
        }
        do {
            try doc.text.write(toFile: doc.path!, atomically: true, encoding: .utf8)
        } catch { return false }
        doc.savedText = doc.text
        doc.lineEnding = Doc.detectLineEnding(doc.text)
        doc.fileEncoding = "UTF-8"
        return true
    }

    func saveAs(_ doc: Doc) {
        let old = doc.path
        doc.path = nil
        if !save(doc) { doc.path = old }
    }
}

// MARK: - Multi-window plumbing (one Store per window, Notepad-style tabs)

/// Registry of live windows. Menu commands and open events always target the
/// frontmost window's store; with no windows they fall back to the shared one.
enum WindowStores {
    private final class Entry {
        let window: NSWindow
        let store: Store
        let guard_: CloseGuard
        init(_ w: NSWindow, _ s: Store, _ g: CloseGuard) { window = w; store = s; guard_ = g }
    }

    private static var entries: [ObjectIdentifier: Entry] = [:]

    static var isEmpty: Bool { entries.isEmpty }

    /// Registers a window: per-window close guard + file-drop support.
    /// Only the first window restores the saved frame; the rest cascade.
    static func register(_ w: NSWindow, _ store: Store) {
        let g = CloseGuard(store: store)
        w.delegate = g
        w.registerForDraggedTypes([.fileURL])
        if entries.isEmpty { w.setFrameAutosaveName("MainWindow") }
        entries[ObjectIdentifier(w)] = Entry(w, store, g)
    }

    static func unregister(_ w: NSWindow) {
        entries.removeValue(forKey: ObjectIdentifier(w))
    }

    static func store(for window: NSWindow?) -> Store? {
        window.flatMap { entries[ObjectIdentifier($0)]?.store }
    }

    static func isRegistered(_ window: NSWindow) -> Bool {
        entries[ObjectIdentifier(window)] != nil
    }

    static func frontmost() -> Store {
        store(for: NSApp.keyWindow ?? NSApp.mainWindow) ?? entries.values.first?.store ?? Store.shared
    }

    static func window(for store: Store) -> NSWindow? {
        entries.values.first { $0.store === store }?.window
    }

    static func allStores() -> [Store] {
        entries.values.map(\.store)
    }
}
