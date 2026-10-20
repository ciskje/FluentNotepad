using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace FluentNotepad;

public sealed partial class MainWindow : Window
{
    private double _zoom = 1.0;
    private int _selStart;
    private int _selLen;
    private bool _dark = false;
    private bool _loading = true;
    private bool _webReady = false;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewTimer;
    private readonly SolidColorBrush _accentSoft =
        new(Windows.UI.Color.FromArgb(0x33, 0x00, 0x78, 0xD4));

    // Code-behind strings: read from the .resw files shipped next to the exe (Strings/<language>/).
    // No MRT/ResourceLoader: it needs package identity, missing in unpackaged apps
    // (FileNotFoundException in the constructor). x:Uid in XAML uses internal MRT and works.
    private static Dictionary<string, string>? _res;
    internal static string Str(string key)
    {
        try
        {
            // Same rule as the rest of the UI: English only on English systems
            _res ??= LoadResw(UiLang() == "en" ? "en-US" : "it-IT");
            if (_res.TryGetValue(key, out var s)) return s;
        }
        catch (Exception ex) { Debug.WriteLine($"Str({key}): {ex.Message}"); }
        return $"[{key}]";
    }

    private static Dictionary<string, string> LoadResw(string folder) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", folder, "Resources.resw"))
            .Root!.Elements("data")
            .Where(e => e.Attribute("name")?.Value != null && e.Element("value") != null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

    // UI language subtag from the Windows language list (Italian for anything but it/en).
    private static string UiLang() =>
        Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault()?.Split('-')[0] ?? "";
    private sealed class DocumentInfo
    {
        public TabViewItem Tab = null!;
        public RichEditBox Editor = null!;
        public string? Path;
        public bool IsMarkdown;
        public bool IsJson;
        public bool IsXml;
        public bool IsYaml;
        public bool IsToml;
        public bool IsIni;
        public bool IsCsv;
        public bool PreviewVisible;
        public bool CanPreview() => IsMarkdown || IsJson || IsXml || IsYaml || IsToml || IsIni || IsCsv;
        // Tree previews share the collapsible schema (JSON pointer + $. paths);
        // CSV has its own grid page. Markdown stands alone (Markdig body).
        public bool IsTree() => IsJson || IsXml || IsYaml || IsToml || IsIni;
        // Tree-preview state, per tab in memory: open node pointers + search text.
        // (XML uses index-based pointers; the same set works for both.)
        public HashSet<string> JsonOpen = new();
        public string JsonQuery = "";
        public bool Dirty;
        public string LineEnding = "Windows (CRLF)";
        public string Encoding = "UTF-8";
        // Edit-mode highlight cache (hash of the inputs, like FluentNotepadMac)
        // and text snapshot (RichEditBox fires TextChanged for formatting too).
        public string HighlightKey = "";
        public int TextLen = -1;
        public int TextHash;
    }

    public MainWindow()
    {
        // UI language (Italian/English) from the Windows language; Italian for anything else.
        // Before InitializeComponent: x:Uid values resolve at XAML load time.
        // Guarded: the setter once fail-fasted on a neutral tag, so the qualified form is used.
        try
        {
            if (UiLang() is not ("it" or "en"))
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "it-IT";
        }
        catch (Exception ex) { Debug.WriteLine($"Lang override: {ex.Message}"); }

        // WebView2 writes its data next to the exe by default: under Program Files (not writable)
        // it never initializes. Point it at LocalAppData — process-local only, not persisted.
        // (Read by WebView2 at first initialization, so it must be set here before anything else.)
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentNotepad", "WebView2"));

        InitializeComponent();

        // Extended title bar + system Mica (no P/Invoke: built in)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        RestoreWindowGeometry();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += App_Closing;
        AppWindow.Changed += App_Changed;

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(400);
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            var d = GetActiveDoc();
            if (d is { PreviewVisible: true } && d.CanPreview()) RenderPreview(d);
        };

        ToolTipService.SetToolTip(ZoomOutBtn, Str("ViewZoomOut.Text"));
        ToolTipService.SetToolTip(ZoomInBtn, Str("ViewZoomIn.Text"));

        // Ctrl++ / Ctrl+- on the main keyboard (187=OemPlus, 189=OemMinus).
        // Hidden: otherwise WinUI shows the first accelerator in a hover tooltip
        // over the whole window (Root is the scope) — shortcuts stay active.
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var plus = new KeyboardAccelerator { Key = (VirtualKey)187, Modifiers = VirtualKeyModifiers.Control };
        plus.Invoked += ZoomInAccel_Invoked;
        Root.KeyboardAccelerators.Add(plus);
        var minus = new KeyboardAccelerator { Key = (VirtualKey)189, Modifiers = VirtualKeyModifiers.Control };
        minus.Invoked += ZoomOutAccel_Invoked;
        Root.KeyboardAccelerators.Add(minus);
        // ESC flips Edit/Preview (both directions, like FluentNotepadMac).
        // The find box keeps its own Escape (closes the bar): yield when focused.
        // ContentDialogs live outside Root's tree, so their ESC is unaffected.
        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += EscapeAccel_Invoked;
        Root.KeyboardAccelerators.Add(esc);

        // Saved theme (light/dark/system): follows Windows by default
        var saved = LoadSettings();
        ThemeCombo.SelectedIndex = saved.Theme == "light" ? 0 : saved.Theme == "dark" ? 1 : 2;
        ApplyThemeSetting(saved.Theme);
        // Saved editor prefs (like macOS UserDefaults): wrap + font size
        WrapCheck.IsChecked = saved.Wrap;
        FontSlider.Value = Math.Clamp(saved.FontSize, 12, 24);
        NewTab(null);
        _loading = false;
        GetActiveBox()?.Focus(FocusState.Programmatic);
    }

    public void OpenFileFromArgs(string path)
    {
        try { OpenFile(path, File.ReadAllText(path)); }
        catch (Exception ex) { Debug.WriteLine($"OpenFileFromArgs: {ex.Message}"); }
    }

    // ================= DOCUMENTS / TABS =================
    private DocumentInfo? GetActiveDoc() => Tabs.SelectedItem as TabViewItem is { Tag: DocumentInfo d } ? d : null;
    private RichEditBox? GetActiveBox() => GetActiveDoc()?.Editor;

    // "Remembered" selection: clicking a button/menu moves focus and the
    // live RichEditBox state in the handler is unreliable — the cache is updated on every SelectionChanged.
    private (int Start, int Length) CachedSelection(RichEditBox b)
    {
        int textLen = GetEditorText(b).Length;
        int start = Math.Clamp(_selStart, 0, textLen);
        return (start, Math.Clamp(_selLen, 0, textLen - start));
    }

    // RichEditBox stores breaks as \r (plus a story-final \r): normalize to
    // \n so offsets match the Markdig spans 1:1 and find/status/toolbar work
    // on a single line-break style. Save converts back to the file's style.
    internal static string GetEditorText(RichEditBox b)
    {
        b.Document.GetText(TextGetOptions.None, out string raw);
        if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];
        return raw.Replace('\r', '\n');
    }

    private static (int Start, int Length) GetSelection(RichEditBox b)
    {
        var s = b.Document.Selection;
        return (s.StartPosition, Math.Max(0, s.EndPosition - s.StartPosition));
    }

    private static void SetSelection(RichEditBox b, int start, int length)
    {
        int textLen = GetEditorText(b).Length;
        start = Math.Clamp(start, 0, textLen);
        length = Math.Clamp(length, 0, textLen - start);
        b.Document.Selection.SetRange(start, start + length);
    }

    // Editor (\n) -> file bytes: restore the file's line-ending style.
    private static string ToSaveText(DocumentInfo doc) =>
        GetEditorText(doc.Editor).Replace("\n", doc.LineEnding.StartsWith("Unix") ? "\n" : "\r\n");

    private float EffectiveFontSize() => (float)((FontSlider?.Value ?? 15) * _zoom);
    private Windows.UI.Color BaseTextColor() =>
        _dark ? Colors.White : Windows.UI.Color.FromArgb(255, 38, 38, 38);
    // Same tones as ApplyTheme: the highlight paints this (never transparent,
    // which RichEdit renders black) so text and editor background match.
    private Windows.UI.Color BaseBgColor() =>
        _dark ? Windows.UI.Color.FromArgb(255, 40, 40, 40) : Colors.White;

    private bool _highlighting;
    private bool _composing; // IME composition: don't fight it (like the Mac skip on marked text)

    // Parser-driven Markdown colors in edit mode (like FluentNotepadMac):
    // runs from TextChanging (the EN_UPDATE equivalent), so the formatting
    // joins the user's own undo unit instead of becoming a separate undo step.
    private void RefreshHighlight(DocumentInfo d)
    {
        if (_highlighting || _composing) return;
        // Formatting applied while the control is outside the visual tree
        // doesn't render (foreground is lost): ShowActiveContent repaints on
        // hosting, skip the wasted pass here.
        if (d.Editor.Parent == null) return;
        string text = GetEditorText(d.Editor);
        string key = $"{text.Length}-{text.GetHashCode()}-{EffectiveFontSize()}-{_dark}-{d.IsMarkdown}-{d.IsJson}-{d.IsXml}-{d.IsYaml}-{d.IsToml}-{d.IsIni}";
        if (key == d.HighlightKey) return;
        d.HighlightKey = key;
        _highlighting = true;
        try
        {
            if (d.IsJson) JsonHighlight.Apply(d.Editor, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor());
            else if (d.IsXml) XmlHighlight.Apply(d.Editor, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor());
            else if (d.IsYaml) YamlHighlight.Apply(d.Editor, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor());
            else if (d.IsToml) TomlHighlight.Apply(d.Editor, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor());
            else if (d.IsIni) IniHighlight.Apply(d.Editor, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor());
            else MarkdownHighlight.Apply(d.Editor, d.IsMarkdown, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor());
        }
        catch (Exception ex) { Debug.WriteLine($"Highlight: {ex.Message}"); }
        finally { _highlighting = false; }
    }

    public static bool IsMarkdownFile(string? path) =>
        path != null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    public static bool IsJsonFile(string? path) =>
        path != null && (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase));

    public static bool IsXmlFile(string? path) =>
        path != null && (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".config", StringComparison.OrdinalIgnoreCase));

    public static bool IsYamlFile(string? path) =>
        path != null && (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));

    public static bool IsTomlFile(string? path) =>
        path != null && path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);

    public static bool IsIniFile(string? path)
    {
        if (path == null) return false;
        if (path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".properties", StringComparison.OrdinalIgnoreCase)) return true;
        // Extensionless dotfiles like .env / .env.local (checked by file name).
        string name = System.IO.Path.GetFileName(path);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCsvFile(string? path) =>
        path != null && (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(DocumentInfo d) =>
        d.Path != null ? System.IO.Path.GetFileName(d.Path) : Str("Untitled");

    private void RefreshHeader(DocumentInfo d) => d.Tab.Header = (d.Dirty ? "*" : "") + DisplayName(d);

    private static string DetectLineEnding(string text) =>
        text.Contains("\r\n") ? "Windows (CRLF)" : text.Contains('\n') ? "Unix (LF)" : "Windows (CRLF)";

    // BOM sniff on open, like macOS (Doc.swift): UTF-8, UTF-8 with BOM, UTF-16 LE/BE.
    // Save always writes UTF-8 without BOM, so this only describes the opened file.
    private static string DetectEncoding(string path)
    {
        try
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return "UTF-8 with BOM";
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return "UTF-16 LE";
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return "UTF-16 BE";
        }
        catch (Exception ex) { Debug.WriteLine($"Enc: {ex.Message}"); }
        return "UTF-8";
    }

    private void RefreshFileLabels()
    {
        var d = GetActiveDoc();
        if (LineEndLabel != null) LineEndLabel.Text = d?.LineEnding ?? "Windows (CRLF)";
        if (EncodingLabel != null) EncodingLabel.Text = d?.Encoding ?? "UTF-8";
    }

    private void NewTab(string? path, string content = "")
    {
        var box = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Cascadia Code, Consolas"),
            FontSize = (FontSlider?.Value ?? 15) * _zoom,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        // No hover/focus background flash: the template paints
        // TextControlBackgroundPointerOver/Focused over our editor tone.
        var noFlash = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBackgroundPointerOver"] = noFlash;
        box.Resources["TextControlBackgroundFocused"] = noFlash;
        box.Resources["TextControlBorderBrushPointerOver"] = noFlash;
        box.Resources["TextControlBorderBrushFocused"] = noFlash;
        // Unsaved tabs count as Markdown (like the Mac): preview toggle,
        // highlighting and .md save default work before the first save.
        // Save As to .txt flips it back (see SaveTabAsync).
        var doc = new DocumentInfo { Editor = box, Path = path, IsMarkdown = path == null || IsMarkdownFile(path), IsJson = path != null && IsJsonFile(path), IsXml = path != null && IsXmlFile(path), IsYaml = path != null && IsYamlFile(path), IsToml = path != null && IsTomlFile(path), IsIni = path != null && IsIniFile(path), IsCsv = path != null && IsCsvFile(path) };
        var tab = new TabViewItem { Tag = doc };
        doc.Tab = tab;
        RefreshHeader(doc);
        box.Tag = doc;
        box.TextChanging += (_, _) =>
        {
            if (box.Tag is DocumentInfo dd) RefreshHighlight(dd);
        };
        // Undo grouping (typing + highlight = one Ctrl+Z): TextChanging is too
        // late (the keystroke is already in its own unit), so the group opens
        // on KeyDown and closes on the resulting TextChanged. Unpaired
        // Begin/End calls are no-ops; empty groups are discarded by TOM.
        box.KeyDown += (_, _) =>
        {
            _flashGen++; // user took over: abort any caret-line flash
            try { box.Document.BeginUndoGroup(); }
            catch (Exception ex) { Debug.WriteLine($"UndoGroup: {ex.Message}"); }
        };
        box.LosingFocus += (_, _) =>
        {
            try { box.Document.EndUndoGroup(); }
            catch (Exception ex) { Debug.WriteLine($"UndoGroup: {ex.Message}"); }
        };
        box.TextChanged += (_, _) =>
        {
            if (_highlighting) return;
            if (box.Tag is not DocumentInfo d) return;
            try { box.Document.EndUndoGroup(); } // closes the KeyDown group (no-op if none)
            catch (Exception ex) { Debug.WriteLine($"UndoGroup: {ex.Message}"); }
            // TextChanged also fires for formatting-only changes: dirty the
            // tab only when the text itself changed.
            string t = GetEditorText(box);
            int h = t.GetHashCode();
            if (t.Length == d.TextLen && h == d.TextHash) return;
            d.TextLen = t.Length;
            d.TextHash = h;
            d.Dirty = true;
            _flashGen++; // real text change (not formatting): abort any caret-line flash
            RefreshHeader(d);
            RefreshSaveBtn();
            if (d.CanPreview() && d.PreviewVisible) { _previewTimer.Stop(); _previewTimer.Start(); }
            UpdateStatus();
        };
        box.SelectionChanged += (_, _) =>
        {
            var (s, l) = GetSelection(box);
            _selStart = s;
            _selLen = l;
            UpdateStatus();
        };
        // Snapshot when focus leaves the editor: the button/menu Click arrives later,
        // when the live selection state is no longer reliable
        box.LosingFocus += (_, _) =>
        {
            var (s, l) = GetSelection(box);
            _selStart = s;
            _selLen = l;
        };
        box.TextCompositionStarted += (_, _) => _composing = true;
        box.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            if (box.Tag is DocumentInfo dd) RefreshHighlight(dd);
        };
        // Formatting applied before first load doesn't render (and ReadBack
        // lies: the model looks colored while the screen stays white): repaint
        // on every hosting, when the control is actually loaded. Deferred by one
        // UI tick: painting RichEdit ranges synchronously inside the layout pass
        // fail-fasts intermittently (AV in ITextCharacterFormat at startup).
        box.Loaded += (_, _) =>
        {
            if (box.Tag is DocumentInfo dd)
            {
                dd.HighlightKey = "";
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (box.Tag is DocumentInfo d2) RefreshHighlight(d2);
                });
            }
        };
        // NOTE: text set AFTER the handlers are attached, so the first
        // highlight runs from TextChanging with the doc already in place
        box.Document.SetText(TextSetOptions.None, content);

        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        string init = GetEditorText(box);
        doc.TextLen = init.Length;
        doc.TextHash = init.GetHashCode();
        RefreshHighlight(doc);
        UpdateStatus();
        RefreshFileLabels();
        RefreshMdUi();
    }

    private enum SaveAction { Cancel, Save, Discard }

    private async Task<SaveAction> AskSaveAsync(DocumentInfo doc)
    {
        var dlg = new ContentDialog
        {
            Title = "FluentNotepad",
            Content = string.Format(Str("SaveContent"), DisplayName(doc)),
            PrimaryButtonText = Str("SavePrimary"),
            SecondaryButtonText = Str("SaveSecondary"),
            CloseButtonText = Str("SaveClose"),
            XamlRoot = Content.XamlRoot
        };
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.Primary)
            return await SaveTabAsync(doc) ? SaveAction.Save : SaveAction.Cancel;
        if (r == ContentDialogResult.Secondary) return SaveAction.Discard;
        return SaveAction.Cancel;
    }

    // NOTE: TabView does NOT remove the tab by itself: do it here (also applies to Cancel = don't remove)
    private async Task CloseTabAsync(TabViewItem item)
    {
        if (item.Tag is not DocumentInfo doc) return;
        if (doc.Dirty && await AskSaveAsync(doc) == SaveAction.Cancel) return;
        Tabs.TabItems.Remove(item);
    }

    private async void Tabs_Close(TabView sender, TabViewTabCloseRequestedEventArgs e)
    {
        if (e.Tab is TabViewItem item) await CloseTabAsync(item);
    }

    private bool _closeConfirmed = false;

    private async void App_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_closeConfirmed) { SaveWindowGeometry(); return; }
        var dirty = Tabs.TabItems.OfType<TabViewItem>()
            .Select(t => t.Tag as DocumentInfo).Where(d => d?.Dirty == true).ToList();
        if (dirty.Count == 0) { SaveWindowGeometry(); return; }
        e.Cancel = true;
        foreach (var d in dirty)
        {
            Tabs.SelectedItem = d!.Tab;
            if (await AskSaveAsync(d) == SaveAction.Cancel) return;
        }
        _closeConfirmed = true;
        SaveWindowGeometry();
        Close();
    }

    // Last non-maximized bounds: when maximized, AppWindow.Size/Position report the
    // monitor, not the restore bounds — keep a cache.
    private RectInt32 _restoreBounds = new(100, 100, 1000, 650);

    // ================= MARKDOWN PREVIEW (WebView2) =================
    private readonly SemaphoreSlim _webLock = new(1, 1);
    private string _lastWebError = "";
    // JSON tree HTML is served from a temp file (not NavigateToString, capped at
    // 2 MB): the payload can exceed the source size.
    private string? _treePreviewFile;
    private int _previewSeq;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _hookedCore;

    // "Busy" cursor (spinning ring) while the preview renders: set on render,
    // cleared on NavigationCompleted (which fires for every navigation).
    private void SetBusy(bool busy) => Root.SetBusy(busy);

    private async void RenderPreview(DocumentInfo doc)
    {
        if (!doc.CanPreview() || !doc.PreviewVisible) return;
        PreviewError.Visibility = Visibility.Collapsed;
        SetBusy(true);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await TryRenderAsync(doc)) return;
            if (!doc.PreviewVisible) { SetBusy(false); return; }
            if (attempt < 2) await Task.Delay(1500);
        }
        PreviewView.Visibility = Visibility.Collapsed;
        PreviewErrorText.Text = Str("PreviewError") + _lastWebError;
        PreviewError.Visibility = Visibility.Visible;
        SetBusy(false);
    }

    private async Task<bool> TryRenderAsync(DocumentInfo doc)
    {
        await _webLock.WaitAsync();
        try
        {
            if (!_webReady)
            {
                await PreviewView.EnsureCoreWebView2Async();
                _webReady = true;
                // Re-hook on every new instance: after a reset (_webReady=false) the old
                // CoreWebView2 is dead and its NavigationCompleted would never fire again,
                // leaving the busy cursor forever. The render is done once the page loads.
                var core = PreviewView.CoreWebView2;
                if (!ReferenceEquals(_hookedCore, core))
                {
                    _hookedCore = core;
                    core.NavigationCompleted += (_, _) => SetBusy(false);
                    core.NavigationStarting += Preview_NavigationStarting;
                    // Click-to-source (like FluentNotepadMac): the HTML carries
                    // data-src-line (see MarkdownHtml); a document-start script
                    // forwards the nearest one, jumping selects the source line.
                    // Links jump to source instead of navigating (same tradeoff
                    // as the Mac); a real text selection is left alone for copy.
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(
                        "document.addEventListener('click',function(e){try{" +
                        "if(window.getSelection&&!window.getSelection().isCollapsed)return;" +
                        "var t=e.target,el=(t&&t.closest)?t.closest('[data-src-line]'):null;" +
                        "if(el){e.preventDefault();window.chrome.webview.postMessage(el.getAttribute('data-src-line'));}" +
                        "}catch(err){}},false);");
                    core.WebMessageReceived += Preview_WebMessage;
                }
            }
            string? baseDir = doc.Path != null ? Path.GetDirectoryName(doc.Path) : null;
            double size = (FontSlider?.Value ?? 15) * _zoom;
            if (doc.IsTree() || doc.IsCsv)
            {
                string html;
                if (doc.IsCsv)
                    html = CsvPreviewHtml.Build(GetEditorText(doc.Editor), _dark, size, DisplayName(doc));
                else if (doc.IsJson)
                    html = JsonPreviewHtml.Build(GetEditorText(doc.Editor), _dark, size,
                        DisplayName(doc), doc.JsonOpen);
                else if (doc.IsXml)
                    html = XmlPreviewHtml.Build(GetEditorText(doc.Editor), _dark, size,
                        DisplayName(doc), doc.JsonOpen);
                else if (doc.IsYaml)
                    html = YamlPreviewHtml.Build(GetEditorText(doc.Editor), _dark, size,
                        DisplayName(doc), doc.JsonOpen);
                else if (doc.IsToml)
                    html = TomlPreviewHtml.Build(GetEditorText(doc.Editor), _dark, size,
                        DisplayName(doc), doc.JsonOpen);
                else
                    html = IniPreviewHtml.Build(GetEditorText(doc.Editor), _dark, size,
                        DisplayName(doc), doc.JsonOpen);
                string file = TreePreviewPath();
                File.WriteAllText(file, html);
                _treePreviewFile = file;
                // Query busts the cache: same file, new content must re-navigate.
                PreviewView.CoreWebView2.Navigate(new Uri(file).AbsoluteUri + "?v=" + (++_previewSeq));
            }
            else
            {
                string html = MarkdownHtml.Build(GetEditorText(doc.Editor), baseDir, _dark, size,
                    DisplayName(doc));
                PreviewView.NavigateToString(html);
            }
            PreviewView.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex) { _webReady = false; _lastWebError = ex.Message; SetBusy(false); return false; }
        finally { _webLock.Release(); }
    }

    private void PreviewRetry_Click(object s, RoutedEventArgs e)
    {
        if (GetActiveDoc() is { } d) RenderPreview(d);
    }

    // The temp file backing the tree previews (see windows/AGENTS.md): shared
    // per process, versioned by query. Allow-listed in Preview_NavigationStarting.
    private static string TreePreviewPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentNotepad", "preview");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"tree-preview-{Environment.ProcessId}.html");
    }

    private static void CopyText(string text)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch (Exception ex) { Debug.WriteLine($"Copy: {ex.Message}"); }
    }

    // Click-to-source, shared by both previews: leave preview, caret on the
    // line, blink it twice in Edit. The caret is placed BEFORE flashing:
    // FlashCaretLine blinks whatever line the caret is on, and aborts if the
    // selection moves mid-blink.
    private void JumpToLine(DocumentInfo d, int line)
    {
        string text = GetEditorText(d.Editor);
        int off = 0;
        for (int cur = 1; cur < line && off < text.Length; cur++)
        {
            int nl = text.IndexOf('\n', off);
            if (nl < 0) { off = text.Length; break; }
            off = nl + 1;
        }
        // Hide preview without flashing: the flash below must run on the
        // just-placed caret, otherwise it blinks the old line (or aborts).
        if (d.PreviewVisible) ShowPreview(d, false, flash: false);
        var b = d.Editor;
        SetSelection(b, off, 0);
        b.Focus(FocusState.Programmatic);
        FlashCaretLine(preDelayMs: 500, blinkMs: 250);
    }

    // Click-to-source for tree previews AND Markdown: jump straight to Edit
    // and blink the caret line twice there (the row to look at).
    // Aborts the jump if the user moved on meanwhile.
    private void Preview_WebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); }
        catch (Exception ex) { Debug.WriteLine($"WebMsg: {ex.Message}"); return; }
        // Tree-preview protocol (JSON + XML): {"op":"line"|"copy"|"state",...}.
        // Anything else falls through to the Markdown plain-line-number path.
        string t = msg.Trim();
        if (t.StartsWith("{"))
        {
            try
            {
                using var jdoc = JsonDocument.Parse(t);
                var root = jdoc.RootElement;
                if (root.TryGetProperty("op", out var op) && op.GetString() is string o)
                {
                    var d = GetActiveDoc();
                    bool isTree = d is { PreviewVisible: true } && (d.IsTree() || d.IsCsv);
                    if (o == "line" && root.TryGetProperty("line", out var ln) &&
                        ln.TryGetInt32(out int line) && line >= 1)
                    {
                        if (isTree && d != null)
                        {
                            try { JumpThenFlash(d, line); }
                            catch (Exception ex) { Debug.WriteLine($"Jump: {ex.Message}"); }
                        }
                    }
                    else if (o == "copy" && root.TryGetProperty("text", out var tx))
                    {
                        CopyText(tx.GetString() ?? "");
                    }
                    else if (o == "state" && isTree && d != null)
                    {
                        if (root.TryGetProperty("open", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            d.JsonOpen.Clear();
                            foreach (var x in arr.EnumerateArray())
                                if (x.GetString() is string s) d.JsonOpen.Add(s);
                        }
                        if (root.TryGetProperty("q", out var qq) && qq.ValueKind == JsonValueKind.String)
                            d.JsonQuery = qq.GetString() ?? "";
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"WebMsgJson: {ex.Message}"); }
            return;
        }
        if (!int.TryParse(t, out int mdLine) || mdLine < 1) return;
        var md = GetActiveDoc();
        if (md is not { IsMarkdown: true, PreviewVisible: true }) return;
        try { JumpThenFlash(md, mdLine); }
        catch (Exception ex) { Debug.WriteLine($"Jump: {ex.Message}"); }
    }

    // Jump straight to Edit and blink the caret line twice there (in place):
    // the jumped-to line is the one to look at, so the flash belongs to Edit,
    // not to the preview. Skips the jump if the user moved on meanwhile.
    private void JumpThenFlash(DocumentInfo d, int line)
    {
        var cur = GetActiveDoc();
        if (!ReferenceEquals(cur, d) || !d.PreviewVisible) return; // user moved on
        JumpToLine(d, line); // ShowPreview flips to Edit and flashes the line
    }

    // A file dropped onto the preview makes WebView2 navigate to it (like Edge),
    // before the XAML drop handlers ever see it: cancel that and open it as a
    // tab instead (like FluentNotepadMac). Our HTML has no file links (images
    // are data-URI), so any file:// navigation is a drop.
    private void Preview_NavigationStarting(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        try
        {
            string uri = e.Uri ?? "";
            if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return;
            string local;
            try { local = new Uri(uri).LocalPath; }
            catch { return; }
            // Our own tree preview page (temp file): let it load, never open as a tab.
            if (_treePreviewFile != null &&
                string.Equals(local, _treePreviewFile, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(local)) return;
            e.Cancel = true;
            // Deferred: we are inside the navigation callback, don't reshuffle tabs here.
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                try { OpenFile(local, File.ReadAllText(local)); }
                catch (Exception ex) { Debug.WriteLine($"DropNav: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Debug.WriteLine($"NavStarting: {ex.Message}"); }
    }

    private void ShowActiveContent()
    {
        PreviewError.Visibility = Visibility.Collapsed;
        var d = GetActiveDoc();
        if (d == null) { PreviewView.Visibility = Visibility.Collapsed; EditorHost.Children.Clear(); return; }
        if (d.PreviewVisible && d.CanPreview())
        {
            EditorHost.Children.Clear();
            PreviewView.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewView.Visibility = Visibility.Collapsed;
            // Don't re-add the editor if already hosted: re-parenting
            // resets selection (and scroll), and tags ended up at the top of the file
            if (EditorHost.Children.Count != 1 || !ReferenceEquals(EditorHost.Children[0], d.Editor))
            {
                EditorHost.Children.Clear();
                EditorHost.Children.Add(d.Editor);
            }
        }
    }

    private void ShowPreview(DocumentInfo d, bool show, bool flash = true)
    {
        if (!d.CanPreview()) return;
        d.PreviewVisible = show;
        if (show) RenderPreview(d);
        ShowActiveContent();
        RefreshMdUi();
        if (show) return;
        if (flash) FlashCaretLine();
    }

    // Caret-line flash (two blinks) when entering Edit: selection-only, so it
    // never touches the undo stack or RichEdit formatting (AV-safe, unlike a
    // paint). Any real edit, key press or tab switch aborts it via _flashGen.
    // Click-to-source jumps here too, so the jumped-to line blinks in Edit.
    // Optional pre-delay + blink length: the click path waits 2 s, then blinks
    // twice at half a second so the target line is easy to catch.
    private int _flashGen;

    private async void FlashCaretLine(int preDelayMs = 0, int blinkMs = 140)
    {
        var d = GetActiveDoc();
        var box = d?.Editor;
        if (d == null || box == null || d.PreviewVisible || box.Parent == null) return;
        int gen = ++_flashGen;
        if (preDelayMs > 0) await Task.Delay(preDelayMs);
        // The user may have moved on during the wait: revalidate everything
        // and blink whatever line the caret is on now.
        if (gen != _flashGen || !ReferenceEquals(GetActiveDoc(), d)) return;
        if (d.PreviewVisible || box.Parent == null) return;
        var (os, ol) = GetSelection(box);
        string text;
        try { text = GetEditorText(box); }
        catch (Exception ex) { Debug.WriteLine($"Flash: {ex.Message}"); return; }
        if (!TextUtil.TryGetLineRange(text, os, out int ls, out int len)) return;
        try
        {
            for (int k = 0; k < 2; k++)
            {
                if (gen != _flashGen || !ReferenceEquals(GetActiveDoc(), d)) return;
                SetSelection(box, ls, len);
                await Task.Delay(blinkMs);
                if (gen != _flashGen || !ReferenceEquals(GetActiveDoc(), d)) { SetSelection(box, os, ol); return; }
                var (cs, cl) = GetSelection(box);
                if (cs != ls || cl != len) return; // user moved on; keep their selection
                SetSelection(box, os, ol);
                await Task.Delay(blinkMs);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Flash: {ex.Message}"); }
    }

    private void TogglePreview()
    {
        var d = GetActiveDoc();
        if (d?.CanPreview() == true) ShowPreview(d, !d.PreviewVisible);
    }

    private void RefreshMdUi()
    {
        var d = GetActiveDoc();
        bool isMd = d?.IsMarkdown == true;
        bool isTree = (d?.IsTree() == true) || d?.IsCsv == true;
        bool isPv = isMd || isTree;
        MdToggle.Visibility = isPv ? Visibility.Visible : Visibility.Collapsed;
        FileTypeLabel.Text = isMd ? "Markdown"
            : d?.IsJson == true ? "JSON"
            : d?.IsXml == true ? "XML"
            : d?.IsYaml == true ? "YAML"
            : d?.IsToml == true ? "TOML"
            : d?.IsIni == true ? "INI"
            : d?.IsCsv == true ? "CSV" : Str("FileTypeLabel.Text");
        if (isPv && d != null)
        {
            StyleToggle(EditModeBtn, !d.PreviewVisible);
            StyleToggle(PreviewModeBtn, d.PreviewVisible);
        }
        RefreshSaveBtn();
    }

    // Quick-save toolbar button (like the Mac one): enabled only when dirty.
    private void RefreshSaveBtn() => SaveBtn.IsEnabled = GetActiveDoc()?.Dirty == true;

    private void StyleToggle(Button b, bool selected)
    {
        b.Background = selected ? _accentSoft : new SolidColorBrush(Colors.Transparent);
        b.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        b.Foreground = new SolidColorBrush(_dark ? Colors.White : Windows.UI.Color.FromArgb(255, 38, 38, 38));
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

    private void EscapeAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        try
        {
            if (ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot), FindBox))
                return; // FindBox_KeyDown closes the bar itself
        }
        catch (Exception ex) { Debug.WriteLine($"Esc focus: {ex.Message}"); }
        if (_composing) return; // IME composition owns the keystroke
        var d = GetActiveDoc();
        if (d?.CanPreview() == true)
        {
            // Tree/grid preview search owns ESC first: clear it, stay in preview.
            if (d.PreviewVisible && (d.IsTree() || d.IsCsv) && !string.IsNullOrEmpty(d.JsonQuery))
            {
                d.JsonQuery = "";
                try { _ = PreviewView.CoreWebView2?.ExecuteScriptAsync("if(window.__fnClearSearch)window.__fnClearSearch();"); }
                catch (Exception ex) { Debug.WriteLine($"ClearSearch: {ex.Message}"); }
                e.Handled = true;
                return;
            }
            ShowPreview(d, !d.PreviewVisible);
            e.Handled = true;
        }
    }

    private void PreviewToggle_Click(object s, RoutedEventArgs e) => TogglePreview();

    // ================= FILE =================
    private void InitPicker(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private async void OpenDialog()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        InitPicker(picker);
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".jsonc");
        picker.FileTypeFilter.Add(".xml");
        picker.FileTypeFilter.Add(".config");
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        picker.FileTypeFilter.Add(".toml");
        picker.FileTypeFilter.Add(".ini");
        picker.FileTypeFilter.Add(".cfg");
        picker.FileTypeFilter.Add(".inf");
        picker.FileTypeFilter.Add(".properties");
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".tsv");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            try { OpenFile(file.Path, await FileIO.ReadTextAsync(file)); }
            catch (Exception ex) { Debug.WriteLine($"Open: {ex.Message}"); }
        }
    }

    private async Task<bool> SaveTabAsync(DocumentInfo doc)
    {
        if (doc.Path == null)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            InitPicker(picker);
            if (doc.IsCsv)
            {
                picker.FileTypeChoices.Add(Str("CsvFilter"), new List<string> { ".csv", ".tsv" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".csv";
            }
            else if (doc.IsIni)
            {
                picker.FileTypeChoices.Add(Str("IniFilter"), new List<string> { ".ini", ".cfg", ".inf", ".properties" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".ini";
            }
            else if (doc.IsToml)
            {
                picker.FileTypeChoices.Add(Str("TomlFilter"), new List<string> { ".toml" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".toml";
            }
            else if (doc.IsYaml)
            {
                picker.FileTypeChoices.Add(Str("YamlFilter"), new List<string> { ".yaml", ".yml" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".yaml";
            }
            else if (doc.IsXml)
            {
                picker.FileTypeChoices.Add(Str("XmlFilter"), new List<string> { ".xml", ".config" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".xml";
            }
            else if (doc.IsJson)
            {
                picker.FileTypeChoices.Add(Str("JsonFilter"), new List<string> { ".json", ".jsonc" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".json";
            }
            else if (doc.IsMarkdown)
            {
                picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".md";
            }
            else
            {
                picker.FileTypeChoices.Add(Str("FileFilterText"), new List<string> { ".txt" });
                picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
                picker.SuggestedFileName = Str("Untitled");
                picker.DefaultFileExtension = ".txt";
            }
            var file = await picker.PickSaveFileAsync();
            if (file == null) return false;
            doc.Path = file.Path;
            doc.IsMarkdown = IsMarkdownFile(doc.Path);
            doc.IsJson = !doc.IsMarkdown && IsJsonFile(doc.Path);
            doc.IsXml = !doc.IsMarkdown && !doc.IsJson && IsXmlFile(doc.Path);
            doc.IsYaml = !doc.IsMarkdown && !doc.IsJson && !doc.IsXml && IsYamlFile(doc.Path);
            doc.IsToml = !doc.IsMarkdown && !doc.IsJson && !doc.IsXml && !doc.IsYaml && IsTomlFile(doc.Path);
            doc.IsIni = !doc.IsMarkdown && !doc.IsJson && !doc.IsXml && !doc.IsYaml && !doc.IsToml && IsIniFile(doc.Path);
            doc.IsCsv = !doc.IsMarkdown && !doc.IsJson && !doc.IsXml && !doc.IsYaml && !doc.IsToml && !doc.IsIni && IsCsvFile(doc.Path);
            RefreshHeader(doc);
            RefreshMdUi();
            RefreshHighlight(doc); // markdown-ness may have changed: recolor
            await FileIO.WriteTextAsync(file, ToSaveText(doc));
        }
        else
        {
            File.WriteAllText(doc.Path, ToSaveText(doc));
        }
        doc.Dirty = false;
        string saved = ToSaveText(doc);
        doc.LineEnding = DetectLineEnding(saved);
        doc.Encoding = "UTF-8"; // save always writes UTF-8 without BOM
        string cur = GetEditorText(doc.Editor);
        doc.TextLen = cur.Length;
        doc.TextHash = cur.GetHashCode();
        RefreshSaveBtn();
        RefreshHeader(doc);
        RefreshFileLabels();
        return true;
    }

    private void OpenFile(string path, string content)
    {
        // Already open: select its tab instead of duplicating (like Store.open on macOS).
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }
        foreach (var item in Tabs.TabItems)
        {
            if (item is TabViewItem t && t.Tag is DocumentInfo e && e.Path != null)
            {
                string ep;
                try { ep = Path.GetFullPath(e.Path); }
                catch { ep = e.Path; }
                if (string.Equals(ep, full, StringComparison.OrdinalIgnoreCase))
                {
                    Tabs.SelectedItem = t;
                    return;
                }
            }
        }
        if (Tabs.SelectedItem is TabViewItem cur && cur.Tag is DocumentInfo cd &&
            GetEditorText(cd.Editor) == "" && cd.Path == null && !cd.Dirty)
            Tabs.TabItems.Remove(cur);
        NewTab(path, content);
        if (GetActiveDoc() is { } d)
        {
            d.Dirty = false;
            d.LineEnding = DetectLineEnding(content);
            d.Encoding = DetectEncoding(path);
            RefreshHeader(d);
            RefreshFileLabels();
            // A previewable file opened from anywhere (association/command line,
            // File > Open, drag & drop) starts directly in Preview.
            if (d.CanPreview()) ShowPreview(d, true);
        }
    }

    private void NewTab_Click(object s, RoutedEventArgs e) => NewTab(null);
    private void NewWindow_Click(object s, RoutedEventArgs e) => (Application.Current as App)?.NewWindow();
    private void Tabs_AddTab(TabView s, object e) => NewTab(null);
    private void Open_Click(object s, RoutedEventArgs e) => OpenDialog();
    private async void Save_Click(object s, RoutedEventArgs e)
    {
        if (GetActiveDoc() is { } d) await SaveTabAsync(d);
    }
    private async void SaveAs_Click(object s, RoutedEventArgs e)
    {
        if (GetActiveDoc() is { } d) { d.Path = null; await SaveTabAsync(d); }
    }
    private async void CloseTab_Click(object s, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem item) await CloseTabAsync(item);
    }

    private void Exit_Click(object s, RoutedEventArgs e) => Close();

    // Help > Keyboard shortcuts: static cheatsheet, same rows as
    // FluentNotepadMac ShortcutsHelpView (labels via Str, glyphs literal).
    private async void Shortcuts_Click(object s, RoutedEventArgs e)
    {
        (string Key, string Glyph)[] rows =
        [
            ("FileNewTab.Text", "Ctrl+T"),
            ("FileOpen.Text", "Ctrl+O"),
            ("FileNewWindow.Text", "Ctrl+Shift+N"),
            ("FileSave.Text", "Ctrl+S"),
            ("FileSaveAs.Text", "Ctrl+Shift+S"),
            ("FileCloseTab.Text", "Ctrl+W"),
            ("FilePrint.Text", "Ctrl+P"),
            ("EditFind.Text", "Ctrl+F"),
            ("ViewZoomIn.Text", "Ctrl++"),
            ("ViewZoomOut.Text", "Ctrl+\u2212"),
            ("ViewZoomReset.Text", "Ctrl+0"),
            ("ViewPreview.Text", "Ctrl+Shift+V"),
            ("ShortcutsEsc.Text", "Esc"),
            ("EditUndo.Text", "Ctrl+Z"),
            ("EditRedo.Text", "Ctrl+Y"),
            ("EditCut.Text", "Ctrl+X"),
            ("EditCopy.Text", "Ctrl+C"),
            ("EditPaste.Text", "Ctrl+V"),
            ("EditSelectAll.Text", "Ctrl+A"),
        ];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var panel = new StackPanel { Spacing = 0, MinWidth = 300 };
        foreach (var (key, glyph) in rows)
        {
            var grid = new Grid { Padding = new Thickness(0, 5, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = Str(key) };
            Grid.SetColumn(label, 0);
            var keys = new TextBlock
            {
                Text = glyph,
                Foreground = secondary,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(16, 0, 0, 0)
            };
            Grid.SetColumn(keys, 1);
            grid.Children.Add(label);
            grid.Children.Add(keys);
            panel.Children.Add(grid);
        }
        var dlg = new ContentDialog
        {
            Title = Str("Shortcuts.Title"),
            Content = new ScrollViewer { Content = panel },
            CloseButtonText = Str("PrintClose"),
            XamlRoot = Content.XamlRoot
        };
        await dlg.ShowAsync();
    }

    // Help > About: name, version and one-line description
    // (macOS uses the native app-menu About box instead).
    private async void About_Click(object s, RoutedEventArgs e)
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var dlg = new ContentDialog
        {
            Title = "FluentNotepad",
            Content = string.Format(Str("VersionInfo"), v?.ToString(3) ?? "?"),
            CloseButtonText = Str("PrintClose"),
            XamlRoot = Content.XamlRoot
        };
        await dlg.ShowAsync();
    }

    private async Task ShowNoteAsync(string content)
    {
        var dlg = new ContentDialog
        {
            Title = "FluentNotepad",
            Content = content,
            CloseButtonText = Str("PrintClose"),
            XamlRoot = Content.XamlRoot
        };
        await dlg.ShowAsync();
    }

    // ================= PRINT (rendered Markdown preview only, in color) =================
    private async void Print_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d?.CanPreview() != true)
        {
            await ShowNoteAsync(Str("PrintOnlyPreview"));
            return;
        }
        if (!d.PreviewVisible) ShowPreview(d, true);
        if (PreviewError.Visibility == Visibility.Visible)
        {
            await ShowNoteAsync(Str("PrintError") + _lastWebError);
            return;
        }
        try
        {
            // The page title already carries DisplayName (see Build): the browser
            // print header shows it instead of "about:blank".
            await PreviewView.EnsureCoreWebView2Async();
            if (d.IsTree())
            {
                // Print the whole tree, not just the open branches: expand first
                // (the toolbar is hidden by the print CSS).
                try
                {
                    await PreviewView.CoreWebView2.ExecuteScriptAsync("if(window.__fnExpandAll)window.__fnExpandAll();");
                    await Task.Delay(200);
                }
                catch (Exception ex) { Debug.WriteLine($"ExpandPrint: {ex.Message}"); }
            }
            PreviewView.CoreWebView2.ShowPrintUI(
                Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Print: {ex.Message}");
            await ShowNoteAsync(Str("PrintError") + ex.Message);
        }
    }

    private void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        ShowActiveContent();
        var ab = GetActiveBox();
        if (ab != null)
        {
            var (s0, l0) = GetSelection(ab);
            _selStart = s0;
            _selLen = l0;
        }
        UpdateStatus();
        RefreshFileLabels();
        RefreshMdUi();
        FlashCaretLine(); // no-op unless the new tab is in Edit
    }

    // ================= EDIT =================
    private RichEditBox? EditableBox()
    {
        var d = GetActiveDoc();
        if (d == null) return null;
        if (d.PreviewVisible) ShowPreview(d, false);
        return d.Editor;
    }

    private void Undo_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox();
        if (b?.Document.CanUndo() == true) b.Document.Undo();
    }
    private void Redo_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox();
        if (b?.Document.CanRedo() == true) b.Document.Redo();
    }
    private void Cut_Click(object s, RoutedEventArgs e)
    {
        GetActiveBox()?.Document.Selection.Cut();
    }
    private void Copy_Click(object s, RoutedEventArgs e)
    {
        GetActiveBox()?.Document.Selection.Copy();
    }
    private void Paste_Click(object s, RoutedEventArgs e)
    {
        GetActiveBox()?.Document.Selection.Paste(0);
    }
    private void SelectAll_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox();
        if (b != null) SetSelection(b, 0, GetEditorText(b).Length);
    }

    // ================= FIND =================
    private void ShowFind()
    {
        var d = GetActiveDoc();
        if (d?.PreviewVisible == true) ShowPreview(d, false);
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }

    // Ctrl+F is contextual: in a tree preview it focuses the tree search
    // (ESC clears that first, see EscapeAccel); otherwise the editor find bar.
    private async void FocusTreeSearch()
    {
        try
        {
            if (PreviewView.CoreWebView2 != null)
                await PreviewView.CoreWebView2.ExecuteScriptAsync("if(window.__fnFocusSearch)window.__fnFocusSearch();");
        }
        catch (Exception ex) { Debug.WriteLine($"TreeSearch: {ex.Message}"); }
    }

    private void Find_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d is { PreviewVisible: true } && (d.IsTree() || d.IsCsv)) { FocusTreeSearch(); return; }
        ShowFind();
    }
    private void FindClose_Click(object s, RoutedEventArgs e) => FindBar.Visibility = Visibility.Collapsed;
    private void FindBox_TextChanged(object s, TextChangedEventArgs e) => FindNext(false, focusEditor: false);
    private void FindBox_KeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            FindNext(shift);
        }
        if (e.Key == Windows.System.VirtualKey.Escape) FindBar.Visibility = Visibility.Collapsed;
    }
    private void FindNext_Click(object s, RoutedEventArgs e) => FindNext(false);
    private void FindPrev_Click(object s, RoutedEventArgs e) => FindNext(true);

    private void FindNext(bool up, bool focusEditor = true)
    {
        var b = GetActiveBox();
        if (b == null || string.IsNullOrEmpty(FindBox.Text)) return;
        var text = GetEditorText(b); var q = FindBox.Text;
        var (cs, cl) = CachedSelection(b);
        int start = up ? Math.Max(0, cs - 1) : cs + cl;
        int i = up ? text.LastIndexOf(q, start, StringComparison.OrdinalIgnoreCase)
                   : text.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && !up) i = text.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && up && text.Length > 0) i = text.LastIndexOf(q, text.Length - 1, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) { SetSelection(b, i, q.Length); if (focusEditor) b.Focus(FocusState.Programmatic); }
    }

    // ================= ZOOM / STATUS =================
    private void SetZoom(double z, bool renderPreview = true)
    {
        _zoom = Math.Clamp(z, 0.5, 3.0);
        if (GetActiveDoc() is { } dd)
        {
            dd.Editor.FontSize = EffectiveFontSize();
            RefreshHighlight(dd); // sizes are baked into the highlight ranges
        }
        if (ZoomLabel != null) ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
        if (!renderPreview) return;
        var d = GetActiveDoc();
        if (d is { PreviewVisible: true } && d.CanPreview()) RenderPreview(d);
    }
    private void ZoomIn_Click(object s, RoutedEventArgs e) => SetZoom(_zoom + 0.1);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => SetZoom(_zoom - 0.1);
    private void ZoomReset_Click(object s, RoutedEventArgs e) => SetZoom(1.0);
    private void ZoomInAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        SetZoom(_zoom + 0.1); e.Handled = true;
    }
    private void ZoomOutAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        SetZoom(_zoom - 0.1); e.Handled = true;
    }

    private void UpdateStatus()
    {
        var b = GetActiveBox();
        if (b == null)
        {
            LnCol.Text = string.Format(Str("StatusLine"), 1, 1);
            CharCount.Text = string.Format(Str("StatusChars"), 0);
            return;
        }
        var text = GetEditorText(b) ?? "";
        CharCount.Text = string.Format(Str("StatusChars"), text.Length);
        int caret = Math.Clamp(GetSelection(b).Start, 0, text.Length);
        int line = 1, col = 1;
        for (int k = 0; k < caret; k++)
        {
            if (text[k] == '\n') { line++; col = 1; }
            else col++;
        }
        LnCol.Text = string.Format(Str("StatusLine"), line, col);
    }

    // ================= THEME =================
    private void DetectWindowsTheme()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) _dark = v == 0;
        }
        catch { _dark = false; }
    }

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        // Only the tab strip is Mica (chrome); the menu bar follows the editor as in
        // Notepad (white / #282828), so tab and menu share the same white/black
        var editorBg = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 40, 40, 40)      // #282828
            : Windows.UI.Color.FromArgb(255, 255, 255, 255)); // bianco
        EditorHost.Background = editorBg;
        TitleBarGrid.Background = new SolidColorBrush(Colors.Transparent); // Mica a vista
        MenuRow.Background = editorBg;
        StatusRow.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)      // #202020
            : Windows.UI.Color.FromArgb(255, 243, 243, 243)); // #F3F3F3
        // Edit/Preview toggle: same tones as the menu, never white in dark mode
        MdToggle.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 45, 45, 45)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        MdToggle.BorderBrush = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 58, 58, 58)
            : Windows.UI.Color.FromArgb(255, 225, 221, 221));
        RefreshMdUi();
        ThemeIcon.Glyph = dark ? "\uE706" : "\uE708";
        // Highlight colors follow the theme: recolor every tab
        foreach (var t in Tabs.TabItems.OfType<TabViewItem>())
            if (t.Tag is DocumentInfo dd) RefreshHighlight(dd);
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 38, 38, 38);
        tb.ButtonInactiveForegroundColor = Colors.Gray;
        var d = GetActiveDoc();
        if (d is { PreviewVisible: true } && d.CanPreview()) RenderPreview(d);
    }

    private void ThemeButton_Click(object s, RoutedEventArgs e) =>
        // Switch to the explicit opposite of the effective theme: the combo saves+applies by itself
        ThemeCombo.SelectedIndex = _dark ? 0 : 1;
    private void ThemeCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string mode = ThemeCombo.SelectedIndex == 0 ? "light"
            : ThemeCombo.SelectedIndex == 1 ? "dark" : "system";
        var ts = LoadSettings();
        ts.Theme = mode;
        SaveSettings(ts);
        ApplyThemeSetting(mode);
    }

    private static readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentNotepad", "settings.json");

    private sealed class AppSettings
    {
        public string Theme { get; set; } = "system";
        public bool Wrap { get; set; } = true;
        public double FontSize { get; set; } = 15;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static AppSettings LoadSettings()
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_settingsPath), _jsonOptions);
            if (s == null) return new AppSettings();
            if (s.Theme is not ("light" or "dark" or "system")) s.Theme = "system";
            return s;
        }
        catch (Exception ex) { Debug.WriteLine($"LoadSettings: {ex.Message}"); }
        return new AppSettings();
    }

    private static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s, _jsonOptions));
        }
        catch (Exception ex) { Debug.WriteLine($"SaveSettings: {ex.Message}"); }
    }

    // ================= WINDOW GEOMETRY =================
    private void RestoreWindowGeometry()
    {
        var s = LoadSettings();
        try
        {
            // Validity derived at load (never persisted): sane size on a live display.
            var bounds = new RectInt32(s.X, s.Y, s.Width, s.Height);
            if (s.Width < 300 || s.Height < 200 || !IntersectsAnyDisplay(bounds))
            {
                AppWindow.Resize(new SizeInt32(1000, 650));
                return;
            }
            _restoreBounds = bounds;
            AppWindow.MoveAndResize(bounds);
            if (s.Maximized && AppWindow.Presenter is OverlappedPresenter p && p.IsMaximizable)
                p.Maximize();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RestoreWindow: {ex.Message}");
            try { AppWindow.Resize(new SizeInt32(1000, 650)); } catch { }
        }
    }

    private static bool IntersectsAnyDisplay(RectInt32 r)
    {
        try
        {
            foreach (var area in DisplayArea.FindAll())
            {
                var w = area.WorkArea;
                if (r.X < w.X + w.Width && r.X + r.Width > w.X &&
                    r.Y < w.Y + w.Height && r.Y + r.Height > w.Y)
                    return true;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Displays: {ex.Message}"); return true; }
        return false;
    }

    private void App_Changed(AppWindow sender, AppWindowChangedEventArgs e)
    {
        try
        {
            // Cache of the restored bounds while the window is neither maximized nor minimized
            if (sender.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Restored &&
                (e.DidSizeChange || e.DidPositionChange))
            {
                _restoreBounds = new RectInt32(sender.Position.X, sender.Position.Y,
                    sender.Size.Width, sender.Size.Height);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"WinChanged: {ex.Message}"); }
    }

    private void SaveWindowGeometry()
    {
        try
        {
            var s = LoadSettings();
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                if (p.State == OverlappedPresenterState.Minimized) return; // never persist the minimized state
                if (p.State == OverlappedPresenterState.Maximized)
                {
                    s.Maximized = true;
                    s.X = _restoreBounds.X; s.Y = _restoreBounds.Y;
                    s.Width = _restoreBounds.Width; s.Height = _restoreBounds.Height;
                    SaveSettings(s);
                    return;
                }
            }
            s.Maximized = false;
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            if (size.Width >= 300 && size.Height >= 200)
            { s.X = pos.X; s.Y = pos.Y; s.Width = size.Width; s.Height = size.Height; }
            SaveSettings(s);
        }
        catch (Exception ex) { Debug.WriteLine($"SaveWindow: {ex.Message}"); }
    }

    private void ApplyThemeSetting(string mode)
    {
        if (mode == "light") ApplyTheme(false);
        else if (mode == "dark") ApplyTheme(true);
        else { DetectWindowsTheme(); ApplyTheme(_dark); }
    }

    // ================= SETTINGS / WRAP / FONT / DRAG & DROP =================
    private async void Settings_Click(object s, RoutedEventArgs e)
    {
        SettingsDialog.XamlRoot = Content.XamlRoot;
        await SettingsDialog.ShowAsync();
    }

    private void Wrap_Changed(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyWrap();
        var ws = LoadSettings();
        ws.Wrap = WrapCheck.IsChecked == true;
        SaveSettings(ws);
    }
    private void Wrap_Click(object s, RoutedEventArgs e) => WrapCheck.IsChecked = !(WrapCheck.IsChecked == true);
    private void ApplyWrap()
    {
        foreach (var t in Tabs.TabItems.OfType<TabViewItem>())
            if ((t.Tag as DocumentInfo)?.Editor is RichEditBox b)
                b.TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }
    private void FontSlider_Changed(object s, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        var fs = LoadSettings();
        fs.FontSize = FontSlider?.Value ?? 15;
        SaveSettings(fs);
        SetZoom(_zoom, renderPreview: false);
        // The WebView2 preview is expensive: debounce instead of rendering on every drag tick
        var d = GetActiveDoc();
        if (d is { PreviewVisible: true } && d.CanPreview()) { _previewTimer.Stop(); _previewTimer.Start(); }
    }

    private void Root_DragOver(object s, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Root_Drop(object s, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile f)
            {
                try { OpenFile(f.Path, await FileIO.ReadTextAsync(f)); }
                catch (Exception ex) { Debug.WriteLine($"Drop: {ex.Message}"); }
            }
        }
    }

    // ================= FORMATTING TOOLBAR =================
    private static void ReplaceSelection(RichEditBox b, int start, int length, string insert)
    {
        b.Document.BeginUndoGroup();
        try
        {
            SetSelection(b, start, length);
            b.Document.Selection.Text = insert;
        }
        finally { try { b.Document.EndUndoGroup(); } catch (Exception ex) { Debug.WriteLine($"UndoGroup: {ex.Message}"); } }
    }

    private void WrapSelection(string before, string after, string? placeholder = null)
    {
        var b = EditableBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        string full = GetEditorText(b);
        string sel = len > 0 ? full.Substring(start, len) : (placeholder ?? Str("PlaceholderText"));
        ReplaceSelection(b, start, len, before + sel + after);
        if (len == 0) SetSelection(b, start + before.Length, sel.Length);
        b.Focus(FocusState.Programmatic);
    }

    private void PrefixLines(string prefix)
    {
        var b = EditableBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        string full = GetEditorText(b);
        int lineStart = start;
        while (lineStart > 0 && full[lineStart - 1] != '\n') lineStart--;
        int lineEnd = start + len;
        while (lineEnd < full.Length && full[lineEnd] != '\n') lineEnd++;
        var chunk = full[lineStart..lineEnd];
        var lines = chunk.Split('\n');
        for (int k = 0; k < lines.Length; k++)
            lines[k] = lines[k].StartsWith(prefix) ? lines[k][prefix.Length..] : prefix + lines[k];
        var joined = string.Join('\n', lines);
        ReplaceSelection(b, lineStart, lineEnd - lineStart, joined);
        SetSelection(b, lineStart, joined.Length);
        b.Focus(FocusState.Programmatic);
    }

    private void FormatHeading_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        var (caret, _) = CachedSelection(b);
        string full = GetEditorText(b);
        int ls = caret;
        while (ls > 0 && full[ls - 1] != '\n') ls--;
        ReplaceSelection(b, ls, 0, "# ");
        SetSelection(b, caret + 2, 0);
        b.Focus(FocusState.Programmatic);
    }
    private void FormatBold_Click(object s, RoutedEventArgs e) => WrapSelection("**", "**");
    private void FormatItalic_Click(object s, RoutedEventArgs e) => WrapSelection("*", "*");
    private void FormatStrike_Click(object s, RoutedEventArgs e) => WrapSelection("~~", "~~");
    private void FormatCode_Click(object s, RoutedEventArgs e) => WrapSelection("`", "`", Str("PlaceholderCode"));
    private void FormatLink_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        string full = GetEditorText(b);
        string sel = len > 0 ? full.Substring(start, len) : Str("PlaceholderText");
        ReplaceSelection(b, start, len, $"[{sel}](https://)");
        SetSelection(b, start + 1, sel.Length);
        b.Focus(FocusState.Programmatic);
    }
    private void FormatQuote_Click(object s, RoutedEventArgs e) => PrefixLines("> ");
    private void FormatBullets_Click(object s, RoutedEventArgs e) => PrefixLines("- ");
    private void FormatNumbers_Click(object s, RoutedEventArgs e) => PrefixLines("1. ");
}
