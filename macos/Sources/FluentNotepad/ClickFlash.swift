import AppKit
import Foundation

// MARK: - Click-to-source + caret-line flash (Windows 2.10.4)

/// Click-to-source, shared by all previews: leave preview, caret on the
/// line, blink it twice in Edit. The caret is placed BEFORE flashing:
/// flashCaretLine blinks whatever line the caret is on, and aborts if the
/// selection moves mid-blink.
func jumpToLine(_ store: Store, _ line: Int) {
    guard let doc = store.selected, doc.canPreview, !doc.text.isEmpty else { return }
    // Hide preview without flashing: the flash below must run on the
    // just-placed caret, otherwise it blinks the old line (or aborts).
    doc.showPreview = false
    doc.revealRange = NSRange(location: lineStartOffsetIn(doc.text, line: line), length: 0)
    flashCaretLine(store, preDelayMs: 500, blinkMs: 250)
}

/// Jump straight to Edit and blink the caret line twice there (in place):
/// the jumped-to line is the one to look at, so the flash belongs to Edit,
/// not to the preview. Skips the jump if the user moved on meanwhile.
func jumpThenFlash(_ store: Store, _ line: Int) {
    guard let doc = store.selected, doc.canPreview, !doc.text.isEmpty else { return }
    if !doc.showPreview { return } // user moved on
    jumpToLine(store, line)
}

/// Caret-line flash (two blinks) when entering Edit: selection-only, so it
/// never touches the undo stack or text attributes. Any real edit, tab
/// switch or back-to-preview aborts it via flashGen. No-op unless the tab is
/// in Edit. Optional pre-delay + blink length: the click path waits 0.5 s,
/// then blinks twice at 250 ms so the target line is easy to catch.
func flashCaretLine(_ store: Store, preDelayMs: UInt64 = 0, blinkMs: UInt64 = 140_000_000) {
    store.flashGen += 1
    let gen = store.flashGen
    guard let doc = store.selected, !doc.showPreview, !doc.text.isEmpty else { return }
    Task { @MainActor [store] in
        if preDelayMs > 0 { try? await Task.sleep(nanoseconds: preDelayMs * 1_000_000) }
        // The user may have moved on during the wait: revalidate everything
        // and blink whatever line the caret is on now.
        guard gen == store.flashGen, let d = store.selected,
            d.id == doc.id, !d.showPreview,
            let (ls, len) = lineRangeIn(d.text, caret: d.caret)
        else { return }
        let orig = NSRange(location: d.caret, length: d.selLen)
        for _ in 0..<2 {
            try? await Task.sleep(nanoseconds: blinkMs)
            guard gen == store.flashGen, let dd = store.selected,
                dd.id == doc.id, !dd.showPreview
            else { return }
            dd.revealRange = NSRange(location: ls, length: len)
            try? await Task.sleep(nanoseconds: blinkMs)
            guard gen == store.flashGen, let dd2 = store.selected,
                dd2.id == doc.id, !dd2.showPreview
            else { return }
            if dd2.caret != ls || dd2.selLen != len { return } // user moved on; keep their selection
            dd2.revealRange = orig
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
