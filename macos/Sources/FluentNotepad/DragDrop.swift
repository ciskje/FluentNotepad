import AppKit

/// Shared file-drop extraction for the three AppKit drop sites
/// (CloseGuard, EditorTextView, PreviewWebView): one call-site each.
extension NSDraggingInfo {
    /// True when the drag carries file URLs.
    var isFileDrop: Bool {
        draggingPasteboard.types?.contains(.fileURL) == true
    }

    /// File URLs on the drag pasteboard.
    var fileURLs: [URL] {
        ((draggingPasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL]) ?? [])
            .filter(\.isFileURL)
    }
}
