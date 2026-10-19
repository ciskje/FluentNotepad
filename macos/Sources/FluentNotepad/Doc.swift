import Foundation

/// One open document. Main-thread confined. Dirty = text differs from last
/// saved/loaded content (comparison, not a flag: re-attaching the text view
/// re-syncs text without marking untouched tabs as modified).
@Observable
final class Doc: Identifiable, @unchecked Sendable {
    let id = UUID()
    var text = ""
    var savedText = ""
    var path: String?
    var showPreview = false
    var caret = 0
    var selLen = 0
    var renderedHTML = ""
    var lineEnding = "Windows (CRLF)"
    var fileEncoding = "UTF-8"

    /// One-shot edit for the NSTextView wrapper (formatting toolbar, find).
    struct EditOp { var range: NSRange; var insert: String; var select: NSRange? }
    var pendingOp: EditOp?
    /// One-shot reveal for find matches (select + scroll + flash).
    var revealRange: NSRange?

    /// Per-tab tree-preview state (JSON): RFC 6901 pointers of open nodes +
    /// last search text (mirrors Windows' JsonOpen/JsonQuery).
    var treeOpen = Set<String>()
    var treeQuery = ""

    var dirty: Bool { text != savedText }

    var isMarkdown: Bool {
        // Unsaved tabs count as Markdown: preview toggle, highlighting and
        // .md save default work before the first save (real .txt keeps none).
        guard let p = path else { return true }
        let l = p.lowercased()
        return l.hasSuffix(".md") || l.hasSuffix(".markdown")
    }

    var isJSON: Bool {
        guard let p = path else { return false }
        let l = p.lowercased()
        return l.hasSuffix(".json") || l.hasSuffix(".jsonc")
    }

    var isXML: Bool {
        guard let p = path else { return false }
        let l = p.lowercased()
        return l.hasSuffix(".xml") || l.hasSuffix(".config")
    }

    var isYaml: Bool {
        guard let p = path else { return false }
        let l = p.lowercased()
        return l.hasSuffix(".yaml") || l.hasSuffix(".yml")
    }

    var isToml: Bool {
        guard let p = path else { return false }
        return p.lowercased().hasSuffix(".toml")
    }

    var isIni: Bool {
        guard let p = path else { return false }
        let l = p.lowercased()
        if l.hasSuffix(".ini") || l.hasSuffix(".cfg") || l.hasSuffix(".inf")
            || l.hasSuffix(".properties") { return true }
        // Extensionless dotfiles like .env / .env.local (checked by file name).
        let name = URL(fileURLWithPath: p).lastPathComponent.lowercased()
        return name == ".env" || name.hasPrefix(".env.")
    }

    var isCSV: Bool {
        guard let p = path else { return false }
        let l = p.lowercased()
        return l.hasSuffix(".csv") || l.hasSuffix(".tsv")
    }

    /// Tree previews share the collapsible schema (JSON pointer + $. paths);
    /// CSV has its own grid page. Markdown stands alone.
    var isTree: Bool { isJSON || isXML || isYaml || isToml || isIni }

    /// Anything with a graphical preview (Markdown + tree/grid previews).
    var canPreview: Bool { isMarkdown || isTree || isCSV }

    var fileName: String { path.map { URL(fileURLWithPath: $0).lastPathComponent } ?? L("untitled") }

    var displayName: String { (dirty ? "• " : "") + fileName }

    static func detectLineEnding(_ s: String) -> String {
        if s.contains("\r\n") { return "Windows (CRLF)" }
        if s.contains("\n") { return "Unix (LF)" }
        return "Windows (CRLF)"
    }

    static func detectEncoding(_ url: URL) -> String {
        guard let h = try? Data(contentsOf: url, options: [.mappedIfSafe]).prefix(3) else { return "UTF-8" }
        let b = Array(h)
        if b.count >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF { return L("encUtf8Bom") }
        if b.count >= 2 && b[0] == 0xFF && b[1] == 0xFE { return "UTF-16 LE" }
        if b.count >= 2 && b[0] == 0xFE && b[1] == 0xFF { return "UTF-16 BE" }
        return "UTF-8"
    }
}
