using System.Text;
using System.Text.Json;
using static FluentNotepad.PreviewTree;
using System.Text.RegularExpressions;

namespace FluentNotepad;

/// <summary>
/// TOML (.toml) -&gt; collapsible tree HTML for WebView2, on the shared PreviewTree
/// schema: tables are objects, arrays of tables and arrays are arrays, dotted
/// keys are expanded (a.b.c = 1 nests). Values keep their raw literal.
/// Copy paths use the $.a.b[2] form; subtree copy rebuilds JSON.
/// </summary>
public static class TomlPreviewHtml
{
    public static string Build(string text, bool dark, double fontSize, string? title, ISet<string>? open, bool wrap = true)
    {
        if (text.Length > MaxChars)
            return PageHead(dark, fontSize, title ?? "TOML", "", wrap) + TooLarge(MainWindow.Str("TomlTooLarge"));
        try
        {
            var root = Parse(text, out bool truncated);
            return TreePage(dark, fontSize, title ?? "TOML",
                truncated ? MainWindow.Str("TomlTruncated") : null,
                JsonSerializer.Serialize(root),
                JsonSerializer.Serialize(open ?? Enumerable.Empty<string>()),
                xml: false, wrap: wrap);
        }
        catch (PreviewTree.TreeParseException ex)
        {
            return ErrorPage(dark, fontSize, new ErrorInfo
            {
                Title = title,
                Format = "TOML",
                Heading = MainWindow.Str("TomlInvalidTitle"),
                InvalidLine = string.Format(MainWindow.Str("TomlInvalid"), ex.Line, ex.Column),
                Message = ex.Message,
                Hint = MainWindow.Str("TomlErrorHint"),
                Source = text,
                Line = ex.Line,
                Column = ex.Column,
            }, wrap);
        }
    }

    // MARK: - Parse (cursor parser, positions are 1-based like the editor)

    private static PreviewTree.TreeNode Parse(string text, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(text)) throw new PreviewTree.TreeParseException("The TOML document is empty.", 1, 1);
        var w = new Walker(text);
        var root = new PreviewTree.TreeNode { t = "o", l = 1, c = new List<PreviewTree.TreeNode>() };
        var cur = root;
        w.SkipBlank();
        while (!w.Eof)
        {
            if (w.Peek() == '[')
            {
                cur = w.ReadTableHeader(root);
                w.ExpectEol();
            }
            else
            {
                var (keys, val) = w.ReadKeyValue();
                w.Assign(cur, keys, val);
                w.ExpectEol();
            }
            w.SkipBlank();
        }
        if (w.pending.Count > 0) { (root.e ??= new List<string>()).AddRange(w.TakePending()!); }
        truncated = w.trunc;
        return root;
    }

    private sealed class Walker
    {
        public readonly string text;
        public readonly List<int> starts = new() { 0 };
        public int pos;
        public int count;
        public bool trunc;
        public readonly List<string> pending = new();
        public PreviewTree.TreeNode? last;
        public int lastLine;
        public readonly HashSet<string> definedTables = new();

        private static readonly Regex DateRe = new(
            @"^\d{4}-\d{2}-\d{2}([Tt ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?([Zz]|([+-]\d{2}:\d{2}))?)?|^\d{2}:\d{2}(:\d{2}(\.\d+)?)?",
            RegexOptions.Compiled);

        public Walker(string t)
        {
            text = t;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
        }

        public bool Eof => pos >= text.Length;
        public char Peek() => Eof ? '\0' : text[pos];

        public int LineOf(int p)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= p) lo = mid;
                else hi = mid - 1;
            }
            return lo + 1;
        }

        public int ColOf(int p) => p - starts[LineOf(p) - 1] + 1;

        public PreviewTree.TreeParseException Fail(string msg) =>
            new(msg, LineOf(pos), ColOf(pos));

        public List<string>? TakePending()
        {
            if (pending.Count == 0) return null;
            var l = new List<string>(pending);
            pending.Clear();
            return l;
        }

        public void Ate(PreviewTree.TreeNode n) { last = n; lastLine = LineOf(pos); }

        public void AteComment(string raw)
        {
            // Same-line comments trail the previous value, else lead the next.
            if (last != null && LineOf(pos) == lastLine) { (last.e ??= new List<string>()).Add(raw); }
            else pending.Add(raw);
        }

        public void SkipInline()
        {
            while (!Eof && (text[pos] == ' ' || text[pos] == '\t')) pos++;
        }

        // Blank lines and full-line comments (attached as remarks).
        public void SkipBlank()
        {
            while (!Eof)
            {
                SkipInline();
                if (Eof) return;
                char c = text[pos];
                if (c == '\n') { pos++; continue; }
                if (c == '\r') { pos++; if (!Eof && text[pos] == '\n') pos++; continue; }
                if (c == '#')
                {
                    int s = pos;
                    while (!Eof && text[pos] != '\n') pos++;
                    AteComment(text[s..pos].TrimEnd('\r'));
                    continue;
                }
                return;
            }
        }

        // Whitespace + comments between tokens (arrays, inline tables).
        public void SkipGap()
        {
            while (!Eof)
            {
                SkipInline();
                if (Eof) return;
                char c = text[pos];
                if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && pos + 1 < text.Length && text[pos + 1] == '\n') pos += 2;
                    else pos++;
                    continue;
                }
                if (c == '#')
                {
                    int s = pos;
                    while (!Eof && text[pos] != '\n') pos++;
                    AteComment(text[s..pos].TrimEnd('\r'));
                    continue;
                }
                return;
            }
        }

        public void ExpectEol()
        {
            SkipInline();
            if (Eof) return;
            char c = text[pos];
            if (c == '\n') { pos++; return; }
            if (c == '\r')
            {
                pos++;
                if (!Eof && text[pos] == '\n') pos++;
                return;
            }
            if (c == '#')
            {
                int s = pos;
                while (!Eof && text[pos] != '\n') pos++;
                AteComment(text[s..pos].TrimEnd('\r'));
                ExpectEol();
                return;
            }
            throw Fail("Expected a line break.");
        }

        // ---- keys ----

        public string ReadKeyPart()
        {
            SkipInline();
            if (Eof) throw Fail("Expected a key.");
            char c = text[pos];
            if (c == '"') { var (v, _) = ReadBasicString(multiline: false); return v; }
            if (c == '\'') { var (v, _) = ReadLiteralString(multiline: false); return v; }
            int s = pos;
            while (!Eof && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '-')) pos++;
            if (pos == s) throw Fail("Expected a key.");
            return text[s..pos];
        }

        public List<(string Key, int Line)> ReadDottedKey()
        {
            var parts = new List<(string, int)>();
            while (true)
            {
                int l = LineOf(pos);
                parts.Add((ReadKeyPart(), l));
                SkipInline();
                if (!Eof && text[pos] == '.') { pos++; continue; }
                return parts;
            }
        }

        // ---- strings ----

        private (string Value, int Line) ReadBasicString(bool multiline)
        {
            int line = LineOf(pos);
            pos++; // opening "
            var sb = new StringBuilder();
            while (true)
            {
                if (Eof) throw Fail("Unterminated string.");
                char c = text[pos];
                if (c == '"') { pos++; return (sb.ToString(), line); }
                if (c == '\n') throw Fail("Unterminated string.");
                if (c == '\\')
                {
                    pos++;
                    if (Eof) throw Fail("Unterminated escape.");
                    char e = text[pos];
                    sb.Append(e switch
                    {
                        'b' => "\b",
                        't' => "\t",
                        'n' => "\n",
                        'f' => "\f",
                        'r' => "\r",
                        '"' => "\"",
                        '\\' => "\\",
                        _ => throw Fail($"Invalid escape '\\{e}'."),
                    });
                    pos++;
                    continue;
                }
                if (c < 0x20 && c != '\t') throw Fail("Control character in string.");
                sb.Append(c);
                pos++;
            }
        }

        private (string Value, int Line) ReadMultilineBasic()
        {
            int line = LineOf(pos);
            pos += 3; // """
            if (!Eof && text[pos] == '\n') pos++; // first newline trimmed
            else if (!Eof && text[pos] == '\r')
            {
                pos++;
                if (!Eof && text[pos] == '\n') pos++;
            }
            var sb = new StringBuilder();
            while (true)
            {
                if (Eof) throw Fail("Unterminated multi-line string.");
                if (text[pos] == '"' && pos + 2 < text.Length && text[pos + 1] == '"' && text[pos + 2] == '"')
                {
                    pos += 3;
                    return (sb.ToString(), line);
                }
                // One or two quotes are content; three close.
                if (text[pos] == '"' && pos + 1 < text.Length && text[pos + 1] == '"'
                    && (pos + 2 >= text.Length || text[pos + 2] != '"'))
                {
                    sb.Append("\"\"");
                    pos += 2;
                    continue;
                }
                if (text[pos] == '"' && (pos + 1 >= text.Length || text[pos + 1] != '"'))
                {
                    sb.Append('"');
                    pos++;
                    continue;
                }
                char c = text[pos];
                if (c == '\\')
                {
                    // Line-ending backslash trims the break + leading whitespace.
                    int q = pos + 1;
                    while (q < text.Length && (text[q] == ' ' || text[q] == '\t')) q++;
                    if (q < text.Length && (text[q] == '\n' || text[q] == '\r'))
                    {
                        if (text[q] == '\r' && q + 1 < text.Length && text[q + 1] == '\n') q++;
                        q++;
                        while (q < text.Length && (text[q] == ' ' || text[q] == '\t' || text[q] == '\n' || text[q] == '\r')) q++;
                        pos = q;
                        continue;
                    }
                    pos++;
                    if (Eof) throw Fail("Unterminated escape.");
                    char e = text[pos];
                    sb.Append(e switch
                    {
                        'b' => "\b",
                        't' => "\t",
                        'n' => "\n",
                        'f' => "\f",
                        'r' => "\r",
                        '"' => "\"",
                        '\\' => "\\",
                        'u' => ReadHex(4),
                        'U' => ReadHex(8),
                        _ => throw Fail($"Invalid escape '\\{e}'."),
                    });
                    pos++;
                    continue;
                }
                sb.Append(c);
                pos++;
            }
        }

        private string ReadHex(int digits)
        {
            if (pos + digits > text.Length) throw Fail("Bad unicode escape.");
            string hex = text.Substring(pos, digits);
            foreach (char h in hex)
                if (!Uri.IsHexDigit(h)) throw Fail("Bad unicode escape.");
            pos += digits - 1; // caller advances past the last digit
            int cp = Convert.ToInt32(hex, 16);
            if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) throw Fail("Bad unicode escape.");
            return char.ConvertFromUtf32(cp);
        }

        private (string Value, int Line) ReadLiteralString(bool multiline)
        {
            int line = LineOf(pos);
            if (multiline)
            {
                pos += 3; // '''
                if (!Eof && text[pos] == '\n') pos++;
                else if (!Eof && text[pos] == '\r')
                {
                    pos++;
                    if (!Eof && text[pos] == '\n') pos++;
                }
                int s = pos;
                while (true)
                {
                    if (Eof) throw Fail("Unterminated multi-line literal.");
                    if (text[pos] == '\'' && pos + 2 < text.Length && text[pos + 1] == '\'' && text[pos + 2] == '\'')
                    {
                        string v = text[s..pos];
                        pos += 3;
                        return (v, line);
                    }
                    pos++;
                }
            }
            pos++; // '
            int st = pos;
            while (true)
            {
                if (Eof) throw Fail("Unterminated literal.");
                if (text[pos] == '\'') { string v = text[st..pos]; pos++; return (v, line); }
                if (text[pos] == '\n') throw Fail("Unterminated literal.");
                pos++;
            }
        }

        // ---- values ----

        public PreviewTree.TreeNode ReadValue(int depth)
        {
            count++;
            if (count > MaxNodes || depth > MaxDepth)
            {
                trunc = true;
                pending.Clear();
                last = null;
                int l = LineOf(pos);
                SkipValue();
                return new PreviewTree.TreeNode { t = "s", v = "…", l = l };
            }
            SkipInline();
            if (Eof) throw Fail("Expected a value.");
            int line = LineOf(pos);
            char c = text[pos];
            if (c == '"')
            {
                bool ml = pos + 2 < text.Length && text[pos + 1] == '"' && text[pos + 2] == '"';
                var (v, _) = ml ? ReadMultilineBasic() : ReadBasicString(multiline: false);
                return new PreviewTree.TreeNode { t = "s", v = v, l = line, m = TakePending() };
            }
            if (c == '\'')
            {
                bool ml = pos + 2 < text.Length && text[pos + 1] == '\'' && text[pos + 2] == '\'';
                var (v, _) = ReadLiteralString(ml);
                return new PreviewTree.TreeNode { t = "s", v = v, l = line, m = TakePending() };
            }
            if (c == '[') return ReadArray(depth);
            if (c == '{') return ReadInlineTable(depth);
            if (c == 't' && MatchWord("true")) return new PreviewTree.TreeNode { t = "b", v = "true", l = line, m = TakePending() };
            if (c == 'f' && MatchWord("false")) return new PreviewTree.TreeNode { t = "b", v = "false", l = line, m = TakePending() };
            string rest = text[pos..Math.Min(text.Length, pos + 40)];
            var dm = DateRe.Match(rest);
            if (dm.Success && dm.Value.Length > 0)
            {
                pos += dm.Value.Length;
                return new PreviewTree.TreeNode { t = "s", v = dm.Value, l = line, m = TakePending() };
            }
            string num = ReadNumber();
            return new PreviewTree.TreeNode { t = "n", v = num, l = line, m = TakePending() };
        }

        private bool MatchWord(string word)
        {
            if (pos + word.Length > text.Length) return false;
            for (int k = 0; k < word.Length; k++)
                if (text[pos + k] != word[k]) return false;
            int after = pos + word.Length;
            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_' || text[after] == '-')) return false;
            pos += word.Length;
            return true;
        }

        private string ReadNumber()
        {
            int s = pos;
            if (!Eof && (text[pos] == '+' || text[pos] == '-')) pos++;
            if (pos + 1 < text.Length && text[pos] == '0' && (text[pos + 1] == 'x' || text[pos + 1] == 'X'))
            {
                pos += 2;
                int d = pos;
                while (!Eof && (Uri.IsHexDigit(text[pos]) || text[pos] == '_')) pos++;
                if (pos == d) throw Fail("Bad number.");
                return text[s..pos];
            }
            if (pos + 1 < text.Length && text[pos] == '0' && (text[pos + 1] == 'o' || text[pos + 1] == 'O'))
            {
                pos += 2;
                int d = pos;
                while (!Eof && ((text[pos] >= '0' && text[pos] <= '7') || text[pos] == '_')) pos++;
                if (pos == d) throw Fail("Bad number.");
                return text[s..pos];
            }
            if (pos + 1 < text.Length && text[pos] == '0' && (text[pos + 1] == 'b' || text[pos + 1] == 'B'))
            {
                pos += 2;
                int d = pos;
                while (!Eof && (text[pos] == '0' || text[pos] == '1' || text[pos] == '_')) pos++;
                if (pos == d) throw Fail("Bad number.");
                return text[s..pos];
            }
            if (TryConsumeWord("inf") || TryConsumeWord("INF") || TryConsumeWord("nan") || TryConsumeWord("NAN"))
                return text[s..pos];
            int ds = pos;
            while (!Eof && (char.IsDigit(text[pos]) || text[pos] == '_')) pos++;
            if (pos == ds) throw Fail("Bad number.");
            if (!Eof && text[pos] == '.')
            {
                pos++;
                int fs = pos;
                while (!Eof && (char.IsDigit(text[pos]) || text[pos] == '_')) pos++;
                if (pos == fs) throw Fail("Bad number.");
            }
            if (!Eof && (text[pos] == 'e' || text[pos] == 'E'))
            {
                int e = pos++;
                if (!Eof && (text[pos] == '+' || text[pos] == '-')) pos++;
                int xs = pos;
                while (!Eof && (char.IsDigit(text[pos]) || text[pos] == '_')) pos++;
                if (pos == xs) pos = e; // lone 'e' ends the number here
            }
            return text[s..pos];
        }

        private bool TryConsumeWord(string word)
        {
            int p = pos;
            foreach (char ch in word)
            {
                if (p >= text.Length || char.ToLowerInvariant(text[p]) != char.ToLowerInvariant(ch)) return false;
                p++;
            }
            pos = p;
            return true;
        }

        private PreviewTree.TreeNode ReadArray(int depth)
        {
            int line = LineOf(pos);
            pos++; // [
            var node = new PreviewTree.TreeNode { t = "a", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
            SkipGap();
            if (!Eof && text[pos] == ']') { pos++; node.e = TakePending(); return node; }
            while (true)
            {
                var child = ReadValue(depth + 1);
                node.c!.Add(child);
                Ate(child);
                if (trunc && node.c.Count >= MaxKidsAfterTrunc)
                {
                    pending.Clear();
                    last = null;
                    SkipToClose(']', '[');
                    return node;
                }
                SkipGap();
                if (Eof) throw Fail("Unterminated array.");
                if (text[pos] == ']') { pos++; node.e = TakePending(); return node; }
                if (text[pos] != ',') throw Fail("Expected ',' or ']'.");
                pos++; // ,
                SkipGap();
                if (!Eof && text[pos] == ']') { pos++; node.e = TakePending(); return node; } // trailing comma
            }
        }

        private PreviewTree.TreeNode ReadInlineTable(int depth)
        {
            int line = LineOf(pos);
            pos++; // {
            var node = new PreviewTree.TreeNode { t = "o", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
            SkipGap();
            if (!Eof && text[pos] == '}') { pos++; node.e = TakePending(); return node; }
            while (true)
            {
                var keys = ReadDottedKey();
                SkipInline();
                if (Eof || text[pos] != '=') throw Fail("Expected '='.");
                pos++;
                SkipInline();
                var val = ReadValue(depth + 1);
                AssignInto(node, keys, val);
                if (trunc && node.c.Count >= MaxKidsAfterTrunc)
                {
                    pending.Clear();
                    last = null;
                    SkipToClose('}', '{');
                    return node;
                }
                SkipGap();
                if (Eof) throw Fail("Unterminated inline table.");
                if (text[pos] == '}') { pos++; node.e = TakePending(); return node; }
                if (text[pos] != ',') throw Fail("Expected ',' or '}'.");
                pos++;
                SkipGap();
            }
        }

        public (List<(string Key, int Line)> Keys, PreviewTree.TreeNode Value) ReadKeyValue()
        {
            var keys = ReadDottedKey();
            SkipInline();
            if (Eof || text[pos] != '=') throw Fail("Expected '='.");
            pos++;
            SkipInline();
            var val = ReadValue(0);
            return (keys, val);
        }

        // ---- tables ----

        public PreviewTree.TreeNode ReadTableHeader(PreviewTree.TreeNode root)
        {
            pos++; // [
            bool isArray = !Eof && text[pos] == '[';
            if (isArray) pos++;
            var parts = ReadDottedKey();
            SkipInline();
            if (isArray)
            {
                if (Eof || text[pos] != ']' || pos + 1 >= text.Length || text[pos + 1] != ']')
                    throw Fail("Expected ']]'.");
                pos += 2;
                return NavigateArray(root, parts);
            }
            if (Eof || text[pos] != ']') throw Fail("Expected ']'.");
            pos++;
            return NavigateTable(root, parts, define: true);
        }

        private PreviewTree.TreeNode Descend(PreviewTree.TreeNode node, string key)
        {
            node.c ??= new List<PreviewTree.TreeNode>();
            foreach (var ch in node.c)
                if (ch.k == key) return ch;
            var n = new PreviewTree.TreeNode { t = "o", l = LineOf(pos), k = key, c = new List<PreviewTree.TreeNode>() };
            node.c!.Add(n);
            return n;
        }

        private PreviewTree.TreeNode NavigateTable(PreviewTree.TreeNode root, List<(string Key, int Line)> parts, bool define)
        {
            var node = root;
            string prefix = "";
            for (int i = 0; i < parts.Count; i++)
            {
                string key = parts[i].Key;
                prefix += "/" + key.Replace("~", "~0").Replace("/", "~1");
                bool last = i == parts.Count - 1;
                if (node.t == "a")
                {
                    if (node.c == null || node.c.Count == 0) throw Fail("Array is empty.");
                    node = node.c[^1]; // spec: subtable binds to the last element
                    continue;
                }
                if (node.t != "o") throw Fail($"'{key}' is already a value.");
                var next = node.c?.FirstOrDefault(c => c.k == key);
                if (next == null)
                {
                    next = new PreviewTree.TreeNode { t = "o", l = parts[i].Line, k = key, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                    node.c!.Add(next);
                }
                else if (next.t == "a")
                {
                    if (!last) { node = next; continue; }
                    throw Fail($"'{key}' is already an array.");
                }
                else if (next.t != "o") throw Fail($"'{key}' is already a value.");
                if (last && define)
                {
                    if (definedTables.Contains(prefix)) throw Fail($"Table '{key}' redefined.");
                    definedTables.Add(prefix);
                }
                node = next;
            }
            return node;
        }

        private PreviewTree.TreeNode NavigateArray(PreviewTree.TreeNode root, List<(string Key, int Line)> parts)
        {
            var node = root;
            for (int i = 0; i < parts.Count; i++)
            {
                string key = parts[i].Key;
                bool last = i == parts.Count - 1;
                if (node.t == "a")
                {
                    if (node.c == null || node.c.Count == 0) throw Fail("Array is empty.");
                    node = node.c[^1];
                    continue;
                }
                if (node.t != "o") throw Fail($"'{key}' is already a value.");
                var next = node.c?.FirstOrDefault(c => c.k == key);
                if (next == null)
                {
                    if (!last) { node = Descend(node, key); continue; }
                    next = new PreviewTree.TreeNode { t = "a", l = parts[i].Line, k = key, c = new List<PreviewTree.TreeNode>() };
                    node.c!.Add(next);
                }
                else if (next.t != "a") throw Fail($"'{key}' is already defined.");
                if (last)
                {
                    var el = new PreviewTree.TreeNode { t = "o", l = parts[i].Line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                    next.c!.Add(el);
                    Ate(el);
                    return el;
                }
                node = next;
            }
            throw Fail("Bad array header.");
        }

        public void Assign(PreviewTree.TreeNode table, List<(string Key, int Line)> keys, PreviewTree.TreeNode val)
        {
            var node = table;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                string key = keys[i].Key;
                if (node.t == "a") throw Fail("Cannot navigate into an array with a dotted key.");
                if (node.t != "o") throw Fail($"'{key}' is already a value.");
                var next = node.c?.FirstOrDefault(c => c.k == key);
                if (next == null)
                {
                    next = new PreviewTree.TreeNode { t = "o", l = keys[i].Line, k = key, c = new List<PreviewTree.TreeNode>() };
                    node.c!.Add(next);
                }
                else if (next.t != "o") throw Fail($"'{key}' is already defined.");
                node = next;
            }
            string lastKey = keys[^1].Key;
            if (node.t == "a") throw Fail("Cannot navigate into an array with a dotted key.");
            if (node.c != null && node.c!.Any(c => c.k == lastKey))
                throw Fail($"Key '{lastKey}' defined more than once.");
            node.c ??= new List<PreviewTree.TreeNode>();
            val.k = lastKey;
            node.c!.Add(val);
            Ate(val);
        }

        public void AssignInto(PreviewTree.TreeNode table, List<(string Key, int Line)> keys, PreviewTree.TreeNode val)
        {
            var node = table;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                string key = keys[i].Key;
                var next = node.c?.FirstOrDefault(c => c.k == key);
                if (next == null)
                {
                    next = new PreviewTree.TreeNode { t = "o", l = keys[i].Line, k = key, c = new List<PreviewTree.TreeNode>() };
                    node.c!.Add(next);
                }
                else if (next.t != "o") throw Fail($"'{key}' is already defined.");
                node = next;
            }
            string lastKey = keys[^1].Key;
            if (node.c != null && node.c!.Any(c => c.k == lastKey))
                throw Fail($"Key '{lastKey}' defined more than once.");
            node.c ??= new List<PreviewTree.TreeNode>();
            val.k = lastKey;
            node.c!.Add(val);
            Ate(val);
        }

        // Best effort: skip the current value when the node cap hits.
        public void SkipValue()
        {
            if (Eof) return;
            if (pos + 2 < text.Length &&
                ((text[pos] == '"' && text[pos + 1] == '"' && text[pos + 2] == '"') ||
                 (text[pos] == '\'' && text[pos + 1] == '\'' && text[pos + 2] == '\'')))
            {
                char q = text[pos];
                int e = text.IndexOf(new string(q, 3), pos + 3, StringComparison.Ordinal);
                pos = e < 0 ? text.Length : e + 3;
                return;
            }
            if (text[pos] == '[') { SkipToClose(']', '['); if (!Eof) pos++; return; }
            if (text[pos] == '{') { SkipToClose('}', '{'); if (!Eof) pos++; return; }
            while (!Eof && text[pos] != '\n') pos++;
        }

        public void SkipToClose(char close, char open)
        {
            int d = 0;
            bool inStr = false;
            char q = '\0';
            while (!Eof)
            {
                char c = text[pos];
                if (inStr)
                {
                    if (c == '\\') { pos++; }
                    else if (c == q) inStr = false;
                }
                else if (c == '"' || c == '\'')
                {
                    // Triple-quoted runs count as one opener.
                    if (pos + 2 < text.Length && text[pos + 1] == c && text[pos + 2] == c)
                    {
                        d++;
                        int e = text.IndexOf(new string(c, 3), pos + 3, StringComparison.Ordinal);
                        pos = e < 0 ? text.Length : e;
                    }
                    else { inStr = true; q = c; }
                }
                else if (c == open) d++;
                else if (c == close)
                {
                    if (d == 0) return;
                    d--;
                }
                else if (c == '#')
                {
                    while (!Eof && text[pos] != '\n') pos++;
                    continue;
                }
                pos++;
            }
        }
    }
}
