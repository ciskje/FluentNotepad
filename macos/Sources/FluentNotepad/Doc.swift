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

    /// Preview/highlight family, detected once from the file name.
    enum Kind: Equatable { case text, markdown, json, xml, yaml, toml, ini, csv }

    // One table drives extension detection for every predicate. Markdown first:
    // it also wins for .md. Extensionless dotfiles (.env, .env.local) are INI.
    private static let kindExts: [(Kind, [String])] = [
        (.markdown, [".md", ".markdown"]),
        (.json, [".json", ".jsonc"]),
        (.xml, [".xml", ".config"]),
        (.yaml, [".yaml", ".yml"]),
        (.toml, [".toml"]),
        (.ini, [".ini", ".cfg", ".inf", ".properties"]),
        (.csv, [".csv", ".tsv"]),
    ]

    var kind: Kind {
        // Unsaved tabs count as Markdown: preview toggle, highlighting and
        // .md save default work before the first save (real .txt keeps none).
        guard let p = path else { return .markdown }
        let name = URL(fileURLWithPath: p).lastPathComponent.lowercased()
        for (k, exts) in Doc.kindExts where exts.contains(where: { name.hasSuffix($0) }) {
            return k
        }
        return name == ".env" || name.hasPrefix(".env.") ? .ini : .text
    }

    var isMarkdown: Bool { kind == .markdown }
    var isJSON: Bool { kind == .json }
    var isXML: Bool { kind == .xml }
    var isYaml: Bool { kind == .yaml }
    var isToml: Bool { kind == .toml }
    var isIni: Bool { kind == .ini }
    var isCSV: Bool { kind == .csv }

    /// Tree previews share the collapsible schema (JSON pointer + $. paths);
    /// CSV has its own grid page. Markdown stands alone.
    var isTree: Bool { kind == .json || kind == .xml || kind == .yaml || kind == .toml || kind == .ini }

    /// Anything with a graphical preview (Markdown + tree/grid previews).
    var canPreview: Bool { kind != .text }

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
