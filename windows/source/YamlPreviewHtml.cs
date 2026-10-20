using System.Text;
using System.Text.Json;
using static FluentNotepad.PreviewTree;
using System.Text.RegularExpressions;

namespace FluentNotepad;

/// <summary>
/// YAML (.yaml/.yml) -&gt; collapsible tree HTML for WebView2, on the shared
/// PreviewTree schema. Pragmatic indentation-based subset (not full YAML):
/// block mappings/sequences/scalars, flow collections (even multi-line),
/// comments, plain and quoted scalars, documents. Anchors/aliases/tags are
/// stripped, not resolved; merge keys (&lt;&lt;) stay plain keys; complex
/// (? ...) keys and tabs in indentation are rejected with a line number.
/// Copy paths use the $.a.b[2] form; subtree copy rebuilds JSON.
/// </summary>
public static class YamlPreviewHtml
{
    public static string Build(string text, bool dark, double fontSize, string? title, ISet<string>? open)
    {
        if (text.Length > MaxChars)
            return PageHead(dark, fontSize, title ?? "YAML") + TooLarge(MainWindow.Str("YamlTooLarge"));
        try
        {
            var root = Parse(text, out bool truncated);
            return TreePage(dark, fontSize, title ?? "YAML",
                truncated ? MainWindow.Str("YamlTruncated") : null,
                JsonSerializer.Serialize(root),
                JsonSerializer.Serialize(open ?? Enumerable.Empty<string>()),
                xml: false);
        }
        catch (PreviewTree.TreeParseException ex)
        {
            return ErrorPage(dark, fontSize, title ?? "YAML",
                MainWindow.Str("YamlInvalidTitle"),
                string.Format(MainWindow.Str("YamlInvalid"), ex.Line, ex.Column),
                ex.Message, text, MainWindow.Str("YamlErrorHint"));
        }
    }

    // MARK: - Parse (line-based, 1-based lines)

    private static PreviewTree.TreeNode Parse(string text, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(text)) throw new PreviewTree.TreeParseException("The YAML document is empty.", 1, 1);
        var w = new Walker(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        var docs = new List<PreviewTree.TreeNode>();
        w.SkipBlank();
        if (w.Eof) throw new PreviewTree.TreeParseException("The YAML document is empty.", 1, 1);
        while (!w.Eof)
        {
            var doc = w.ParseDoc();
            if (doc != null) docs.Add(doc);
            w.SkipBlank();
        }
        if (docs.Count == 0) throw new PreviewTree.TreeParseException("The YAML document is empty.", 1, 1);
        if (w.pending.Count > 0)
        {
            var last = docs[^1];
            (last.e ??= new List<string>()).AddRange(w.TakePending()!);
        }
        truncated = w.trunc;
        if (docs.Count == 1) return docs[0];
        var root = new PreviewTree.TreeNode { t = "o", l = 1, c = new List<PreviewTree.TreeNode>() };
        foreach (var d in docs) root.c!.Add(d);
        return root;
    }

    private sealed class Walker
    {
        private static readonly Regex BlockHeadRe = new(@"^[|>]\s*([+-]?\d+|\d+[+-]?)?\s*(#.*)?$", RegexOptions.Compiled);
        public readonly List<string> lines;
        public int idx;
        public int count;
        public bool trunc;
        public readonly List<string> pending = new();
        public PreviewTree.TreeNode? last;
        public int lastLine;

        public Walker(string text)
        {
            lines = new List<string>(text.Split('\n'));
        }

        public bool Eof => idx >= lines.Count;
        public int Line => idx + 1; // 1-based
        public string Cur => Eof ? "" : lines[idx];

        public PreviewTree.TreeParseException Fail(string msg, int? line = null) =>
            new(msg, line ?? Line, 1);

        public List<string>? TakePending()
        {
            if (pending.Count == 0) return null;
            var l = new List<string>(pending);
            pending.Clear();
            return l;
        }

        public void Ate(PreviewTree.TreeNode n, int line)
        {
            last = n;
            lastLine = line;
            if (++count > MaxNodes)
            {
                trunc = true;
                pending.Clear();
                last = null;
                idx = lines.Count; // abandon the rest; levels unwind with partial trees
            }
        }

        public void AteComment(string raw, int line)
        {
            if (last != null && line == lastLine) { (last.e ??= new List<string>()).Add(raw); }
            else pending.Add(raw);
        }

        public static int IndentOf(string s)
        {
            int i = 0;
            while (i < s.Length && s[i] == ' ') i++;
            return i;
        }

        public static bool IsBlank(string s)
        {
            foreach (char c in s) if (c != ' ' && c != '\t') return false;
            return true;
        }

        public static bool IsCommentLine(string s)
        {
            int i = IndentOf(s);
            return i < s.Length && s[i] == '#';
        }

        // Blank lines and full-line comments (attached as remarks).
        public void SkipBlank()
        {
            while (!Eof)
            {
                string s = Cur;
                if (IsBlank(s)) { idx++; continue; }
                int ind = IndentOf(s);
                if (ind < s.Length && s[ind] == '\t') throw Fail("Tabs are not allowed for indentation.");
                if (IsCommentLine(s)) { AteComment(s.Trim(), Line); idx++; continue; }
                if (ind == 0 && s.StartsWith("%", StringComparison.Ordinal)) { idx++; continue; } // directive: skip
                return;
            }
        }

        // ---- documents ----

        public PreviewTree.TreeNode? ParseDoc()
        {
            // Optional explicit start (content after --- belongs to the doc).
            if (!Eof && IsDocMarker(Cur, "---", out string? rest))
            {
                if (rest == null) { idx++; SkipBlank(); if (Eof) return null; }
                else lines[idx] = rest; // reprocess the remainder as content
            }
            if (Eof) return null;
            if (IsDocMarker(Cur, "...", out string? restEnd))
            {
                if (restEnd != null) throw Fail("Unexpected content after '...'.");
                idx++;
                return null;
            }
            var node = ParseBlock(-1, 0);
            if (!Eof && IsDocMarker(Cur, "...", out string? restEnd2))
            {
                if (restEnd2 != null) throw Fail("Unexpected content after '...'.");
                idx++;
            }
            return node;
        }

        private static bool IsDocMarker(string s, string mark, out string? rest)
        {
            rest = null;
            string t = s.Trim();
            if (t == mark) return true;
            if (t.StartsWith(mark + " ", StringComparison.Ordinal) || t.StartsWith(mark + "\t", StringComparison.Ordinal))
            {
                rest = t[(mark.Length + 1)..];
                return true;
            }
            return false;
        }

        // ---- blocks ----

        public PreviewTree.TreeNode ParseBlock(int parentIndent, int depth = 0)
        {
            if (depth > MaxDepth) throw new PreviewTree.TreeParseException("Too deeply nested.", Line, 1);
            SkipBlank();
            if (Eof) throw Fail("Unexpected end of document.");
            string first = Cur;
            int ind = IndentOf(first);
            if (parentIndent < 0) { /* document root: any indentation goes */ }
            else if (ind <= parentIndent) throw Fail("Bad indentation.");
            CheckNoTabs(first);
            bool seq = IsSeqItem(first, ind);
            var node = new PreviewTree.TreeNode
            {
                t = seq ? "a" : "o",
                l = Line,
                c = new List<PreviewTree.TreeNode>(),
                m = TakePending()
            };
            int expect = ind;
            bool? wantSeq = null;
            while (true)
            {
                SkipBlank();
                if (Eof) break;
                string s = Cur;
                int ci = IndentOf(s);
                CheckNoTabs(s);
                if (ci < expect) break;
                if (ci > expect) throw Fail("Bad indentation.");
                bool isSeq = IsSeqItem(s, ci);
                if (wantSeq == null) wantSeq = isSeq;
                else if (wantSeq != isSeq) throw Fail("Cannot mix mapping and sequence entries.");
                if (isSeq) ParseSeqItem(node, ci, depth);
                else ParseMapEntry(node, ci, depth);
                if (trunc) return node;
            }
            node.e = TakePending();
            return node;
        }

        private void CheckNoTabs(string s)
        {
            int ind = IndentOf(s);
            for (int i = 0; i < ind; i++)
                if (s[i] == '\t') throw Fail("Tabs are not allowed for indentation.");
        }

        private static bool IsSeqItem(string s, int indent)
        {
            if (indent >= s.Length || s[indent] != '-') return false;
            if (indent + 1 >= s.Length) return true; // lone "-"
            char n = s[indent + 1];
            return n == ' ' || n == '\t';
        }

        private void SkipToDedent(int indent)
        {
            while (!Eof)
            {
                string s = Cur;
                if (IsBlank(s) || IsCommentLine(s)) { idx++; continue; }
                if (IndentOf(s) < indent) return;
                idx++;
            }
        }

        private void ParseSeqItem(PreviewTree.TreeNode seq, int dashIndent, int depth)
        {
            string s = Cur;
            int line = Line;
            int p = dashIndent + 1;
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++;
            string rest = s[p..].TrimEnd();
            idx++; // consume the dash line
            var (restNC, restComment) = SplitComment(rest);
            rest = restNC;
            void AttachRest(PreviewTree.TreeNode n) { if (restComment != null) (n.e ??= new List<string>()).Add(restComment); }
            if (IsFlowStart(rest)) rest = ReadFlowLines(rest, line);
            if (rest == "")
            {
                // Nested block (or null).
                if (restComment != null) AteComment(restComment, line);
                SkipBlank();
                if (Eof || IndentOf(Cur) <= dashIndent)
                {
                    var nul = new PreviewTree.TreeNode { t = "u", l = line, m = TakePending() };
                    seq.c!.Add(nul);
                    Ate(nul, line);
                    return;
                }
                var child = ParseBlock(dashIndent, depth + 1);
                seq.c!.Add(child);
                Ate(child, line);
                return;
            }
            if (BlockHeadRe.IsMatch(rest))
            {
                var txt = ReadBlockScalar(dashIndent, rest, line);
                var lit = new PreviewTree.TreeNode { t = "s", v = txt, l = line, m = TakePending() };
                seq.c!.Add(lit);
                Ate(lit, line);
                AttachRest(lit);
                return;
            }
            if (rest == "-" || rest.StartsWith("- ", StringComparison.Ordinal))
            {
                // Nested sequence on one line: "- - a".
                var sub = new PreviewTree.TreeNode { t = "a", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                seq.c!.Add(sub);
                ParseSeqInline(sub, rest, dashIndent, line, depth);
                Ate(sub, line);
                AttachRest(sub);
                return;
            }
            var (kind, key, raw) = SplitInline(rest, line);
            if (kind == InlineKind.Mapping)
            {
                if (IsBlockHead(raw))
                {
                    var txt = ReadBlockScalar(dashIndent, raw, line);
                    var map = new PreviewTree.TreeNode { t = "o", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                    map.c!.Add(new PreviewTree.TreeNode { t = "s", k = key, v = txt, l = line });
                    seq.c!.Add(map);
                    Ate(map, line);
                    AttachRest(map);
                    SkipBlank();
                    return;
                }
                var map2 = new PreviewTree.TreeNode { t = "o", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                var leaf = ScalarNode(raw, line);
                leaf.k = key;
                map2.c!.Add(leaf);
                Ate(leaf, line);
                CheckDup(map2, key, line);
                seq.c!.Add(map2);
                AttachRest(map2);
                SkipBlank();
                if (!Eof && IndentOf(Cur) > dashIndent && !IsSeqItem(Cur, IndentOf(Cur)))
                    ContinueMapping(map2, IndentOf(Cur), depth);
                else
                    Ate(map2, line);
                return;
            }
            var leaf2 = ScalarNode(raw, line);
            leaf2.m = TakePending();
            seq.c!.Add(leaf2);
            Ate(leaf2, line);
            AttachRest(leaf2);
        }

        // "- - a" then following deeper lines belong to the same sub-sequence.
        private void ParseSeqInline(PreviewTree.TreeNode sub, string rest, int dashIndent, int line, int depth)
        {
            string first = rest[1..].TrimStart(); // after inner '-'
            if (IsBlockHead(first))
            {
                var txt = ReadBlockScalar(dashIndent, first, line);
                var lit = new PreviewTree.TreeNode { t = "s", v = txt, l = line };
                sub.c!.Add(lit);
                Ate(lit, line);
            }
            else
            {
                var (kind, key, raw) = SplitInline(first, line);
                if (kind == InlineKind.Mapping)
                {
                    var map = new PreviewTree.TreeNode { t = "o", l = line, c = new List<PreviewTree.TreeNode>() };
                    var leaf = ScalarNode(raw, line);
                    leaf.k = key;
                    map.c!.Add(leaf);
                    Ate(leaf, line);
                    CheckDup(map, key, line);
                    sub.c!.Add(map);
                }
                else
                {
                    var leaf = ScalarNode(raw, line);
                    sub.c!.Add(leaf);
                    Ate(leaf, line);
                }
            }
            SkipBlank();
            while (!Eof && IndentOf(Cur) > dashIndent)
            {
                string s = Cur;
                int ci = IndentOf(s);
                if (!IsSeqItem(s, ci)) break;
                string r2 = s[(ci + 1)..].TrimStart().TrimEnd();
                idx++;
                var (r2n, r2c) = SplitComment(r2);
                r2 = r2n;
                if (r2 == "")
                {
                    if (r2c != null) AteComment(r2c, line);
                    SkipBlank();
                    if (!Eof && IndentOf(Cur) > dashIndent) { var cn = ParseBlock(dashIndent, depth + 1); sub.c!.Add(cn); Ate(cn, line); }
                    else { var un = new PreviewTree.TreeNode { t = "u", l = line }; sub.c!.Add(un); Ate(un, line); }
                    continue;
                }
                if (IsFlowStart(r2)) r2 = ReadFlowLines(r2, line);
                var (k2, key2, raw2) = SplitInline(r2, line);
                if (k2 == InlineKind.Mapping)
                {
                    var map = new PreviewTree.TreeNode { t = "o", l = line, c = new List<PreviewTree.TreeNode>() };
                    var leaf = ScalarNode(raw2, line);
                    leaf.k = key2;
                    map.c!.Add(leaf);
                    CheckDup(map, key2, line);
                    sub.c!.Add(map);
                    Ate(map, line);
                    if (r2c != null) (map.e ??= new List<string>()).Add(r2c);
                }
                else
                {
                    var sl = ScalarNode(raw2, line);
                    sub.c!.Add(sl);
                    Ate(sl, line);
                    if (r2c != null) (sl.e ??= new List<string>()).Add(r2c);
                }
            }
        }

        private void ContinueMapping(PreviewTree.TreeNode map, int expect, int depth)
        {
            while (true)
            {
                SkipBlank();
                if (Eof) break;
                string s = Cur;
                int ci = IndentOf(s);
                CheckNoTabs(s);
                if (ci < expect) break;
                if (ci > expect) throw Fail("Bad indentation.");
                if (IsSeqItem(s, ci)) throw Fail("Cannot mix mapping and sequence entries.");
                ParseMapEntry(map, ci, depth);
                if (trunc) return;
            }
            var extra = TakePending();
            if (extra != null) { (map.e ??= new List<string>()).AddRange(extra); }
        }

        private void ParseMapEntry(PreviewTree.TreeNode map, int keyIndent, int depth)
        {
            string s = Cur;
            int line = Line;
            // Trailing comment on the key line belongs to the value being defined.
            var (_, keyComment) = SplitComment(s[keyIndent..]);
            void AttachComment() { if (keyComment != null && last != null) (last.e ??= new List<string>()).Add(keyComment); }
            var (key, after) = SplitKey(s, keyIndent);
            idx++; // consume the key line
            if (after == null)
            {
                // No value on this line: nested block or null.
                SkipBlank();
                if (!Eof && IndentOf(Cur) > keyIndent && !IsDocMarker(Cur, "---", out _) && !IsDocMarker(Cur, "...", out _))
                {
                    var child = ParseBlock(keyIndent, depth + 1);
                    child.k = key;
                    map.c!.Add(child);
                    Ate(child, line);
                    AttachComment();
                    CheckDup(map, key, line);
                    return;
                }
                var n = new PreviewTree.TreeNode { t = "u", k = key, l = line, m = TakePending() };
                map.c!.Add(n);
                Ate(n, line);
                AttachComment();
                CheckDup(map, key, line);
                return;
            }
            if (IsBlockHead(after))
            {
                var txt = ReadBlockScalar(keyIndent, after, line);
                var n = new PreviewTree.TreeNode { t = "s", k = key, v = txt, l = line, m = TakePending() };
                map.c!.Add(n);
                Ate(n, line);
                AttachComment();
                CheckDup(map, key, line);
                return;
            }
            string flow = ReadFlowLines(after, line);
            var leaf = ScalarNode(flow, line);
            leaf.k = key;
            leaf.m = TakePending();
            map.c!.Add(leaf);
            Ate(leaf, line);
            AttachComment();
            CheckDup(map, key, line);
        }

        private static void CheckDup(PreviewTree.TreeNode map, string key, int line)
        {
            if (map.c == null) return;
            int n = 0;
            foreach (var c in map.c) if (c.k == key && ++n > 1)
                throw new PreviewTree.TreeParseException($"Duplicate key '{key}'.", line, 1);
        }

        private enum InlineKind { Scalar, Mapping }

        // Dash-line remainder: either a scalar/flow or the first pair of a mapping.
        // Returns (kind, key, rawValue).
        private (InlineKind Kind, string Key, string Raw) SplitInline(string rest, int line)
        {
            string t = rest.TrimStart();
            if (t.StartsWith("[", StringComparison.Ordinal) || t.StartsWith("{", StringComparison.Ordinal))
                return (InlineKind.Scalar, "", rest);
            int colon = ColonOutside(rest);
            if (colon >= 0)
            {
                string k = Unquote(rest[..colon].Trim());
                if (k == "" || k.StartsWith("?")) throw Fail("Unsupported mapping key.", line);
                return (InlineKind.Mapping, k, rest[(colon + 1)..].Trim());
            }
            return (InlineKind.Scalar, "", rest);
        }

        // Splits "key: value" at the key colon. Returns (key, value-or-null).
        private (string Key, string? Value) SplitKey(string s, int keyIndent)
        {
            int p = keyIndent;
            bool inS = false, inD = false;
            int depth = 0;
            for (int i = p; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '[' || c == '{') { depth++; continue; }
                if (c == ']' || c == '}') { depth--; continue; }
                if (c == ':' && depth == 0 && i + 1 < s.Length && (s[i + 1] == ' ' || s[i + 1] == '\t' || s[i + 1] == '\r'))
                {
                    string key = StripRefs(s[p..i].Trim(), out _);
                    if (key == "" || key == "?" || key.StartsWith("? ")) throw Fail("Unsupported mapping key.");
                    string rest = s[(i + 1)..].Trim();
                    if (rest.StartsWith("#", StringComparison.Ordinal)) rest = "";
                    else rest = StripComment(rest);
                    return (Unquote(key), rest == "" ? null : rest);
                }
                if (c == ':' && depth == 0 && i + 1 == s.Length)
                {
                    string key = StripRefs(s[p..i].Trim(), out _);
                    if (key == "") throw Fail("Expected a key.");
                    return (Unquote(key), null);
                }
            }
            throw Fail("Expected ':'.");
        }

        // First ':' outside quotes/flow followed by space/EOL (or at EOL).
        private static int ColonOutside(string s)
        {
            bool inS = false, inD = false;
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '[' || c == '{') { depth++; continue; }
                if (c == ']' || c == '}') { depth--; continue; }
                if (c == ':' && depth == 0 && (i + 1 >= s.Length || s[i + 1] == ' ' || s[i + 1] == '\t' || s[i + 1] == '\r')) return i;
            }
            return -1;
        }

        // Strips a trailing " #comment" outside quotes.
        private static string StripComment(string s)
        {
            bool inS = false, inD = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '#' && i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t')) return s[..(i - 1)].TrimEnd();
            }
            return s;
        }

        // Splits value and trailing comment (quote-aware). The comment keeps '#'.
        private static (string Value, string? Comment) SplitComment(string s)
        {
            bool inS = false, inD = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t'))
                    return (s[..i].TrimEnd(), s[i..].Trim());
            }
            return (s, null);
        }

        // Removes #...\n and trailing #... segments outside quotes (flow values).
        private static string StripFlowComments(string s)
        {
            var sb = new StringBuilder();
            bool inS = false, inD = false;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (inS)
                {
                    sb.Append(c);
                    if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; } else inS = false; }
                    i++;
                    continue;
                }
                if (inD)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                    if (c == '"') inD = false;
                    i++;
                    continue;
                }
                if (c == '\'') { inS = true; sb.Append(c); i++; continue; }
                if (c == '"') { inD = true; sb.Append(c); i++; continue; }
                if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsBlockHead(string v)
        {
            return BlockHeadRe.IsMatch(v.Trim());
        }

        // Strips leading tag/anchor/alias refs (!x, &x, *x). Returns cleaned value.
        private static string StripRefs(string s, out bool had)
        {
            had = false;
            string t = s.TrimStart();
            while (t.Length > 0 && (t[0] == '!' || t[0] == '&' || t[0] == '*'))
            {
                had = true;
                int i = 1;
                while (i < t.Length && t[i] != ' ' && t[i] != '\t') i++;
                t = t[i..].TrimStart();
            }
            return t;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            {
                var sb = new StringBuilder();
                for (int i = 1; i < s.Length - 1; i++)
                {
                    if (s[i] == '\\' && i + 1 < s.Length - 1)
                    {
                        i++;
                        sb.Append(s[i] switch
                        {
                            'n' => "\n",
                            't' => "\t",
                            '"' => "\"",
                            '\\' => "\\",
                            _ => "\\" + s[i],
                        });
                    }
                    else if (s[i] == '"' && i + 1 < s.Length - 1 && s[i + 1] == '"') { sb.Append('"'); i++; }
                    else sb.Append(s[i]);
                }
                return sb.ToString();
            }
            if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
                return s[1..^1].Replace("''", "'");
            return s;
        }

        // If a flow collection opens here but doesn't close on this line,
        // consume following lines (joined with \n) until balanced.
        private static bool IsFlowStart(string s)
        {
            string t = s.TrimStart();
            return t.StartsWith("[", StringComparison.Ordinal) || t.StartsWith("{", StringComparison.Ordinal);
        }

        private string ReadFlowLines(string first, int line)
        {
            string t = first.TrimStart();
            if ((!t.StartsWith("[", StringComparison.Ordinal) && !t.StartsWith("{", StringComparison.Ordinal)) || FlowBalanced(t))
                return first;
            var sb = new StringBuilder(first);
            int startIdx = line - 1;
            while (idx < lines.Count)
            {
                string ln = lines[idx];
                // Whole-line comments inside a flow: keep them as remarks.
                if (ln.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    AteComment(ln.Trim(), idx + 1);
                    idx++;
                    continue;
                }
                sb.Append('\n');
                sb.Append(ln);
                idx++;
                if (FlowBalanced(StripFlowComments(sb.ToString()))) break;
                if (idx - startIdx > 500) throw Fail("Unbalanced flow collection.", line);
            }
            string joined = sb.ToString();
            if (!FlowBalanced(StripFlowComments(joined))) throw Fail("Unbalanced flow collection.", line);
            return joined;
        }

        private static bool FlowBalanced(string s)
        {
            bool inS = false, inD = false;
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}') { depth--; if (depth < 0) return false; }
            }
            return depth == 0;
        }

        // ---- scalar values ----

        private PreviewTree.TreeNode ScalarNode(string raw, int line)
        {
            string t0 = raw.Trim();
            if (t0.StartsWith("*", StringComparison.Ordinal))
                return new PreviewTree.TreeNode { t = "s", v = t0, l = line }; // unresolved alias: kept verbatim
            string v = StripRefs(t0, out _);
            if (v == "" || v == "~") return new PreviewTree.TreeNode { t = "u", l = line };
            if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
                return new PreviewTree.TreeNode { t = "s", v = Unquote(v), l = line };
            if (v[0] == '[' || v[0] == '{') return ParseFlow(v, line);
            string low = v.ToLowerInvariant();
            if (low is "true" or "false" or "yes" or "no" or "on" or "off")
                return new PreviewTree.TreeNode { t = "b", v = low is "true" or "yes" or "on" ? "true" : "false", l = line };
            if (low is "null" or "nul")
                return new PreviewTree.TreeNode { t = "u", l = line };
            if (low is ".inf" or ".infinity" or "-.inf" or "-.infinity" or "+.inf" or "+.infinity" or ".nan")
                return new PreviewTree.TreeNode { t = "n", v = v, l = line };
            if (IsInt(v) || IsFloat(v))
                return new PreviewTree.TreeNode { t = "n", v = v, l = line };
            return new PreviewTree.TreeNode { t = "s", v = v, l = line };
        }

        private static bool IsInt(string v)
        {
            int i = 0;
            if (v.Length == 0) return false;
            if (v[0] == '+' || v[0] == '-') { if (v.Length == 1) return false; i = 1; }
            if (v.Length - i > 2 && v[i] == '0' && (v[i + 1] == 'x' || v[i + 1] == 'X'))
                return v[(i + 2)..].Trim('_').Length > 0 && v[(i + 2)..].All(c => Uri.IsHexDigit(c) || c == '_');
            if (v.Length - i > 2 && v[i] == '0' && (v[i + 1] == 'o' || v[i + 1] == 'O'))
                return v[(i + 2)..].Trim('_').Length > 0 && v[(i + 2)..].All(c => (c >= '0' && c <= '7') || c == '_');
            bool d = false;
            for (; i < v.Length; i++)
            {
                char c = v[i];
                if (char.IsDigit(c)) d = true;
                else if (c != '_') return false;
            }
            return d;
        }

        private static bool IsFloat(string v)
        {
            int i = 0;
            if (v.Length == 0) return false;
            if (v[0] == '+' || v[0] == '-') { if (v.Length == 1) return false; i = 1; }
            bool digit = false, dot = false, exp = false;
            for (; i < v.Length; i++)
            {
                char c = v[i];
                if (char.IsDigit(c)) digit = true;
                else if (c == '_') { }
                else if (c == '.' && !dot && !exp) dot = true;
                else if ((c == 'e' || c == 'E') && !exp && digit)
                {
                    exp = true;
                    digit = false;
                    if (i + 1 < v.Length && (v[i + 1] == '+' || v[i + 1] == '-')) i++;
                }
                else return false;
            }
            return digit && (dot || exp);
        }

        // ---- flow collections ----

        private PreviewTree.TreeNode ParseFlow(string s, int line)
        {
            s = StripFlowComments(s);
            if (s[0] == '[')
            {
                int end = MatchBracket(s, '[', ']');
                if (end < 0) throw Fail("Unbalanced flow collection.", line);
                var node = new PreviewTree.TreeNode { t = "a", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                foreach (var item in SplitTop(s[1..end], ','))
                {
                    string it = StripComment(item.Trim());
                    if (it == "") continue;
                    var child = ScalarNode(it, line);
                    node.c!.Add(child);
                    Ate(child, line);
                    if (trunc && node.c.Count >= MaxKidsAfterTrunc) { pending.Clear(); last = null; return node; }
                }
                node.e = TakePending();
                AttachFlowComment(node, s, end);
                return node;
            }
            else
            {
                int end = MatchBracket(s, '{', '}');
                if (end < 0) throw Fail("Unbalanced flow collection.", line);
                var node = new PreviewTree.TreeNode { t = "o", l = line, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                foreach (var item in SplitTop(s[1..end], ','))
                {
                    string it = StripComment(item.Trim());
                    if (it == "") continue;
                    int colon = ColonOutside(it);
                    if (colon < 0) throw Fail("Expected ':' in flow mapping.", line);
                    string k = Unquote(it[..colon].Trim());
                    var val = ScalarNode(it[(colon + 1)..].Trim(), line);
                    val.k = k;
                    val.m = TakePending();
                    if (node.c!.Any(c => c.k == k)) throw Fail($"Duplicate key '{k}'.", line);
                    node.c!.Add(val);
                    Ate(val, line);
                    if (trunc && node.c.Count >= MaxKidsAfterTrunc) { pending.Clear(); last = null; return node; }
                }
                node.e = TakePending();
                AttachFlowComment(node, s, end);
                return node;
            }
        }

        // Trailing "# comment" after the closing bracket belongs to the node.
        private void AttachFlowComment(PreviewTree.TreeNode node, string s, int end)
        {
            int p = end + 1;
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++;
            if (p < s.Length && s[p] == '#') { (node.e ??= new List<string>()).Add(s[p..].TrimEnd()); }
        }

        private static int MatchBracket(string s, char open, char close)
        {
            bool inS = false, inD = false;
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static List<string> SplitTop(string s, char sep)
        {
            var out_ = new List<string>();
            bool inS = false, inD = false;
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inS) { if (c == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else inS = false; } continue; }
                if (inD) { if (c == '\\') i++; else if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}') depth--;
                else if (c == sep && depth == 0) { out_.Add(s[start..i]); start = i + 1; }
            }
            out_.Add(s[start..]);
            return out_;
        }

        // ---- block scalars ----

        private string ReadBlockScalar(int keyIndent, string header, int line)
        {
            int p = 0;
            char style = header[p++];
            char chomp = '\0';
            int explicitIndent = -1;
            while (p < header.Length)
            {
                char c = header[p];
                if ((c == '+' || c == '-') && chomp == '\0') { chomp = c; p++; }
                else if (char.IsDigit(c) && explicitIndent < 0)
                {
                    int q = p;
                    while (q < header.Length && char.IsDigit(header[q])) q++;
                    explicitIndent = int.Parse(header[p..q]) - 1; // relative to parent
                    p = q;
                }
                else if (c == ' ' || c == '\t') p++;
                else if (c == '#') break; // trailing comment
                else throw Fail("Bad block scalar header.", line);
            }
            var raw = new List<string>();
            int contentIndent = -1;
            int i = idx;
            while (i < lines.Count)
            {
                string s = lines[i];
                if (IsBlank(s)) { raw.Add(""); i++; continue; }
                int ind = IndentOf(s);
                if (ind <= keyIndent) break;
                if (contentIndent < 0)
                {
                    contentIndent = explicitIndent >= 0 ? keyIndent + 1 + explicitIndent : ind;
                    if (ind < contentIndent) throw Fail("Bad block scalar indentation.", i + 1);
                }
                if (ind < contentIndent) break;
                raw.Add(s.Length >= contentIndent ? s[contentIndent..] : "");
                i++;
            }
            idx = i;
            while (raw.Count > 0 && raw[^1] == "") raw.RemoveAt(raw.Count - 1);
            if (style == '|')
            {
                string body = string.Join("\n", raw);
                return chomp switch
                {
                    '-' => body,
                    '+' => body + "\n" + new string('\n', CountTrailingBlanks()),
                    _ => body + (body.Length > 0 ? "\n" : ""),
                };
            }
            else
            {
                var paras = new List<string>();
                var cur = new StringBuilder();
                foreach (string ln in raw)
                {
                    if (ln == "")
                    {
                        if (cur.Length > 0) { paras.Add(cur.ToString()); cur.Clear(); }
                        paras.Add("");
                    }
                    else
                    {
                        if (cur.Length > 0) cur.Append(' ');
                        cur.Append(ln);
                    }
                }
                if (cur.Length > 0) paras.Add(cur.ToString());
                string body = string.Join("\n", paras);
                return chomp switch
                {
                    '-' => body.TrimEnd('\n'),
                    '+' => body + "\n" + new string('\n', CountTrailingBlanks()),
                    _ => body + (body.Length > 0 && !body.EndsWith('\n') ? "\n" : ""),
                };
            }

            int CountTrailingBlanks()
            {
                int n = 0, j = idx - 1;
                while (j >= 0 && IsBlank(lines[j])) { n++; j--; }
                return n;
            }
        }
    }
}
