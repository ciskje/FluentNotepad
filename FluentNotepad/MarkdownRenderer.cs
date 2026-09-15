using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocList = System.Windows.Documents.List;

namespace FluentNotepad;

/// <summary>
/// Mini renderer Markdown -&gt; FlowDocument, senza dipendenze esterne.
/// Supporta: titoli ATX/Setext, grassetti/corsivi/b Barrati, codice inline,
/// blocchi di codice, citazioni, liste puntate/numerate annidate, checkbox,
/// hr, link, immagini (segnaposto), tabelle.
/// </summary>
public static class MarkdownRenderer
{
    private sealed class Palette
    {
        public Brush Fore = Brushes.Black;
        public Brush Subtle = Brushes.Gray;
        public Brush Border = Brushes.LightGray;
        public Brush CodeBg = Brushes.WhiteSmoke;
        public Brush QuoteBg = Brushes.WhiteSmoke;
        public Brush QuoteBorder = Brushes.Gray;
        public Brush TableHeadBg = Brushes.WhiteSmoke;
        public Brush Link = Brushes.Blue;
        public readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

        public static Palette Light() => new()
        {
            Fore = (Brush)new BrushConverter().ConvertFromString("#1B1B1B")!,
            Subtle = (Brush)new BrushConverter().ConvertFromString("#605E5C")!,
            Border = (Brush)new BrushConverter().ConvertFromString("#E1DFDD")!,
            CodeBg = (Brush)new BrushConverter().ConvertFromString("#F1F0EE")!,
            QuoteBg = (Brush)new BrushConverter().ConvertFromString("#F7F7F7")!,
            QuoteBorder = (Brush)new BrushConverter().ConvertFromString("#C8C6C4")!,
            TableHeadBg = (Brush)new BrushConverter().ConvertFromString("#F3F3F3")!,
            Link = (Brush)new BrushConverter().ConvertFromString("#0078D4")!,
        };

        public static Palette Dark() => new()
        {
            Fore = (Brush)new BrushConverter().ConvertFromString("#EAEAEA")!,
            Subtle = (Brush)new BrushConverter().ConvertFromString("#B0B0B0")!,
            Border = (Brush)new BrushConverter().ConvertFromString("#3A3A3A")!,
            CodeBg = (Brush)new BrushConverter().ConvertFromString("#262626")!,
            QuoteBg = (Brush)new BrushConverter().ConvertFromString("#2A2A2A")!,
            QuoteBorder = (Brush)new BrushConverter().ConvertFromString("#605E5C")!,
            TableHeadBg = (Brush)new BrushConverter().ConvertFromString("#2D2D2D")!,
            Link = (Brush)new BrushConverter().ConvertFromString("#60CDFF")!,
        };
    }

    private static readonly Regex InlineRx = new(
        @"(?<code>`(?<ccode>[^`]+?)`)" +
        @"|(?<bold>\*\*(?<b1>.+?)\*\*|__(?<b2>.+?)__)" +
        @"|(?<strike>~~(?<s>.+?)~~)" +
        @"|(?<italic>(?<!\*)\*(?!\*)(?<i1>[^*]+?)(?<!\*)\*(?!\*)|(?<!\w)_(?!_)(?<i2>[^_]+?)_(?!\w))" +
        @"|(?<img>!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)(?:\s+""[^""]*"")?\))" +
        @"|(?<link>\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\))" +
        @"|(?<auto><(?<aurl>https?://[^>\s]+)>)" +
        @"|(?<esc>\\(?<echar>[\\`*_{}\[\]()#+\-.!>]))",
        RegexOptions.Compiled);

    public static FlowDocument Render(string markdown, bool dark, double baseFontSize, string? baseDir = null)
    {
        var pal = dark ? Palette.Dark() : Palette.Light();
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = baseFontSize,
            Foreground = pal.Fore,
            PagePadding = new Thickness(0),
        };
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        foreach (var b in new Parser(lines, pal, baseFontSize, baseDir).Parse())
            doc.Blocks.Add(b);
        foreach (var b in doc.Blocks)
            if (b is Paragraph p && IsSingleImage(p))
                p.TextAlignment = TextAlignment.Center;
        return doc;
    }

    private static bool IsSingleImage(Paragraph p)
    {
        int n = 0;
        foreach (var inl in p.Inlines)
        {
            if (inl is LineBreak) continue;
            if (inl is InlineUIContainer) n++;
            else return false;
        }
        return n == 1;
    }

    // ================= INLINE =================
    private static List<Inline> ParseInlines(string text, Palette pal, double baseSize, string? baseDir)
    {
        var result = new List<Inline>();
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) AddText(result, text[pos..m.Index], pal, baseSize);
            var g = m.Groups;
            if (g["code"].Success)
            {
                result.Add(new Run(g["ccode"].Value)
                {
                    FontFamily = pal.Mono, FontSize = baseSize * 0.88, Background = pal.CodeBg
                });
            }
            else if (g["bold"].Success)
            {
                var b = new Bold();
                foreach (var inl in ParseInlines(g["b1"].Success ? g["b1"].Value : g["b2"].Value, pal, baseSize, baseDir))
                    b.Inlines.Add(inl);
                result.Add(b);
            }
            else if (g["strike"].Success)
            {
                var s = new Span { TextDecorations = TextDecorations.Strikethrough };
                foreach (var inl in ParseInlines(g["s"].Value, pal, baseSize, baseDir))
                    s.Inlines.Add(inl);
                result.Add(s);
            }
            else if (g["italic"].Success)
            {
                var it = new Italic();
                foreach (var inl in ParseInlines(g["i1"].Success ? g["i1"].Value : g["i2"].Value, pal, baseSize, baseDir))
                    it.Inlines.Add(inl);
                result.Add(it);
            }
            else if (g["img"].Success)
            {
                var alt = g["alt"].Value;
                var src = g["src"].Value.Trim();
                ImageSource? quick = null;
                try
                {
                    if (TryResolveLocalPath(src, baseDir, out var full))
                        quick = LoadBitmapCached(full);
                }
                catch { }
                result.Add(quick != null
                    ? new InlineUIContainer(MakeImage(quick, alt, baseSize))
                    : MakePendingImage(src, alt));
            }
            else if (g["link"].Success)
            {
                result.Add(MakeLink(ParseInlines(g["text"].Value, pal, baseSize, baseDir), g["url"].Value, pal));
            }
            else if (g["auto"].Success)
            {
                result.Add(MakeLink(new List<Inline> { new Run(g["aurl"].Value) }, g["aurl"].Value, pal));
            }
            else if (g["esc"].Success)
            {
                AddText(result, g["echar"].Value, pal, baseSize);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) AddText(result, text[pos..], pal, baseSize);
        return result;
    }

    private static Hyperlink MakeLink(List<Inline> inner, string url, Palette pal)
    {
        var h = new Hyperlink { Foreground = pal.Link, ToolTip = url };
        foreach (var inl in inner) h.Inlines.Add(inl);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            h.NavigateUri = uri;
            h.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
        }
        return h;
    }

    private static void AddText(List<Inline> acc, string text, Palette pal, double baseSize)
    {
        text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                   .Replace("&#39;", "'").Replace("&amp;", "&");
        if (text.Length > 0) acc.Add(new Run(text));
    }

    // ================= BLOCCHI =================
    private sealed class Parser
    {
        private readonly List<string> _lines;
        private readonly Palette _pal;
        private readonly double _base;
        private readonly string? _baseDir;
        private int _i;

        public Parser(List<string> lines, Palette pal, double baseSize, string? baseDir = null)
        {
            _lines = lines; _pal = pal; _base = baseSize; _baseDir = baseDir;
        }

        public List<Block> Parse()
        {
            var blocks = new List<Block>();
            while (_i < _lines.Count)
            {
                var b = ParseBlock();
                if (b != null) blocks.Add(b);
            }
            return blocks;
        }

        private static bool IsBlank(string l) => string.IsNullOrWhiteSpace(l);

        private Block? ParseBlock()
        {
            if (_i >= _lines.Count) return null;
            var line = _lines[_i];

            if (IsBlank(line)) { _i++; return null; }

            // Codice recintato ``` o ~~~
            var fence = Regex.Match(line, @"^\s{0,3}(`{3,}|~{3,})\s*([^\s`]*).*$");
            if (fence.Success) return ParseFenced(fence.Groups[1].Value, fence.Groups[2].Value);

            // Titolo ATX
            var atx = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$");
            if (atx.Success && atx.Groups[2].Value.Length > 0)
            {
                _i++;
                return Header(atx.Groups[1].Value.Length, atx.Groups[2].Value);
            }

            // Titolo Setext (riga successiva === o ---)
            if (_i + 1 < _lines.Count && IsSetext(_lines[_i + 1], out int slvl))
            {
                var t = line.Trim(); _i += 2;
                return Header(slvl, t);
            }

            // HR
            if (Regex.IsMatch(line, @"^\s{0,3}([-*_])(\s*\1){2,}\s*$")) { _i++; return Hr(); }

            // Citazione
            if (Regex.IsMatch(line, @"^\s{0,3}>")) return ParseQuote();

            // Tabella (| ... | + riga di ---)
            if (line.Contains('|') && _i + 1 < _lines.Count && IsDelimRow(_lines[_i + 1]))
                return ParseTable();

            // Lista
            if (IsListItem(line, out _, out _, out _, out _)) return ParseList();

            // Codice indentato (dopo riga vuota)
            if ((_i == 0 || IsBlank(_lines[_i - 1])) && (line.StartsWith("    ") || line.StartsWith("\t")))
                return ParseIndentedCode();

            // Paragrafo
            return ParseParagraph();
        }

        // ---------- helper di riconoscimento ----------
        private static bool IsSetext(string l, out int level)
        {
            level = 0;
            if (Regex.IsMatch(l, @"^\s*=+\s*$")) { level = 1; return true; }
            if (Regex.IsMatch(l, @"^\s*-+\s*$")) { level = 2; return true; }
            return false;
        }

        private static bool IsDelimRow(string l) =>
            Regex.IsMatch(l, @"^\s*\|?(\s*:?-+:?\s*\|)+\s*:?-+:?\s*\|?\s*$") && l.Contains('-');

        private bool StartsTable(int j) =>
            j < _lines.Count && _lines[j].Contains('|') &&
            j + 1 < _lines.Count && IsDelimRow(_lines[j + 1]);

        private bool StartsBlock(int j)
        {
            if (j >= _lines.Count || IsBlank(_lines[j])) return true;
            var l = _lines[j];
            if (Regex.IsMatch(l, @"^\s{0,3}(`{3,}|~{3,})")) return true;
            if (Regex.IsMatch(l, @"^\s{0,3}#{1,6}\s+")) return true;
            if (Regex.IsMatch(l, @"^\s{0,3}([-*_])(\s*\1){2,}\s*$")) return true;
            if (Regex.IsMatch(l, @"^\s{0,3}>")) return true;
            if (IsListItem(l, out _, out _, out _, out _)) return true;
            if (StartsTable(j)) return true;
            return false;
        }

        private static bool IsListItem(string l, out int indent, out bool ordered, out int number, out string content)
        {
            indent = 0; ordered = false; number = 1; content = "";
            var m = Regex.Match(l, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (!m.Success) return false;
            var lead = m.Groups[1].Value;
            foreach (var c in lead) indent += c == '\t' ? 4 - (indent % 4) : 1;
            var marker = m.Groups[2].Value;
            ordered = char.IsDigit(marker[0]);
            if (ordered) int.TryParse(Regex.Match(marker, @"\d+").Value, out number);
            content = m.Groups[3].Value;
            return true;
        }

        private Paragraph Para(string text, double sizeMul = 1, bool bold = false, Brush? fore = null)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var inl in ParseInlines(text, _pal, _base * sizeMul, _baseDir))
            {
                if (bold && inl is Run r) r.FontWeight = FontWeights.Bold;
                p.Inlines.Add(inl);
            }
            if (sizeMul != 1) p.FontSize = _base * sizeMul;
            if (bold) p.FontWeight = FontWeights.Bold;
            if (fore != null) p.Foreground = fore;
            return p;
        }

        private Block Header(int level, string text)
        {
            double[] sizes = [1.8, 1.5, 1.3, 1.15, 1.0, 0.9];
            var p = Para(text, sizes[Math.Min(level, 6) - 1], bold: true,
                         fore: level == 6 ? _pal.Subtle : null);
            p.Margin = new Thickness(0, level <= 2 ? 16 : 12, 0, 4);
            if (level <= 2)
            {
                p.BorderBrush = _pal.Border;
                p.BorderThickness = new Thickness(0, 0, 0, 1);
                p.Padding = new Thickness(0, 0, 0, 4);
            }
            return p;
        }

        private Block Hr()
        {
            return new BlockUIContainer(new System.Windows.Controls.Border
            {
                Height = 1, Background = _pal.Border, Margin = new Thickness(0, 10, 0, 10)
            });
        }

        private Block CodeBlock(List<string> codeLines, string? lang)
        {
            var section = new Section { Margin = new Thickness(0, 0, 0, 8) };
            if (!string.IsNullOrEmpty(lang))
                section.Blocks.Add(new Paragraph(new Run(lang))
                {
                    Foreground = _pal.Subtle, FontSize = _base * 0.75, Margin = new Thickness(0, 0, 0, 2)
                });
            var p = new Paragraph(new Run(string.Join("\n", codeLines)))
            {
                FontFamily = _pal.Mono, FontSize = _base * 0.9, Background = _pal.CodeBg,
                BorderBrush = _pal.Border, BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Margin = new Thickness(0)
            };
            section.Blocks.Add(p);
            return section;
        }

        private Block ParseFenced(string fence, string lang)
        {
            char fc = fence[0];
            _i++;
            var code = new List<string>();
            while (_i < _lines.Count && !Regex.IsMatch(_lines[_i], @"^\s{0,3}" + fc + @"{3,}\s*$"))
                code.Add(_lines[_i++]);
            if (_i < _lines.Count) _i++; // chiudi fence
            return CodeBlock(code, string.IsNullOrEmpty(lang) ? null : lang);
        }

        private Block ParseIndentedCode()
        {
            var code = new List<string>();
            while (_i < _lines.Count && (_lines[_i].StartsWith("    ") || _lines[_i].StartsWith("\t")))
            {
                var l = _lines[_i];
                code.Add(l.StartsWith("\t") ? l[1..] : l.Length >= 4 ? l[4..] : l);
                _i++;
            }
            return CodeBlock(code, null);
        }

        private Block ParseParagraph()
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            while (_i < _lines.Count && !IsBlank(_lines[_i]) && !StartsBlock(_i) &&
                   (_i + 1 >= _lines.Count || !IsSetext(_lines[_i + 1], out _)))
            {
                var l = _lines[_i];
                bool hard = l.EndsWith("  ") || l.EndsWith("\\");
                var t = hard && l.EndsWith("\\") ? l[..^1] : l.TrimEnd();
                if (!first) sb.Append(hard ? "\n" : " ");
                sb.Append(t);
                if (hard && !l.EndsWith("\\")) { /* soft: spazio già gestito sotto */ }
                first = false;
                _i++;
                if (hard) { sb.Append('\n'); }
            }
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var part in sb.ToString().Split('\n'))
            {
                foreach (var inl in ParseInlines(part.TrimEnd(), _pal, _base, _baseDir))
                    p.Inlines.Add(inl);
                p.Inlines.Add(new LineBreak());
            }
            // rimuovi ultimo LineBreak superfluo
            if (p.Inlines.LastInline is LineBreak lb) p.Inlines.Remove(lb);
            return p;
        }

        private Block ParseQuote()
        {
            var inner = new List<string>();
            while (_i < _lines.Count && Regex.IsMatch(_lines[_i], @"^\s{0,3}>"))
            {
                inner.Add(Regex.Replace(_lines[_i], @"^\s{0,3}>\s?", ""));
                _i++;
            }
            var blocks = new Parser(inner, _pal, _base, _baseDir).Parse();
            var section = new Section { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var b in blocks)
            {
                if (b is Paragraph qp)
                {
                    qp.BorderBrush = _pal.QuoteBorder;
                    qp.BorderThickness = new Thickness(3, 0, 0, 0);
                    qp.Padding = new Thickness(10, 4, 4, 4);
                    qp.Background = _pal.QuoteBg;
                    qp.Margin = new Thickness(0, 0, 0, 4);
                }
                else if (b is DocList ql) ql.Margin = new Thickness(16, 0, 0, 4);
                section.Blocks.Add(b);
            }
            return section;
        }

        private Block ParseList()
        {
            DocList? root = null;
            var stack = new Stack<(int indent, DocList list, ListItem last)>();
            Paragraph? open = null;

            while (_i < _lines.Count)
            {
                var line = _lines[_i];
                if (IsBlank(line))
                {
                    int j = _i;
                    while (j < _lines.Count && IsBlank(_lines[j])) j++;
                    if (j < _lines.Count && IsListItem(_lines[j], out _, out _, out _, out _))
                    { _i = j; open = null; continue; }
                    break;
                }
                if (IsListItem(line, out int indent, out bool ordered, out int num, out string content))
                {
                    while (stack.Count > 0 && indent < stack.Peek().indent) stack.Pop();
                    DocList cur;
                    if (stack.Count == 0)
                    {
                        cur = NewList(ordered, num); root = cur;
                        stack.Push((indent, cur, null!));
                    }
                    else if (indent > stack.Peek().indent)
                    {
                        cur = NewList(ordered, num);
                        stack.Peek().last.Blocks.Add(cur);
                        stack.Push((indent, cur, null!));
                    }
                    else cur = stack.Peek().list;

                    var item = new ListItem();
                    var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    var cm = Regex.Match(content, @"^\[([ xX])\]\s+(.*)$");
                    if (cm.Success)
                    {
                        para.Inlines.Add(new Run(cm.Groups[1].Value == " " ? "☐ " : "☑ ")
                        { Foreground = _pal.Subtle });
                        content = cm.Groups[2].Value;
                    }
                    foreach (var inl in ParseInlines(content, _pal, _base, _baseDir))
                        para.Inlines.Add(inl);
                    item.Blocks.Add(para);
                    cur.ListItems.Add(item);
                    var top = stack.Pop();
                    stack.Push((top.indent, top.list, item));
                    open = para;
                    _i++;
                }
                else if (open != null && !StartsBlock(_i))
                {
                    var t = line.Trim();
                    if (t.EndsWith("  ")) open.Inlines.Add(new LineBreak());
                    else open.Inlines.Add(new Run(" " + t));
                    _i++;
                }
                else break;
            }
            if (root != null) root.Margin = new Thickness(0, 0, 0, 8);
            return root is null ? new Paragraph() : root;
        }

        private static DocList NewList(bool ordered, int start)
        {
            var l = new DocList
            {
                MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(16, 0, 0, 4)
            };
            if (ordered) l.StartIndex = Math.Max(1, start);
            return l;
        }

        private static List<string> SplitRow(string l)
        {
            var t = l.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|')) t = t[..^1];
            return Regex.Split(t, @"(?<!\\)\|").Select(c => c.Replace("\\|", "|").Trim()).ToList();
        }

        private Block ParseTable()
        {
            var head = SplitRow(_lines[_i]);
            var delims = SplitRow(_lines[_i + 1]);
            int cols = Math.Max(head.Count, delims.Count);
            var aligns = new TextAlignment[cols];
            for (int c = 0; c < cols; c++)
            {
                var d = c < delims.Count ? delims[c] : "---";
                aligns[c] = d.StartsWith(':') && d.EndsWith(':') ? TextAlignment.Center
                          : d.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left;
            }
            _i += 2;

            var table = new Table
            {
                BorderBrush = _pal.Border, BorderThickness = new Thickness(1, 1, 0, 0),
                CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8)
            };
            for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            var hr = new TableRow { Background = _pal.TableHeadBg };
            for (int c = 0; c < cols; c++)
            {
                var cell = TableCell(c < head.Count ? head[c] : "", true, aligns[c]);
                hr.Cells.Add(cell);
            }
            group.Rows.Add(hr);

            while (_i < _lines.Count && !IsBlank(_lines[_i]) && _lines[_i].Contains('|'))
            {
                var cells = SplitRow(_lines[_i]);
                var row = new TableRow();
                for (int c = 0; c < cols; c++)
                    row.Cells.Add(TableCell(c < cells.Count ? cells[c] : "", false, aligns[c]));
                group.Rows.Add(row);
                _i++;
            }
            table.RowGroups.Add(group);
            return table;
        }

        private TableCell TableCell(string text, bool header, TextAlignment align)
        {
            var p = new Paragraph { Margin = new Thickness(0), TextAlignment = align };
            foreach (var inl in ParseInlines(text, _pal, _base, _baseDir))
            {
                if (header && inl is Run r) r.FontWeight = FontWeights.Bold;
                p.Inlines.Add(inl);
            }
            if (header) p.FontWeight = FontWeights.Bold;
            return new TableCell(p)
            {
                Padding = new Thickness(6, 4, 6, 4),
                BorderBrush = _pal.Border,
                BorderThickness = new Thickness(0, 0, 1, 1)
            };
        }
    }

    // ================= IMMAGINI =================
    /// <summary>Segnaposto di un'immagine in attesa di caricamento (remota, data-URI o non trovata).</summary>
    public sealed record PendingImage(string Src, string Alt);

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, ImageSource> _remoteCache = new();
    private static readonly Dictionary<string, (ImageSource Img, DateTime Write)> _fileCache = new();
    private const long MaxImageBytes = 20L * 1024 * 1024;

    private static InlineUIContainer MakePendingImage(string src, string alt)
    {
        var label = string.IsNullOrWhiteSpace(alt) ? "Immagine" : alt;
        var tb = new TextBlock(new Run("\U0001F5BC " + label + "…"))
        {
            FontStyle = FontStyles.Italic,
            Foreground = SystemColors.GrayTextBrush
        };
        return new InlineUIContainer(tb) { Tag = new PendingImage(src, alt) };
    }

    private static Image MakeImage(ImageSource src, string alt, double baseSize)
    {
        return new Image
        {
            Source = src,
            MaxWidth = Math.Max(320, baseSize * 40),
            Stretch = Stretch.Uniform,
            ToolTip = string.IsNullOrWhiteSpace(alt) ? "Immagine" : alt,
            Margin = new Thickness(0, 4, 0, 4)
        };
    }

    /// <summary>Toglie query (?raw=true) e frammenti (#center) dal percorso: su disco non fanno parte del nome file.</summary>
    private static string StripQueryFragment(string s)
    {
        int q = s.IndexOf('?');
        int h = s.IndexOf('#');
        int cut = q >= 0 && h >= 0 ? Math.Min(q, h) : q >= 0 ? q : h;
        return cut >= 0 ? s[..cut] : s;
    }

    /// <summary>github.com/.../blob/.../img.png?raw=true -&gt; raw.githubusercontent.com (altrimenti si scarica HTML).</summary>
    private static string NormalizeImageUrl(string src)
    {
        var m = Regex.Match(src,
            @"^https?://github\.com/(?<o>[^/]+)/(?<r>[^/]+)/blob/(?<b>[^/]+)/(?<p>.+)$",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var path = StripQueryFragment(m.Groups["p"].Value);
            return $"https://raw.githubusercontent.com/{m.Groups["o"].Value}/{m.Groups["r"].Value}/{m.Groups["b"].Value}/{path}";
        }
        return src;
    }

    private static bool TryResolveLocalPath(string src, string? baseDir, out string full)
    {
        full = "";
        var s = src.Trim();
        if (s.StartsWith("<") && s.EndsWith(">") && s.Length > 2) s = s[1..^1];
        s = StripQueryFragment(s);
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { s = Uri.UnescapeDataString(s); } catch { }
        try
        {
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                full = new Uri(s).LocalPath;
                return true;
            }
            if (Path.IsPathRooted(s)) { full = s; return true; }
            if (baseDir != null) { full = Path.GetFullPath(Path.Combine(baseDir, s)); return true; }
        }
        catch { }
        return false;
    }

    private static bool IsSvg(string pathOrSrc) =>
        pathOrSrc.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        pathOrSrc.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase);

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path) || IsSvg(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static ImageSource? LoadBitmapCached(string path)
    {
        try
        {
            if (!File.Exists(path) || IsSvg(path)) return null;
            var w = File.GetLastWriteTimeUtc(path);
            lock (_fileCache)
            {
                if (_fileCache.TryGetValue(path, out var e) && e.Write == w)
                    return e.Img;
            }
            var bi = LoadBitmap(path);
            if (bi == null) return null;
            lock (_fileCache)
            {
                if (_fileCache.Count > 200) _fileCache.Clear();
                _fileCache[path] = (bi, w);
            }
            return bi;
        }
        catch { return null; }
    }

    private static ImageSource? LoadDataUri(string src)
    {
        try
        {
            var m = Regex.Match(src, @"^data:(?<mime>image/[a-zA-Z0-9.+-]+)?;base64,(?<data>.+)$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!m.Success || IsSvg(src)) return null;
            var bytes = Convert.FromBase64String(m.Groups["data"].Value.Trim());
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
            return BytesToImage(bytes);
        }
        catch { return null; }
    }

    private static ImageSource? BytesToImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = ms;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is > MaxImageBytes) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > MaxImageBytes ? null : bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<(ImageSource? Img, string? Error)> ResolveImageAsync(string src, string? baseDir, CancellationToken ct)
    {
        try
        {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var bi = LoadDataUri(src);
                return bi != null ? (bi, null) : (null, "dati immagine non validi");
            }
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var url = NormalizeImageUrl(src);
                ImageSource? cached;
                lock (_remoteCache) { _remoteCache.TryGetValue(url, out cached); }
                if (cached != null) return (cached, null);
                var bytes = await DownloadAsync(url, ct);
                if (bytes == null) return (null, "scaricamento fallito");
                var bi = BytesToImage(bytes);
                if (bi == null) return (null, "formato non supportato");
                lock (_remoteCache)
                {
                    if (_remoteCache.Count > 100) _remoteCache.Clear();
                    _remoteCache[url] = bi;
                }
                return (bi, null);
            }
            if (TryResolveLocalPath(src, baseDir, out var full))
            {
                if (IsSvg(StripQueryFragment(src))) return (null, "SVG non supportato");
                var bi = LoadBitmapCached(full);
                return bi != null ? (bi, null) : (null, "file non trovato");
            }
            return (null, "percorso non valido");
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, "errore di caricamento"); }
    }

    /// <summary>Carica le immagini in sospeso del documento (remote/data-URI/mancanti) e le sostituisce al segnaposto.</summary>
    public static async Task HydrateImagesAsync(FlowDocument doc, string? baseDir, double baseSize, CancellationToken ct)
    {
        var targets = new List<InlineUIContainer>();
        CollectPending(doc.Blocks, targets);
        if (targets.Count == 0) return;
        var disp = doc.Dispatcher;
        foreach (var c in targets)
        {
            if (ct.IsCancellationRequested) return;
            if (c.Tag is not PendingImage p) continue;
            (ImageSource? img, string? err) result;
            try { result = await ResolveImageAsync(p.Src, baseDir, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            disp.Invoke(() =>
            {
                if (result.img != null)
                    c.Child = MakeImage(result.img, p.Alt, baseSize);
                else
                {
                    var label = string.IsNullOrWhiteSpace(p.Alt) ? "Immagine" : p.Alt;
                    c.Child = new TextBlock(new Run($"\U0001F5BC {label} — non disponibile ({result.err})"))
                    {
                        FontStyle = FontStyles.Italic,
                        Foreground = SystemColors.GrayTextBrush
                    };
                }
            });
        }
    }

    private static void CollectPending(BlockCollection blocks, List<InlineUIContainer> acc)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p: ScanInlines(p.Inlines, acc); break;
                case Section s: CollectPending(s.Blocks, acc); break;
                case DocList l:
                    foreach (var item in l.ListItems) CollectPending(item.Blocks, acc);
                    break;
                case Table t:
                    foreach (var g in t.RowGroups)
                        foreach (var r in g.Rows)
                            foreach (var cell in r.Cells)
                                CollectPending(cell.Blocks, acc);
                    break;
            }
        }
    }

    private static void ScanInlines(InlineCollection inlines, List<InlineUIContainer> acc)
    {
        foreach (var inl in inlines)
        {
            if (inl is InlineUIContainer u && u.Tag is PendingImage) acc.Add(u);
            else if (inl is Span s) ScanInlines(s.Inlines, acc);
        }
    }
}
