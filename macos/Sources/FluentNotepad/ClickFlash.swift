import AppKit
import Foundation

// MARK: - Click-to-source + caret-line flash (Windows 2.10.4, macOS 2.10.6)

/// Click-to-source, shared by all previews: leave preview, caret on the
/// line, blink it twice in Edit. The target range is computed synchronously
/// from the text and flashed on the live view, so no stale caret is ever
/// blinked while the reveal is still being applied.
func jumpToLine(_ store: Store, _ line: Int) {
    guard let doc = store.selected, doc.canPreview, !doc.text.isEmpty else { return }
    let off = lineStartOffsetIn(doc.text, line: line)
    guard let (ls, len) = lineRangeIn(doc.text, caret: off) else { return }
    // Hide preview without flashing; the reveal places the caret on the
    // next render while the flash below drives the live view directly.
    doc.showPreview = false
    doc.revealRange = NSRange(location: off, length: 0)
    flashLine(store, NSRange(location: ls, length: len), preDelayMs: 500, blinkMs: 250_000_000, blinks: 1)
}

/// Jump straight to Edit and blink the caret line twice there (in place):
/// the jumped-to line is the one to look at, so the flash belongs to Edit,
/// not to the preview. Skips the jump if the user moved on meanwhile.
func jumpThenFlash(_ store: Store, _ line: Int) {
    guard let doc = store.selected, doc.canPreview, !doc.text.isEmpty else { return }
    if !doc.showPreview { return } // user moved on
    jumpToLine(store, line)
}

/// Caret-line flash when entering Edit: selection-only, so it never touches
/// the undo stack or text attributes. Any real edit, tab switch or
/// back-to-preview aborts it via flashGen. No-op unless the tab is in Edit.
/// `range` is nil for plain Edit entries (blink the live caret line twice);
/// the click path passes its known target range and blinks once.
///
/// Timing is anchored to applied changes: the pre-delay starts only after
/// validation, the editor view is awaited (bounded) before the first blink,
/// and every sleep brackets a synchronous view selection — never a render
/// round-trip — so on/off times are actually respected.
func flashLine(_ store: Store, _ range: NSRange?, preDelayMs: UInt64 = 0, blinkMs: UInt64 = 140_000_000, blinks: Int = 2) {
    store.flashGen += 1
    let gen = store.flashGen
    guard let doc = store.selected, !doc.showPreview, !doc.text.isEmpty else { return }
    Task { @MainActor [store] in
        if preDelayMs > 0 { try? await Task.sleep(nanoseconds: preDelayMs * 1_000_000) }
        guard gen == store.flashGen, let d = store.selected,
            d.id == doc.id, !d.showPreview
        else { return }
        // The editor view appears right after leaving preview; wait for it
        // (bounded) before the first blink so the timer starts on the view.
        var waited: UInt64 = 0
        while store.editorRef.view == nil, waited < 1_000_000_000 {
            try? await Task.sleep(nanoseconds: 50_000_000)
            waited += 50_000_000
            guard gen == store.flashGen, let dd = store.selected,
                dd.id == doc.id, !dd.showPreview
            else { return }
        }
        guard let live = store.editorRef.selection,
            let (ls, len) = range.map({ ($0.location, $0.length) })
                ?? lineRangeIn(d.text, caret: live.location)
        else { return }
        let line = NSRange(location: ls, length: len)
        guard let orig = store.editorRef.selection else { return }
        for _ in 0..<max(blinks, 1) {
            guard gen == store.flashGen, let dd = store.selected,
                dd.id == doc.id, !dd.showPreview
            else { return }
            store.editorRef.select(line, flash: true)
            try? await Task.sleep(nanoseconds: blinkMs)
            guard gen == store.flashGen, let dd2 = store.selected,
                dd2.id == doc.id, !dd2.showPreview,
                let cur = store.editorRef.selection,
                cur.location == line.location, cur.length == line.length
            else { return } // user moved on; keep their selection
            store.editorRef.select(orig, flash: false)
            try? await Task.sleep(nanoseconds: blinkMs)
        }
    }
}

/// Caret's line as (start, length) on \n text. Empty lines collapse to one
/// char (the break, or the previous one at EOF) so a flash stays visible.
/// Mirrors Windows' TextUtil.TryGetLineRange.
func lineRangeIn(_ text: String, caret: Int) -> (Int, Int)? {
    if text.isEmpty { return nil }
    let ns = text as NSString
    var ls = min(max(caret, 0), ns.length)
    while ls > 0, ns.character(at: ls - 1) != 10 { ls -= 1 }
    var le = min(max(caret, 0), ns.length)
    while le < ns.length, ns.character(at: le) != 10 { le += 1 }
    var len = le - ls
    if len <= 0 {
        if ls + 1 <= ns.length { len = 1 } else if ls > 0 { ls -= 1; len = 1 } else { return nil }
    }
    return (ls, len)
}

/// UTF-16 offset of a 1-based line start, clamped.
func lineStartOffsetIn(_ text: String, line: Int) -> Int {
    guard line > 1 else { return 0 }
    let map = SyntaxHighlight.LineMap(text)
    if line - 1 < map.starts.count { return map.starts[line - 1] }
    return (text as NSString).length
}
