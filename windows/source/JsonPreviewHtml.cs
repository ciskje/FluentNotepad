using System.Buffers;
using System.Text;
using System.Text.Json;
using static FluentNotepad.PreviewTree;

namespace FluentNotepad;

/// <summary>
/// JSON (.json/.jsonc) -&gt; collapsible tree HTML for WebView2. One streaming
/// Utf8JsonReader pass builds the node tree (positions included, no second
/// parser): comments skipped and trailing commas allowed here, so .jsonc needs
/// no browser-side parsing — the page only renders the embedded nodes with
/// textContent, never innerHTML from values. Node identity for the per-tab
/// open state is the RFC 6901 JSON Pointer; the $.a.b[2] form is derived in
/// JS only for the copy button. Large docs are capped (10 MB / 300k nodes):
/// NavigateToString has a 2 MB ceiling, so MainWindow serves this HTML from a
/// temp file instead.
/// </summary>
public static class JsonPreviewHtml
{
    public static string Build(string text, bool dark, double fontSize, string? title, ISet<string>? open)
    {
        if (text.Length > MaxChars)
            return PageHead(dark, fontSize, title ?? "JSON") + TooLarge(MainWindow.Str("JsonTooLarge"));
        try
        {
            var root = Parse(text, out bool truncated);
            return TreePage(dark, fontSize, title ?? "JSON",
                truncated ? MainWindow.Str("JsonTruncated") : null,
                JsonSerializer.Serialize(root),
                JsonSerializer.Serialize(open ?? Enumerable.Empty<string>()),
                xml: false);
        }
        catch (JsonException ex)
        {
            // JsonException positions are zero-based: the page shows 1-based.
            long line = (ex.LineNumber ?? 0) + 1;
            long col = (ex.BytePositionInLine ?? 0) + 1;
            return ErrorPage(dark, fontSize, title ?? "JSON",
                MainWindow.Str("JsonInvalidTitle"),
                string.Format(MainWindow.Str("JsonInvalid"), line, col),
                ex.Message, text, MainWindow.Str("JsonErrorHint"));
        }
    }

    // MARK: - Parse (single streaming pass, positions included)

    private static PreviewTree.TreeNode Parse(string text, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(text)) throw new JsonException("The JSON document is empty.");
        var w = new Walker(text);
        // Leading comments belong to the root.
        while (true)
        {
            if (!w.r.Read()) throw new JsonException("The JSON document is empty.");
            if (w.r.TokenType == JsonTokenType.Comment) { w.AteComment(w.r.GetComment()); continue; }
            break;
        }
        var root = w.ReadValue(0);
        w.Ate(root);
        // Trailing comments hug the root end; anything else is garbage.
        while (w.r.Read())
        {
            if (w.r.TokenType == JsonTokenType.Comment) { w.AteComment(w.r.GetComment()); continue; }
            throw new JsonException("Extra content after the JSON document.");
        }
        if (w.pending.Count > 0) { (root.e ??= new List<string>()).AddRange(w.TakePending()!); }
        truncated = w.trunc;
        return root;
    }

    // Comments ride in the same token stream (CommentHandling.Allow) and are
    // attached as leading (m) or trailing (e) remarks of the node they stick to.
    // A ref struct (like the Utf8JsonReader it owns): stack-only, never boxed.
    private ref struct Walker
    {
        public readonly byte[] bytes;
        // Byte-based line starts (\n never hides inside a UTF-8 multibyte
        // sequence): TokenStartIndex is a byte offset, so this maps 1:1 even
        // past non-ASCII.
        public readonly List<int> starts = new() { 0 };
        public Utf8JsonReader r;
        public int count;
        public bool trunc;
        public readonly List<string> pending = new();
        public PreviewTree.TreeNode? last;
        public int lastLine;

        public Walker(string text)
        {
            bytes = Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] == (byte)'\n') starts.Add(i + 1);
            r = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Allow,
                AllowTrailingCommas = true,
            });
        }

        public int LineOf(long bytePos)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= bytePos) lo = mid;
                else hi = mid - 1;
            }
            return lo + 1;
        }

        public List<string>? TakePending()
        {
            if (pending.Count == 0) return null;
            var l = new List<string>(pending);
            pending.Clear();
            return l;
        }

        // GetComment returns the bare inner text: restore the markers so the
        // tree shows real comments (remarks never copy). Same-line comments
        // trail the previous value; otherwise they lead the next one.
        public void Ate(PreviewTree.TreeNode n) { last = n; lastLine = LineOf(r.TokenStartIndex); }

        public void AteComment(string inner)
        {
            // TokenStartIndex aims at the comment's opening delimiter.
            long pos = r.TokenStartIndex;
            bool block = pos + 1 < bytes.Length
                && bytes[(int)pos] == (byte)'/' && bytes[(int)pos + 1] == (byte)'*';
            string c = (block || inner.Contains('\n')) ? "/*" + inner + "*/" : "//" + inner;
            if (last != null && LineOf(pos) == lastLine) { (last.e ??= new List<string>()).Add(c); }
            else pending.Add(c);
        }

        // Current token is the value's first token. Consumes through its end.
        public PreviewTree.TreeNode ReadValue(int depth)
        {
            last = null;
            count++;
            if (count > MaxNodes || depth > MaxDepth)
            {
                // Cap hit: swallow the whole value, leave one widow marker.
                trunc = true;
                pending.Clear();
                last = null;
                int l = LineOf(r.TokenStartIndex);
                SkipValue();
                return new PreviewTree.TreeNode { t = "s", v = "…", l = l };
            }
            switch (r.TokenType)
            {
                case JsonTokenType.StartObject:
                    {
                        var node = new PreviewTree.TreeNode { t = "o", l = LineOf(r.TokenStartIndex), c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                        while (r.Read())
                        {
                            if (r.TokenType == JsonTokenType.Comment) { AteComment(r.GetComment()); continue; }
                            if (r.TokenType == JsonTokenType.EndObject) { node.e = TakePending(); return node; }
                            if (r.TokenType != JsonTokenType.PropertyName)
                                throw new JsonException("Invalid JSON: expected a property name.");
                            string key = r.GetString() ?? "";
                            while (true)
                            {
                                if (!r.Read()) throw new JsonException("Invalid JSON: unexpected end of document.");
                                if (r.TokenType == JsonTokenType.Comment) { AteComment(r.GetComment()); continue; }
                                break;
                            }
                            var child = ReadValue(depth + 1);
                            child.k = key;
                            node.c.Add(child);
                            Ate(child);
                            if (trunc && node.c.Count >= MaxKidsAfterTrunc)
                            {
                                pending.Clear();
                                last = null;
                                SkipRest(JsonTokenType.EndObject);
                                return node;
                            }
                        }
                        throw new JsonException("Invalid JSON: unexpected end of document.");
                    }
                case JsonTokenType.StartArray:
                    {
                        var node = new PreviewTree.TreeNode { t = "a", l = LineOf(r.TokenStartIndex), c = new List<PreviewTree.TreeNode>(), m = TakePending() };
                        while (true)
                        {
                            if (!r.Read()) throw new JsonException("Invalid JSON: unexpected end of document.");
                            if (r.TokenType == JsonTokenType.Comment) { AteComment(r.GetComment()); continue; }
                            if (r.TokenType == JsonTokenType.EndArray) { node.e = TakePending(); return node; }
                            var child = ReadValue(depth + 1);
                            node.c.Add(child);
                            Ate(child);
                            if (trunc && node.c.Count >= MaxKidsAfterTrunc)
                            {
                                pending.Clear();
                                last = null;
                                SkipRest(JsonTokenType.EndArray);
                                return node;
                            }
                        }
                    }
                case JsonTokenType.String:
                    return new PreviewTree.TreeNode { t = "s", v = r.GetString() ?? "", l = LineOf(r.TokenStartIndex), m = TakePending() };
                case JsonTokenType.Number:
                    {
                        string raw = r.HasValueSequence
                            ? Encoding.UTF8.GetString(r.ValueSequence.ToArray())
                            : Encoding.UTF8.GetString(r.ValueSpan);
                        return new PreviewTree.TreeNode { t = "n", v = raw, l = LineOf(r.TokenStartIndex), m = TakePending() };
                    }
                case JsonTokenType.True:
                    return new PreviewTree.TreeNode { t = "b", v = "true", l = LineOf(r.TokenStartIndex), m = TakePending() };
                case JsonTokenType.False:
                    return new PreviewTree.TreeNode { t = "b", v = "false", l = LineOf(r.TokenStartIndex), m = TakePending() };
                case JsonTokenType.Null:
                    return new PreviewTree.TreeNode { t = "u", l = LineOf(r.TokenStartIndex), m = TakePending() };
                default:
                    throw new JsonException("Invalid JSON: unexpected token.");
            }
        }

        public void SkipValue()
        {
            if (r.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) return;
            int d = 1;
            while (d > 0 && r.Read())
            {
                if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) d++;
                else if (r.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) d--;
            }
        }

        public void SkipRest(JsonTokenType end)
        {
            int d = 0;
            while (r.Read())
            {
                if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) d++;
                else if (r.TokenType == end && d == 0) return;
                else if (r.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) d--;
            }
        }
    }

}
