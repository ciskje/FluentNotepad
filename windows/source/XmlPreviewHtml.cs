using System.Text.Json;
using System.Xml;
using static FluentNotepad.PreviewTree;

namespace FluentNotepad;

/// <summary>
/// XML (.xml/.config) -&gt; collapsible tree HTML for WebView2, on the same
/// PreviewTree schema as JSON: elements are objects, attributes are
/// `@name` leaves, text-only elements collapse to leaves, mixed text becomes
/// `#text` rows, XML comments ride as remarks. Copy paths are XPath
/// (`/catalog/book[2]/@id`), built in JS from an `__XML__` flag; copy of a
/// container rebuilds outer XML (comments excluded, stays valid). Positions
/// come from IXmlLineInfo (already 1-based). Large docs are capped like JSON.
/// </summary>
public static class XmlPreviewHtml
{
    public static string Build(string text, bool dark, double fontSize, string? title, ISet<string>? open)
    {
        if (text.Length > MaxChars)
            return PageHead(dark, fontSize, title ?? "XML") + TooLarge(MainWindow.Str("XmlTooLarge"));
        try
        {
            var root = Parse(text, out bool truncated);
            return TreePage(dark, fontSize, title ?? "XML",
                truncated ? MainWindow.Str("XmlTruncated") : null,
                JsonSerializer.Serialize(root),
                JsonSerializer.Serialize(open ?? Enumerable.Empty<string>()),
                xml: true);
        }
        catch (XmlException ex)
        {
            return ErrorPage(dark, fontSize, title ?? "XML",
                MainWindow.Str("XmlInvalidTitle"),
                string.Format(MainWindow.Str("XmlInvalid"), ex.LineNumber, ex.LinePosition),
                ex.Message, text, MainWindow.Str("XmlErrorHint"));
        }
    }

    // MARK: - Parse (streaming XmlReader, positions from IXmlLineInfo)

    private static PreviewTree.TreeNode Parse(string text, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(text)) throw new XmlException("The XML document is empty.");
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            DtdProcessing = DtdProcessing.Parse, // tolerate DOCTYPE; no resolver, nothing external loads
        };
        using var sr = new StringReader(text);
        using var xr = XmlReader.Create(sr, settings);
        var w = new Walker(xr);
        var root = new PreviewTree.TreeNode { t = "o", l = 1, c = new List<PreviewTree.TreeNode>() };
        PreviewTree.TreeNode? docEl = null;
        while (w.r.Read())
        {
            switch (w.r.NodeType)
            {
                case XmlNodeType.XmlDeclaration:
                    w.AteComment("<?" + w.r.Name + " " + w.r.Value + "?>");
                    break;
                case XmlNodeType.DocumentType:
                    w.AteComment("<!DOCTYPE " + w.r.Name + ">");
                    break;
                case XmlNodeType.Comment:
                    w.AteComment("<!--" + w.r.Value + "-->");
                    break;
                case XmlNodeType.ProcessingInstruction:
                    w.AteComment("<?" + w.r.Name + " " + w.r.Value + "?>");
                    break;
                case XmlNodeType.Element:
                    if (docEl != null) throw new XmlException("Extra content after the XML document.");
                    docEl = w.ReadElement(0);
                    root.c.Add(docEl);
                    w.Ate(docEl);
                    break;
                default:
                    break; // Whitespace (ignored), EntityReference (unresolvable), etc.
            }
        }
        if (docEl == null) throw new XmlException("The XML document is empty.");
        root.e = w.TakePending();
        truncated = w.trunc;
        return root;
    }

    private sealed class Walker
    {
        public readonly XmlReader r;
        public int count;
        public bool trunc;
        public readonly List<string> pending = new();
        public PreviewTree.TreeNode? last;
        public int lastLine;

        public Walker(XmlReader reader) { r = reader; }

        public int Line()
        {
            var li = (IXmlLineInfo)r;
            return li.HasLineInfo() ? li.LineNumber : 1;
        }

        public List<string>? TakePending()
        {
            if (pending.Count == 0) return null;
            var l = new List<string>(pending);
            pending.Clear();
            return l;
        }

        // Same-line comments trail the previous node; otherwise they lead the next.
        public void Ate(PreviewTree.TreeNode n) { last = n; lastLine = Line(); }

        public void AteComment(string c)
        {
            if (last != null && Line() == lastLine) { (last.e ??= new List<string>()).Add(c); }
            else pending.Add(c);
        }

        // Positioned on StartElement/EmptyElement. Consumes through its end.
        public PreviewTree.TreeNode ReadElement(int depth)
        {
            last = null;
            count++;
            if (count > MaxNodes || depth > MaxDepth)
            {
                trunc = true;
                pending.Clear();
                last = null;
                int l = Line();
                if (r.NodeType == XmlNodeType.Element && !r.IsEmptyElement) r.Skip();
                return new PreviewTree.TreeNode { t = "s", v = "…", l = l };
            }
            var node = new PreviewTree.TreeNode { t = "o", l = Line(), k = r.Name, c = new List<PreviewTree.TreeNode>(), m = TakePending() };
            bool empty = r.IsEmptyElement;
            if (r.HasAttributes)
            {
                while (r.MoveToNextAttribute())
                {
                    var at = new PreviewTree.TreeNode { t = "s", k = "@" + r.Name, v = r.Value, l = Line(), m = TakePending() };
                    node.c.Add(at);
                    Ate(at);
                }
                r.MoveToElement();
            }
            if (empty) { node.e = TakePending(); return node; }
            while (r.Read())
            {
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        {
                            var el = ReadElement(depth + 1);
                            node.c.Add(el);
                            Ate(el);
                            break;
                        }
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                        {
                            // Kept verbatim (even whitespace-only, like JSON ""):
                            // indentation between elements is already skipped by
                            // IgnoreWhitespace, so only real content arrives here.
                            var tn = new PreviewTree.TreeNode { t = "s", k = "#text", v = r.Value, l = Line(), m = TakePending() };
                            node.c.Add(tn);
                            Ate(tn);
                            break;
                        }
                    case XmlNodeType.Comment:
                        AteComment("<!--" + r.Value + "-->");
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        AteComment("<?" + r.Name + " " + r.Value + "?>");
                        break;
                    case XmlNodeType.EndElement:
                        CollapseText(node);
                        node.e = TakePending();
                        return node;
                    default:
                        break;
                }
                if (trunc && node.c.Count >= MaxKidsAfterTrunc)
                {
                    pending.Clear();
                    last = null;
                    SkipRest();
                    return node;
                }
            }
            throw new XmlException("Unexpected end of document.");
        }

        // Element with a single text child and no attributes reads as a leaf.
        private static void CollapseText(PreviewTree.TreeNode node)
        {
            if (node.c == null || node.c.Count != 1) return;
            var only = node.c[0];
            if (only.k != "#text" || only.t != "s") return;
            if (node.m == null) node.m = only.m;
            else if (only.m != null) node.m.AddRange(only.m);
            node.t = "s";
            node.v = only.v;
            node.c = null;
        }

        public void SkipRest()
        {
            int d = 0;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && !r.IsEmptyElement) d++;
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (d == 0) return;
                    d--;
                }
            }
        }
    }
}
