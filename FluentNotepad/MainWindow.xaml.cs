using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FluentNotepad;

public partial class MainWindow : Window
{
    private const string OpenFilter =
        "Markdown (*.md)|*.md|File di testo (*.txt)|*.txt|Tutti i file (*.*)|*.*";

    private int _untitled = 0;
    private double _zoom = 1.0;
    private bool _dark = false;
    private string _lastFind = "";
    private readonly DispatcherTimer _previewTimer;
    private readonly SolidColorBrush _accentSoft =
        new(Color.FromArgb(0x33, 0x00, 0x78, 0xD4));

    private sealed class DocumentInfo
    {
        public TabItem Tab = null!;
        public TextBox Editor = null!;
        public RichTextBox? Preview;
        public string? Path;
        public bool IsMarkdown;
        public bool PreviewVisible;
        public CancellationTokenSource? ImageCts;
    }

    public MainWindow()
    {
        InitializeComponent();
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += PreviewTimer_Tick;
        DetectWindowsTheme();
        ApplyTheme(_dark);
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionInfo.Text = $"FluentNotepad {v?.ToString(3) ?? "?"} — demo Fluent Design in WPF (tab, Markdown, temi).";
        NewTab(null);
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Apertura da riga di comando: FluentNotepad.exe appunto.md
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (File.Exists(arg))
            {
                OpenFile(arg);
                var d = GetActiveDoc();
                if (d?.IsMarkdown == true) ShowPreview(d, true);
                break;
            }
        }
        GetActiveBox()?.Focus();
    }

    // ================= DOCUMENTI / TAB =================
    private DocumentInfo? GetActiveDoc() => (Tabs.SelectedItem as TabItem)?.Tag as DocumentInfo;
    private TextBox? GetActiveBox() => GetActiveDoc()?.Editor;
    private string? GetActivePath() => GetActiveDoc()?.Path;

    public static bool IsMarkdownFile(string? path) =>
        path != null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".mdown", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".mkd", StringComparison.OrdinalIgnoreCase));

    private void NewTab(string? path, string content = "")
    {
        _untitled++;
        var name = path != null ? System.IO.Path.GetFileName(path)
                 : $"Senza titolo {(_untitled == 1 ? "" : _untitled.ToString())}".Trim();

        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe UI Symbol"), FontSize = 8, Width = 24, Height = 24, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Chiudi scheda" };
        close.Click += (s, e) => { e.Handled = true; CloseTab((TabItem)((Button)s).Tag); };
        header.Children.Add(title);
        header.Children.Add(close);

        var box = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontSize = FontSlider?.Value ?? 15,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Top,
            AllowDrop = true,
            UndoLimit = 200
        };
        box.SetResourceReference(ForegroundProperty, "ForeBrush");

        var doc = new DocumentInfo { Editor = box, Path = path, IsMarkdown = IsMarkdownFile(path) };
        box.Tag = doc;
        box.TextChanged += (_, _) =>
        {
            if (box.Tag is DocumentInfo d)
            {
                MarkDirty(d.Tab);
                if (d.IsMarkdown && d.PreviewVisible) { _previewTimer.Stop(); _previewTimer.Start(); }
            }
            UpdateStatus();
        };
        box.SelectionChanged += (_, _) => UpdateStatus();
        box.Drop += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop)!) OpenFile(f); };
        box.PreviewDragOver += (s, e) => { e.Handled = true; e.Effects = DragDropEffects.Copy; };

        var tab = new TabItem { Header = header, Content = box, Tag = doc };
        doc.Tab = tab;
        close.Tag = tab;
        if (doc.IsMarkdown) SyncMarkdownUi(doc);

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateStatus();
        RefreshMdUi();
    }

    private void MarkDirty(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children[0] is TextBlock t && !t.Text.StartsWith("*"))
            t.Text = "*" + t.Text;
    }

    private void ClearDirty(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children[0] is TextBlock t && t.Text.StartsWith("*"))
            t.Text = t.Text.TrimStart('*');
    }

    private void CloseTab(TabItem? tab)
    {
        if (tab == null) return;
        if (tab.Header is StackPanel sp && sp.Children[0] is TextBlock t && t.Text.StartsWith("*"))
        {
            var r = MessageBox.Show($"Salvare le modifiche a {t.Text.TrimStart('*')}?", "FluentNotepad",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes && !SaveTab(tab)) return;
        }
        int i = Tabs.Items.IndexOf(tab);
        (tab.Tag as DocumentInfo)?.ImageCts?.Cancel();
        Tabs.Items.Remove(tab);
        if (Tabs.Items.Count == 0) NewTab(null);
        else Tabs.SelectedIndex = Math.Min(i, Tabs.Items.Count - 1);
    }

    // ================= ANTEPRIMA MARKDOWN =================
    private RichTextBox CreatePreviewBox() => new()
    {
        IsReadOnly = true,
        IsDocumentEnabled = true,
        IsReadOnlyCaretVisible = true,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(20, 12, 20, 12),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Visibility = Visibility.Collapsed,
        Document = new System.Windows.Documents.FlowDocument()
    };

    /// <summary>Aggiunge o rimuove il contenitore di anteprima in base al tipo di file.</summary>
    private void SyncMarkdownUi(DocumentInfo doc)
    {
        if (doc.IsMarkdown && doc.Preview == null)
        {
            doc.Tab.Content = null; // stacca l'editor
            var grid = new Grid();
            grid.Children.Add(doc.Editor);
            doc.Preview = CreatePreviewBox();
            grid.Children.Add(doc.Preview);
            doc.Tab.Content = grid;
            doc.Editor.Visibility = Visibility.Visible;
        }
        else if (!doc.IsMarkdown && doc.Preview != null)
        {
            doc.Tab.Content = null;
            doc.Preview = null;
            doc.PreviewVisible = false;
            doc.Editor.Visibility = Visibility.Visible;
            doc.Tab.Content = doc.Editor;
        }
        RefreshMdUi();
    }

    private int _renderSeq = 0;

    private void RenderPreview(DocumentInfo doc)
    {
        if (doc.Preview == null) return;
        doc.ImageCts?.Cancel();
        doc.ImageCts?.Dispose();
        var cts = new CancellationTokenSource();
        doc.ImageCts = cts;
        int seq = ++_renderSeq;
        Mouse.OverrideCursor = Cursors.Wait;
        string? baseDir = doc.Path != null ? Path.GetDirectoryName(doc.Path) : null;
        double size = (FontSlider?.Value ?? 15) * _zoom;
        try
        {
            doc.Preview.Document = MarkdownRenderer.Render(doc.Editor.Text, _dark, size, baseDir);
        }
        catch
        {
            var fb = new System.Windows.Documents.FlowDocument();
            fb.Blocks.Add(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run("Errore durante il rendering dell'anteprima.")));
            doc.Preview.Document = fb;
            if (seq == _renderSeq) Mouse.OverrideCursor = null;
            return;
        }
        _ = HydrateAndRestoreAsync(doc.Preview.Document, baseDir, size, cts.Token, seq);
    }

    /// <summary>Carica le immagini e toglie la clessidra (solo se non è partito un altro rendering nel frattempo).</summary>
    private async Task HydrateAndRestoreAsync(System.Windows.Documents.FlowDocument document, string? baseDir, double size, CancellationToken ct, int seq)
    {
        try { await MarkdownRenderer.HydrateImagesAsync(document, baseDir, size, ct); }
        catch (OperationCanceledException) { }
        catch { }
        finally { if (seq == _renderSeq) Mouse.OverrideCursor = null; }
    }

    private void RefreshVisiblePreviews()
    {
        foreach (TabItem t in Tabs.Items)
            if ((t.Tag as DocumentInfo) is { IsMarkdown: true, PreviewVisible: true } d)
                RenderPreview(d);
    }

    private void ShowPreview(DocumentInfo d, bool show)
    {
        if (!d.IsMarkdown || d.Preview == null) return;
        d.PreviewVisible = show;
        if (show) RenderPreview(d);
        d.Editor.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        d.Preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) d.Preview.Focus();
        else d.Editor.Focus();
        RefreshMdUi();
    }

    private void TogglePreview()
    {
        var d = GetActiveDoc();
        if (d?.IsMarkdown == true) ShowPreview(d, !d.PreviewVisible);
    }

    private void RefreshMdUi()
    {
        var d = GetActiveDoc();
        bool isMd = d?.IsMarkdown == true;
        MdToggle.Visibility = isMd ? Visibility.Visible : Visibility.Collapsed;
        FileTypeLabel.Text = isMd ? "Markdown" : "Testo normale";
        if (isMd && d != null)
        {
            StyleToggle(EditModeBtn, !d.PreviewVisible);
            StyleToggle(PreviewModeBtn, d.PreviewVisible);
        }
    }

    private void StyleToggle(Button b, bool selected)
    {
        b.Background = selected ? _accentSoft : Brushes.Transparent;
        b.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void EditMode_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d != null) ShowPreview(d, false);
    }

    private void PreviewMode_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d != null) ShowPreview(d, true);
    }

    private void PreviewTimer_Tick(object? s, EventArgs e)
    {
        _previewTimer.Stop();
        var d = GetActiveDoc();
        if (d is { IsMarkdown: true, PreviewVisible: true }) RenderPreview(d);
    }

    // ================= FILE =================
    private static string SaveFilter(bool md) => md
        ? "Markdown (*.md)|*.md|File di testo (*.txt)|*.txt|Tutti i file (*.*)|*.*"
        : "File di testo (*.txt)|*.txt|Markdown (*.md)|*.md|Tutti i file (*.*)|*.*";

    private bool SaveTab(TabItem tab)
    {
        if (tab.Tag is not DocumentInfo doc) return false;
        if (doc.Path == null)
        {
            var d = new SaveFileDialog
            {
                Filter = SaveFilter(doc.IsMarkdown),
                DefaultExt = doc.IsMarkdown ? "md" : "txt",
                FileName = doc.IsMarkdown ? "Senza titolo.md" : "Senza titolo.txt"
            };
            if (d.ShowDialog() != true) return false;
            doc.Path = d.FileName;
            if (tab.Header is StackPanel sp && sp.Children[0] is TextBlock t)
                t.Text = System.IO.Path.GetFileName(doc.Path);
            // Il tipo di file potrebbe essere cambiato (es. .txt -> .md)
            doc.IsMarkdown = IsMarkdownFile(doc.Path);
            SyncMarkdownUi(doc);
        }
        File.WriteAllText(doc.Path, doc.Editor.Text);
        ClearDirty(tab);
        return true;
    }

    private void OpenFile(string path)
    {
        try
        {
            // riusa scheda vuota
            if (Tabs.SelectedItem is TabItem cur && cur.Tag is DocumentInfo cd &&
                cd.Editor.Text == "" && cd.Path == null)
                Tabs.Items.Remove(cur);
            NewTab(path, File.ReadAllText(path));
            if (Tabs.SelectedItem is TabItem t) ClearDirty(t);
        }
        catch (Exception ex) { MessageBox.Show("Impossibile aprire il file:\n" + ex.Message); }
    }

    // ================= MENU =================
    private void ShowMenu(params (string label, string shortcut, Action action)[] items)
    {
        MenuPanel.Children.Clear();
        foreach (var (label, shortcut, action) in items)
        {
            if (label == "-") { MenuPanel.Children.Add(new Separator()); continue; }
            var b = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => { MenuPopup.IsOpen = false; action(); };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
            var sc = new TextBlock { Text = shortcut, FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(24, 0, 0, 0) };
            Grid.SetColumn(sc, 1); g.Children.Add(sc);
            b.Content = g;
            b.SetResourceReference(ForegroundProperty, "ForeBrush");
            MenuPanel.Children.Add(b);
        }
        MenuPopup.IsOpen = true;
    }

    private void OpenDialog()
    {
        var d = new OpenFileDialog { Filter = OpenFilter };
        if (d.ShowDialog() == true) OpenFile(d.FileName);
    }

    private void FileMenu_Click(object s, RoutedEventArgs e) => ShowMenu(
        ("Nuova scheda", "Ctrl+T", () => NewTab(null)),
        ("Apri…", "Ctrl+O", OpenDialog),
        ("-", "", () => { }),
        ("Salva", "Ctrl+S", () => { if (Tabs.SelectedItem is TabItem t) SaveTab(t); }),
        ("Salva con nome…", "Ctrl+Maiusc+S", () => { if ((Tabs.SelectedItem as TabItem)?.Tag is DocumentInfo doc) { doc.Path = null; SaveTab(doc.Tab); } }),
        ("Chiudi scheda", "Ctrl+W", () => CloseTab(Tabs.SelectedItem as TabItem)),
        ("-", "", () => { }),
        ("Esci", "Alt+F4", Close));

    private void EditMenu_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d?.PreviewVisible == true) ShowPreview(d, false); // la ricerca opera sull'editor
        var b = GetActiveBox(); if (b == null) return;
        ShowMenu(
            ("Annulla", "Ctrl+Z", () => { if (b.CanUndo) b.Undo(); }),
            ("Ripeti", "Ctrl+Y", () => { if (b.CanRedo) b.Redo(); }),
            ("-", "", () => { }),
            ("Taglia", "Ctrl+X", b.Cut),
            ("Copia", "Ctrl+C", b.Copy),
            ("Incolla", "Ctrl+V", b.Paste),
            ("-", "", () => { }),
            ("Trova…", "Ctrl+F", () => { FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); }),
            ("Seleziona tutto", "Ctrl+A", b.SelectAll));
    }

    private void ViewMenu_Click(object s, RoutedEventArgs e) => ShowMenu(
        ("Aumenta zoom", "Ctrl++", () => SetZoom(_zoom + 0.1)),
        ("Riduci zoom", "Ctrl+-", () => SetZoom(_zoom - 0.1)),
        ("Ripristina zoom", "Ctrl+0", () => SetZoom(1.0)),
        ("-", "", () => { }),
        ("Anteprima Markdown", "Ctrl+Maiusc+V", TogglePreview),
        ((_dark ? "Tema chiaro" : "Tema scuro"), "", () => ApplyTheme(!_dark)),
        ((WrapCheck?.IsChecked == true ? "Disattiva" : "Attiva") + " a capo automatico", "", () => { WrapCheck!.IsChecked = !(WrapCheck.IsChecked == true); ApplyWrap(); }));

    // ================= TROVA =================
    private void FindButton_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d?.PreviewVisible == true) ShowPreview(d, false);
        if (FindBar.Visibility == Visibility.Visible) FindBar.Visibility = Visibility.Collapsed;
        else { FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); }
    }

    private void FindClose_Click(object s, RoutedEventArgs e) => FindBar.Visibility = Visibility.Collapsed;

    private void FindBox_TextChanged(object s, TextChangedEventArgs e) { _lastFind = FindBox.Text; FindNext(false); }
    private void FindBox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) FindNext(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift)); if (e.Key == Key.Escape) FindBar.Visibility = Visibility.Collapsed; }
    private void FindNext_Click(object s, RoutedEventArgs e) => FindNext(false);
    private void FindPrev_Click(object s, RoutedEventArgs e) => FindNext(true);

    private void FindNext(bool up)
    {
        var b = GetActiveBox(); if (b == null || string.IsNullOrEmpty(FindBox.Text)) return;
        var text = b.Text; var q = FindBox.Text;
        int start = up ? Math.Max(0, b.SelectionStart - 1) : b.SelectionStart + b.SelectionLength;
        int i = up ? text.LastIndexOf(q, start, StringComparison.OrdinalIgnoreCase) : text.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && !up) i = text.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && up) i = text.LastIndexOf(q, text.Length - 1, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) { b.Select(i, q.Length); b.ScrollToLine(b.GetLineIndexFromCharacterIndex(i)); b.Focus(); }
    }

    // ================= ZOOM / STATUS =================
    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.5, 3.0);
        var b = GetActiveBox();
        if (b != null) b.FontSize = (FontSlider?.Value ?? 15) * _zoom;
        if (ZoomLabel != null) ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
        RefreshVisiblePreviews();
    }
    private void ZoomIn_Click(object s, RoutedEventArgs e) => SetZoom(_zoom + 0.1);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => SetZoom(_zoom - 0.1);

    private void UpdateStatus()
    {
        var b = GetActiveBox();
        if (b == null) return;
        var text = b.Text ?? "";
        CharCount.Text = $"{text.Length} caratteri";
        int caret = Math.Clamp(b.CaretIndex, 0, text.Length);
        int line = 1, col = 1;
        for (int k = 0; k < caret; k++)
        {
            if (text[k] == '\n') { line++; col = 1; }
            else col++;
        }
        LnCol.Text = $"Riga {line}, col. {col}";
    }

    // ================= BARRA FORMATTAZIONE =================
    private TextBox? EditableBox()
    {
        var d = GetActiveDoc();
        if (d == null) return null;
        if (d.PreviewVisible) ShowPreview(d, false);
        return d.Editor;
    }

    private void WrapSelection(string before, string after, string placeholder = "testo")
    {
        var b = EditableBox(); if (b == null) return;
        string sel = string.IsNullOrEmpty(b.SelectedText) ? placeholder : b.SelectedText;
        int start = b.SelectionStart;
        string replacement = before + sel + after;
        if (b.SelectionLength > 0) b.SelectedText = replacement;
        else
        {
            b.Text = b.Text.Insert(start, replacement);
            b.Select(start + before.Length, sel.Length);
        }
        b.Focus();
    }

    private void PrefixLines(string prefix)
    {
        var b = EditableBox(); if (b == null) return;
        int start = b.SelectionStart, len = b.SelectionLength;
        int lineStart = start;
        while (lineStart > 0 && b.Text[lineStart - 1] != '\n') lineStart--;
        int lineEnd = start + len;
        while (lineEnd < b.Text.Length && b.Text[lineEnd] != '\n') lineEnd++;
        var chunk = b.Text[lineStart..lineEnd];
        var lines = chunk.Split('\n');
        for (int k = 0; k < lines.Length; k++)
            lines[k] = lines[k].StartsWith(prefix) ? lines[k][prefix.Length..] : prefix + lines[k];
        b.Text = b.Text[..lineStart] + string.Join('\n', lines) + b.Text[lineEnd..];
        b.Select(lineStart, string.Join('\n', lines).Length);
        b.Focus();
    }

    private void FormatHeading_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        int caret = b.CaretIndex;
        int ls = caret;
        while (ls > 0 && b.Text[ls - 1] != '\n') ls--;
        var m = System.Text.RegularExpressions.Regex.Match(b.Text[ls..caret], @"^(#{1,6}\s+)");
        if (m.Success && m.Groups[1].Value.Length < 7) { /* già titolo: aggiungi un # */ }
        b.Text = b.Text.Insert(ls, "# ");
        b.CaretIndex = caret + 2;
        b.Focus();
    }
    private void FormatBold_Click(object s, RoutedEventArgs e) => WrapSelection("**", "**");
    private void FormatItalic_Click(object s, RoutedEventArgs e) => WrapSelection("*", "*");
    private void FormatStrike_Click(object s, RoutedEventArgs e) => WrapSelection("~~", "~~");
    private void FormatCode_Click(object s, RoutedEventArgs e) => WrapSelection("`", "`", "codice");
    private void FormatLink_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        string sel = string.IsNullOrEmpty(b.SelectedText) ? "testo" : b.SelectedText;
        int start = b.SelectionStart;
        string rep = $"[{sel}](https://)";
        if (b.SelectionLength > 0) b.SelectedText = rep;
        else { b.Text = b.Text.Insert(start, rep); }
        b.Select(start + 1, sel.Length);
        b.Focus();
    }
    private void FormatQuote_Click(object s, RoutedEventArgs e) => PrefixLines("> ");
    private void FormatBullets_Click(object s, RoutedEventArgs e) => PrefixLines("- ");
    private void FormatNumbers_Click(object s, RoutedEventArgs e) => PrefixLines("1. ");

    private void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        RefreshMdUi();
    }

    // ================= TEMA =================
    private void DetectWindowsTheme()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) _dark = v == 0;
        }
        catch { _dark = false; }
    }

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        var R = Application.Current.Resources;
        void Set(string k, string hex) => R[k] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        if (dark)
        {
            Set("AppBgBrush", "#202020"); Set("CardBrush", "#2B2B2B"); Set("LayerBrush", "#2D2D2D");
            Set("ForeBrush", "#FFFFFF"); Set("SubtleForeBrush", "#B0B0B0"); Set("BorderBrush", "#3A3A3A");
            Set("HoverBrush", "#1AFFFFFF"); Set("PressedBrush", "#2AFFFFFF");
            Set("SelectedTabBrush", "#2B2B2B"); Set("EditorBrush", "#282828"); Set("TitleBarBrush", "#202020");
            Set("MenuBarBrush", "#2B2B2B"); Set("StatusBarBrush", "#202020");
            ThemeButton.Content = "\uE706;";
        }
        else
        {
            Set("AppBgBrush", "#F3F3F3"); Set("CardBrush", "#FFFFFF"); Set("LayerBrush", "#FBFBFB");
            Set("ForeBrush", "#262626"); Set("SubtleForeBrush", "#605E5C"); Set("BorderBrush", "#E1DFDD");
            Set("HoverBrush", "#0F000000"); Set("PressedBrush", "#1A000000");
            Set("SelectedTabBrush", "#F9F1F8"); Set("EditorBrush", "#F9F9F9"); Set("TitleBarBrush", "#ECCFE3");
            Set("MenuBarBrush", "#F9F1F8"); Set("StatusBarBrush", "#F9F1F8");
            ThemeButton.Content = "\uE708;";
        }
        RefreshVisiblePreviews();
    }

    private void ThemeButton_Click(object s, RoutedEventArgs e) => ApplyTheme(!_dark);
    private void ThemeCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedIndex == 0) ApplyTheme(false);
        else if (ThemeCombo.SelectedIndex == 1) ApplyTheme(true);
        else DetectWindowsTheme();
        if (IsLoaded) ApplyTheme(_dark);
    }

    // ================= IMPOSTAZIONI / WRAP / FONT =================
    private void SettingsButton_Click(object s, RoutedEventArgs e) =>
        SettingsDrawer.Visibility = SettingsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Wrap_Changed(object s, RoutedEventArgs e) => ApplyWrap();
    private void ApplyWrap()
    {
        foreach (TabItem t in Tabs.Items)
            if ((t.Tag as DocumentInfo)?.Editor is TextBox b)
                b.TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }
    private void FontSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return; // evento scattato durante InitializeComponent
        SetZoom(_zoom);
    }

    // ================= FINESTRA / SHORTCUT =================
    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMax(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void MinButton_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object s, RoutedEventArgs e) => ToggleMax();
    private void ToggleMax()
    {
        bool max = WindowState != WindowState.Maximized;
        WindowState = max ? WindowState.Maximized : WindowState.Normal;
        MaxButton.Content = max ? "\uE923;" : "\uE922;";
        // massimizzata: niente margine/ombra/angoli (occupa l'area di lavoro)
        OuterBorder.Margin = max ? new Thickness(0) : new Thickness(16);
        OuterBorder.CornerRadius = max ? new CornerRadius(0) : new CornerRadius(8);
    }
    private void CloseButton_Click(object s, RoutedEventArgs e) => Close();
    private void NewTab_Click(object s, RoutedEventArgs e) => NewTab(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl) { base.OnKeyDown(e); return; }
        switch (e.Key)
        {
            case Key.T: NewTab(null); e.Handled = true; break;
            case Key.W: CloseTab(Tabs.SelectedItem as TabItem); e.Handled = true; break;
            case Key.N: NewTab(null); e.Handled = true; break;
            case Key.V when shift: TogglePreview(); e.Handled = true; break;
            case Key.O: OpenDialog(); e.Handled = true; break;
            case Key.S when !shift: if (Tabs.SelectedItem is TabItem t) SaveTab(t); e.Handled = true; break;
            case Key.S when shift:
                if ((Tabs.SelectedItem as TabItem)?.Tag is DocumentInfo doc) { doc.Path = null; SaveTab(doc.Tab); }
                e.Handled = true; break;
            case Key.F:
                { var d = GetActiveDoc(); if (d?.PreviewVisible == true) ShowPreview(d, false); }
                FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); e.Handled = true; break;
            case Key.Add:
            case Key.OemPlus: SetZoom(_zoom + 0.1); e.Handled = true; break;
            case Key.Subtract:
            case Key.OemMinus: SetZoom(_zoom - 0.1); e.Handled = true; break;
            case Key.D0: SetZoom(1.0); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        foreach (TabItem t in Tabs.Items.OfType<TabItem>().ToList())
        {
            if (t.Header is StackPanel sp && sp.Children[0] is TextBlock tb && tb.Text.StartsWith("*"))
            {
                Tabs.SelectedItem = t;
                var r = MessageBox.Show($"Salvare le modifiche a {tb.Text.TrimStart('*')}?", "FluentNotepad", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageBoxResult.Yes && !SaveTab(t)) { e.Cancel = true; return; }
            }
        }
        base.OnClosing(e);
    }
}
