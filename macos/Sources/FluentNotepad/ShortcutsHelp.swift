import SwiftUI

/// Help > Keyboard Shortcuts: static cheatsheet of the app's key bindings.
/// Labels go through L() (both languages); key glyphs are literal.
struct ShortcutsHelpView: View {
    private let rows: [(String, String)] = [
        ("newTab", "⌘T"),
        ("open", "⌘O"),
        ("newWindow", "⇧⌘N"),
        ("save", "⌘S"),
        ("saveAs", "⇧⌘S"),
        ("closeTab", "⌘W"),
        ("print", "⌘P"),
        ("find", "⌘F"),
        ("zoomIn", "⌘+"),
        ("zoomOut", "⌘-"),
        ("zoomReset", "⌘0"),
        ("togglePreview", "⇧⌘V"),
        ("toggleEsc", "Esc"),
        ("undo", "⌘Z"),
        ("cut", "⌘X"),
        ("copy", "⌘C"),
        ("paste", "⌘V"),
        ("selectAll", "⌘A"),
    ]

    var body: some View {
        ScrollView {
            VStack(spacing: 0) {
                ForEach(rows, id: \.0) { key, glyph in
                    HStack {
                        Text(L(key))
                        Spacer()
                        Text(glyph)
                            .foregroundStyle(.secondary)
                            .font(.system(.body, design: .monospaced))
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 5)
                    Divider()
                }
            }
            .padding(.vertical, 8)
        }
        .frame(minWidth: 360, minHeight: 400)
    }
}
