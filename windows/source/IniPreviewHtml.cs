using System.Text.Json;
using static FluentNotepad.PreviewTree;

namespace FluentNotepad;

/// <summary>
/// INI-like (.ini/.cfg/.inf/.properties/.env*) -&gt; collapsible tree HTML for
/// WebView2, on the shared PreviewTree schema: sections are objects, keys are
/// leaves (values kept verbatim, uninterpreted). Supports `=`/`:` separators,
/// `;`/`#` comments and `\` line continuations. Copy paths use the $.a.b form;
/// subtree copy rebuilds JSON.
/// </summary>
public static class IniPreviewHtml
{
    public static string Build(string text, bool dark, double fontSize, string? title, ISet<string>? open, bool wrap = true)
    {
        if (text.Length > MaxChars)
            return PageHead(dark, fontSize, title ?? "INI", "", wrap) + TooLarge(MainWindow.Str("IniTooLarge"));
        try
        {
            var root = Parse(text, out bool truncated);
            return TreePage(dark, fontSize, title ?? "INI",
                truncated ? MainWindow.Str("IniTruncated") : null,
                JsonSerializer.Serialize(root),
                JsonSerializer.Serialize(open ?? Enumerable.Empty<string>()),
                xml: false, wrap: wrap);
        }
        catch (PreviewTree.TreeParseException ex)
        {
            return ErrorPage(dark, fontSize, title ?? "INI",
                MainWindow.Str("IniInvalidTitle"),
                string.Format(MainWindow.Str("IniInvalid"), ex.Line, ex.Column),
                ex.Message, text, MainWindow.Str("IniErrorHint"), wrap);
        }
    }

    // MARK: - Parse (line-based, 1-based lines)

    private static PreviewTree.TreeNode Parse(string text, out bool truncated)
    {
        truncated = false;
        string norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(norm)) throw new PreviewTree.TreeParseException("The document is empty.", 1, 1);
        var lines = norm.Split('\n');
        var root = new PreviewTree.TreeNode { t = "o", l = 1, c = new List<PreviewTree.TreeNode>() };
        var cur = root;
        var pending = new List<string>();
        PreviewTree.TreeNode? last = null;
        int lastLine = 0;
        int count = 0;

        void Ate(PreviewTree.TreeNode n, int line) { last = n; lastLine = line; }
        void Comment(string raw, int line)
        {
            if (last != null && line == lastLine) { (last.e ??= new List<string>()).Add(raw); }
            else pending.Add(raw);
        }

        int i = 0;
        while (i < lines.Length)
        {
            string raw = lines[i];
            int line = i + 1;
            i++;
            // Continuation: trailing unescaped backslash joins the next line.
            while (EndsWithContinuation(raw) && i < lines.Length)
            {
                raw = raw[..^1] + lines[i].TrimStart();
                line = i + 1;
                i++;
            }
            string t = raw.Trim();
            if (t.Length == 0) continue;
            char f = t[0];
            if (f == ';' || f == '#') { Comment(t, line); continue; }
            if (f == '[')
            {
                int end = t.IndexOf(']');
                if (end < 0) throw new PreviewTree.TreeParseException("Unclosed section header.", line, 1);
                string rest = t[(end + 1)..].Trim();
                if (rest.Length > 0 && rest[0] != ';' && rest[0] != '#')
                    throw new PreviewTree.TreeParseException("Unexpected content after section header.", line, 1);
                string name = t[1..end].Trim();
                if (name.Length == 0) throw new PreviewTree.TreeParseException("Empty section name.", line, 1);
                // Duplicate sections merge (union of keys).
                PreviewTree.TreeNode? sec = null;
                foreach (var c in root.c!) if (c.k == name && c.t == "o") { sec = c; break; }
                if (sec == null)
                {
                    sec = new PreviewTree.TreeNode { t = "o", k = name, l = line, c = new List<PreviewTree.TreeNode>() };
                    if (pending.Count > 0) { sec.m = new List<string>(pending); pending.Clear(); }
                    root.c.Add(sec);
                }
                cur = sec;
                Ate(sec, line);
                if (rest.Length > 0) Comment(rest, line);
                continue;
            }
            // key = value (first unquoted = or :)
            int sep = -1;
            bool inS = false, inD = false;
            for (int k = 0; k < t.Length; k++)
            {
                char c = t[k];
                if (inS) { if (c == '\'') inS = false; continue; }
                if (inD) { if (c == '\\') k++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '=' || c == ':') { sep = k; break; }
            }
            if (sep < 0) throw new PreviewTree.TreeParseException("Expected '=' or ':'.", line, 1);
            string key = t[..sep].Trim();
            if (key.Length == 0) throw new PreviewTree.TreeParseException("Expected a key.", line, 1);
            var (val, trail) = SplitInlineComment(t[(sep + 1)..].Trim());
            var node = new PreviewTree.TreeNode { t = "s", k = key, v = val, l = line };
            if (pending.Count > 0) { node.m = new List<string>(pending); pending.Clear(); }
            if (trail != null) (node.e ??= new List<string>()).Add(trail);
            cur.c ??= new List<PreviewTree.TreeNode>();
            cur.c.Add(node);
            Ate(node, line);
            if (++count > MaxNodes)
            {
                truncated = true;
                break;
            }
        }
        if (pending.Count > 0) { (root.e ??= new List<string>()).AddRange(pending); pending.Clear(); }
        return root;

        static bool EndsWithContinuation(string s)
        {
            int backslashes = 0;
            for (int k = s.Length - 1; k >= 0 && s[k] == '\\'; k--) backslashes++;
            return backslashes % 2 == 1;
        }

        static (string Value, string? Comment) SplitInlineComment(string s)
        {
            bool inS = false, inD = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') inS = false; continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if ((c == ';' || c == '#') && i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t'))
                    return (s[..(i - 1)].TrimEnd(), s[i..].Trim());
            }
            return (s, null);
        }

    }
}