import AppKit
import SwiftUI

/// Window-close guard: the standard save-changes sheet when the red dot is
/// clicked with dirty tabs (NSWindowDelegate has no SwiftUI equivalent).
/// One doc per attempt: answering re-triggers close for the next dirty doc.
/// Also the file-drop target: drops anywhere in the window (including over the
/// WKWebView preview, which swallows them before SwiftUI's onDrop sees them).
final class CloseGuard: NSObject, NSWindowDelegate {
    let store: Store
    init(store: Store) { self.store = store }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard let doc = store.dirtyDocs.first else { return true }
        store.confirmSaveSheet(doc, window: sender) { [self] choice in
            switch choice {
            case .save: if store.save(doc) { sender.close() }
            case .dontSave: store.close(doc); sender.close()
            case .cancel: break
            }
        }
        return false
    }

    func windowWillClose(_ notification: Notification) {
        if let w = notification.object as? NSWindow { WindowStores.unregister(w) }
    }

    func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { .copy }

    func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL] ?? []
        let files = urls.filter(\.isFileURL)
        guard !files.isEmpty else { return false }
        for url in files { store.open(url: url) }
        return true
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Command-line file open: FluentNotepad notes.md. Queued: the first
        // window to appear (or the CLI fallback below) consumes them.
        for arg in CommandLine.arguments.dropFirst() where !arg.hasPrefix("-") {
            if FileManager.default.fileExists(atPath: arg) {
                PendingFiles.urls.append(URL(fileURLWithPath: arg))
            }
        }
        // Raw-CLI launch with files: SwiftUI withholds the default window for
        // open-document launches, so show one ourselves if none appears.
        if !PendingFiles.urls.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                if !NSApp.windows.contains(where: \.isVisible) { self?.showFallbackWindow() }
            }
        }
    }

    /// Queued files go to the frontmost window (no-op while none exists).
    static func deliverPending() {
        guard !PendingFiles.urls.isEmpty, !WindowStores.isEmpty else { return }
        let s = WindowStores.frontmost()
        for url in PendingFiles.urls { s.open(url: url) }
        PendingFiles.urls.removeAll()
    }

    func showFallbackWindow() {
        let store = Store()
        for url in PendingFiles.urls { store.open(url: url) }
        PendingFiles.urls.removeAll()
        if store.docs.isEmpty { store.newTab() }
        let win = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1000, height: 650),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered, defer: false)
        win.title = "FluentNotepad"
        win.contentViewController = NSHostingController(rootView: ContentView(store: store))
        // NSHostingController content has no intrinsic size: enforce a sane
        // size, ignoring (and repairing) a previously-saved collapsed frame.
        if win.frame.width < 560 || win.frame.height < 360 {
            win.setContentSize(NSSize(width: 1000, height: 650))
            win.center()
        }
        WindowStores.register(win, store)
        _ = installEscMonitor(for: win, store: store)
        win.makeKeyAndOrderFront(nil)
    }

    func application(_ sender: NSApplication, openFile filename: String) -> Bool {
        PendingFiles.urls.append(URL(fileURLWithPath: filename))
        Self.deliverPending()
        return true
    }

    /// Quit/Reboot with dirty tabs: standard sheets, one doc at a time,
    /// across all windows.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        quitQueue = WindowStores.allStores().flatMap { s in s.dirtyDocs.map { (s, $0) } }
        guard !quitQueue.isEmpty else { return .terminateNow }
        reviewNextQuitDoc()
        return .terminateLater
    }

    private var quitQueue: [(Store, Doc)] = []

    private func reviewNextQuitDoc() {
        guard let (store, doc) = quitQueue.first else {
            NSApp.reply(toApplicationShouldTerminate: true)
            return
        }
        quitQueue.removeFirst()
        // Already closed via another sheet meanwhile: skip.
        guard store.docs.contains(where: { $0.id == doc.id }) else {
            reviewNextQuitDoc()
            return
        }
        store.confirmSaveSheet(doc, window: WindowStores.window(for: store)) { choice in
            switch choice {
            case .save:
                if store.save(doc) { self.reviewNextQuitDoc() }
                else { NSApp.reply(toApplicationShouldTerminate: false) }
            case .dontSave: store.close(doc); self.reviewNextQuitDoc()
            case .cancel: NSApp.reply(toApplicationShouldTerminate: false)
            }
        }
    }
}

/// One window = one tab set. Each window owns its Store; menus and open
/// events route to the frontmost window via WindowStores.
/// ESC toggles Edit/Preview no matter which view has focus (the editor and
/// the web view would otherwise each eat it). Yields when a text field
/// (find bar) or an IME composition owns the keystroke. One per window;
/// only the key window's acts.
func installEscMonitor(for w: NSWindow, store: Store) -> Any {
    NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak w, store] event in
        guard let w, w.isKeyWindow,
            event.keyCode == 53,
            event.modifierFlags.intersection(.deviceIndependentFlagsMask).isEmpty
        else { return event }
        if let fr = w.firstResponder {
            if let tv = fr as? NSTextView, tv.hasMarkedText() { return event }
            if fr is NSTextField { return event }
        }
        guard let doc = store.selected, doc.isMarkdown else { return event }
        NotificationCenter.default.post(name: .escapeToggle, object: nil)
        return nil
    }
}

struct WindowRoot: View {
    @State private var store = Store()
    @Environment(\.colorScheme) private var scheme
    /// ESC monitor token (removed with the window).
    @State private var escMonitor: Any?

    var body: some View {
        ContentView(store: store)
            .frame(minWidth: 560, minHeight: 360)
            .onAppear {
                if store.docs.isEmpty { store.newTab() }
                // Our window: visible and not yet registered (one appears at a time).
                // Native geometry memory (like the Windows app's settings.json).
                if let w = NSApp.windows.first(where: { $0.isVisible && !WindowStores.isRegistered($0) }) {
                    WindowStores.register(w, store)
                    escMonitor = installEscMonitor(for: w, store: store)
                }
                AppDelegate.deliverPending()
            }
            .onDisappear {
                if let m = escMonitor { NSEvent.removeMonitor(m); escMonitor = nil }
            }
            .preferredColorScheme(store.theme == "light" ? .light : store.theme == "dark" ? .dark : nil)
    }
}

/// File > New Window (the automatic item is unreliable once .newItem is
/// replaced, so this explicit one uses the scene's openWindow).
struct NewWindowButton: View {
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Button(L("newWindow")) { openWindow(id: "main") }
            .keyboardShortcut("n", modifiers: [.command, .shift])
    }
}

@main
struct FluentNotepadApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        WindowGroup("FluentNotepad", id: "main") {
            WindowRoot()
        }
        .windowToolbarStyle(.unified)
        .defaultSize(width: 1000, height: 650)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button(L("newTab")) { _ = WindowStores.frontmost().newTab() }.keyboardShortcut("t", modifiers: .command)
                Button(L("open")) { WindowStores.frontmost().openPanel() }.keyboardShortcut("o", modifiers: .command)
                Divider()
                NewWindowButton()
            }
            CommandGroup(after: .saveItem) {
                Button(L("saveAs")) {
                    if let d = WindowStores.frontmost().selected { WindowStores.frontmost().saveAs(d) }
                }.keyboardShortcut("s", modifiers: [.command, .shift])
                Button(L("closeTab")) {
                    if let d = WindowStores.frontmost().selected { WindowStores.frontmost().requestClose(d) }
                }.keyboardShortcut("w", modifiers: .command)
                Divider()
                Button(L("print")) {
                    let s = WindowStores.frontmost()
                    if let d = s.selected, d.isMarkdown {
                        if !d.showPreview { d.showPreview = true }
                        s.webRef.setTitle(d.fileName)
                        s.webRef.print()
                    }
                }.keyboardShortcut("p", modifiers: .command)
            }
            CommandGroup(after: .textEditing) {
                Button(L("find")) {
                    NotificationCenter.default.post(name: .toggleFind, object: nil)
                }.keyboardShortcut("f", modifiers: .command)
            }
            CommandGroup(after: .toolbar) {
                Menu(L("view")) {
                    Button(L("zoomIn")) { let s = WindowStores.frontmost(); s.zoom = min(s.zoom + 0.1, 3.0) }
                        .keyboardShortcut("+", modifiers: .command)
                    Button(L("zoomOut")) { let s = WindowStores.frontmost(); s.zoom = max(s.zoom - 0.1, 0.5) }
                        .keyboardShortcut("-", modifiers: .command)
                    Button(L("zoomReset")) { WindowStores.frontmost().zoom = 1.0 }
                        .keyboardShortcut("0", modifiers: .command)
                    Divider()
                    Button(L("togglePreview")) {
                        if let d = WindowStores.frontmost().selected, d.isMarkdown { d.showPreview.toggle() }
                    }.keyboardShortcut("v", modifiers: [.command, .shift])
                    Toggle(L("wordWrap"), isOn: Binding(
                        get: { WindowStores.frontmost().wrap }, set: { WindowStores.frontmost().wrap = $0 }))
                }
            }
        }
        Settings { SettingsView(store: WindowStores.frontmost()) }
    }
}

extension Notification.Name {
    static let toggleFind = Notification.Name("toggleFind")
    static let srcLineTapped = Notification.Name("srcLineTapped")
    static let escapeToggle = Notification.Name("escapeToggle")
}
