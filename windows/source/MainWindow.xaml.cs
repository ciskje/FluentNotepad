using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
    private static System.Collections.Generic.Dictionary<string, string>? _res;
    internal static string Str(string key)
    {
        try
        {
            if (_res == null)
            {
                // Same rule as the rest of the UI: English only on English systems
                var lang = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault()?.Split('-')[0];
                _res = LoadResw(lang == "en" ? "en-US" : "it-IT");
            }
            if (_res.TryGetValue(key, out var s)) return s;
        }
        catch (Exception ex) { Debug.WriteLine($"Str({key}): {ex.Message}"); }
        return $"[{key}]";
    }

    private static System.Collections.Generic.Dictionary<string, string> LoadResw(string folder)
    {
        var d = new System.Collections.Generic.Dictionary<string, string>();
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "Strings", folder, "Resources.resw"));
        foreach (System.Xml.XmlNode n in xml.SelectNodes("/root/data")!)
        {
            var name = n.Attributes?["name"]?.Value;
            var value = n.SelectSingleNode("value")?.InnerText;
            if (name != null && value != null) d[name] = value;
        }
        return d;
    }
    private sealed class DocumentInfo
    {
        public TabViewItem Tab = null!;
        public RichEditBox Editor = null!;
        public string? Path;
        public bool IsMarkdown;
        public bool PreviewVisible;
        public bool Dirty;
        public string LineEnding = "Windows (CRLF)";
        public string FileEncoding = "UTF-8";
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
            var lang = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault()?.Split('-')[0];
            if (lang != "it" && lang != "en")
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
        AppWindow.Destroying += (_, _) =>
        {
            try { if (_previewTmp != null && File.Exists(_previewTmp)) File.Delete(_previewTmp); }
            catch (Exception ex) { Debug.WriteLine($"PreviewTmp cleanup: {ex.Message}"); }
        };

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(400);
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            var d = GetActiveDoc();
            if (d is { IsMarkdown: true, PreviewVisible: true }) RenderPreview(d);
        };

        _winSaveTimer = DispatcherQueue.CreateTimer();
        _winSaveTimer.Interval = TimeSpan.FromSeconds(1);
        _winSaveTimer.Tick += (_, _) => { _winSaveTimer.Stop(); SaveWindowGeometry(); };

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionInfo.Text = string.Format(Str("VersionInfo"), v?.ToString(3) ?? "?");

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
        string mode = LoadThemeSetting();
        ThemeCombo.SelectedIndex = mode == "light" ? 0 : mode == "dark" ? 1 : 2;
        ApplyThemeSetting(mode);
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
        string key = $"{text.Length}-{text.GetHashCode()}-{EffectiveFontSize()}-{_dark}-{d.IsMarkdown}";
        if (key == d.HighlightKey) return;
        d.HighlightKey = key;
        _highlighting = true;
        try { MarkdownHighlight.Apply(d.Editor, d.IsMarkdown, _dark, EffectiveFontSize(), BaseTextColor(), BaseBgColor()); }
        catch (Exception ex) { Debug.WriteLine($"Highlight: {ex.Message}"); }
        finally { _highlighting = false; }
    }

    public static bool IsMarkdownFile(string? path) =>
        path != null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(DocumentInfo d) =>
        (d.Dirty ? "*" : "") + (d.Path != null ? System.IO.Path.GetFileName(d.Path) : Str("Untitled"));

    private void RefreshHeader(DocumentInfo d) => d.Tab.Header = DisplayName(d);

    private static string DetectLineEnding(string text) =>
        text.Contains("\r\n") ? "Windows (CRLF)" : text.Contains('\n') ? "Unix (LF)" : "Windows (CRLF)";

    private static string DetectEncoding(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int b0 = fs.ReadByte(), b1 = fs.ReadByte(), b2 = fs.ReadByte();
            if (b0 == 0xEF && b1 == 0xBB && b2 == 0xBF) return Str("EncUtf8Bom");
            if (b0 == 0xFF && b1 == 0xFE) return "UTF-16 LE";
            if (b0 == 0xFE && b1 == 0xFF) return "UTF-16 BE";
        }
        catch (Exception ex) { Debug.WriteLine($"DetectEncoding: {ex.Message}"); }
        return "UTF-8";
    }

    private void RefreshFileLabels()
    {
        var d = GetActiveDoc();
        if (LineEndLabel != null) LineEndLabel.Text = d?.LineEnding ?? "Windows (CRLF)";
        if (EncodingLabel != null) EncodingLabel.Text = d?.FileEncoding ?? "UTF-8";
    }

    private void NewTab(string? path, string content = "")
    {
        var box = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Cascadia Code, Consolas"),
            FontSize = 15 * _zoom,
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
        var doc = new DocumentInfo { Editor = box, Path = path, IsMarkdown = IsMarkdownFile(path) };
        var tab = new TabViewItem { Tag = doc };
        doc.Tab = tab;
        RefreshHeader(doc);
        box.Tag = doc;
        box.TextChanging += (_, _) =>
        {
            if (box.Tag is DocumentInfo dd) RefreshHighlight(dd);
        };
        box.TextChanged += (_, _) =>
        {
            if (_highlighting) return;
            if (box.Tag is not DocumentInfo d) return;
            // TextChanged also fires for formatting-only changes: dirty the
            // tab only when the text itself changed.
            string t = GetEditorText(box);
            int h = t.GetHashCode();
            if (t.Length == d.TextLen && h == d.TextHash) return;
            d.TextLen = t.Length;
            d.TextHash = h;
            d.Dirty = true;
            RefreshHeader(d);
            if (d.IsMarkdown && d.PreviewVisible) { _previewTimer.Stop(); _previewTimer.Start(); }
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
        // on every hosting, when the control is actually loaded. Typing and
        // later passes then only refresh on real changes (HighlightKey).
        box.Loaded += (_, _) =>
        {
            if (box.Tag is DocumentInfo dd)
            {
                dd.HighlightKey = "";
                RefreshHighlight(dd);
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
            Content = string.Format(Str("SaveContent"), DisplayName(doc).TrimStart('*')),
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
    private bool _restoreValid = false;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _winSaveTimer;

    // ================= MARKDOWN PREVIEW (WebView2) =================
    private readonly SemaphoreSlim _webLock = new(1, 1);
    private string _lastWebError = "";
    private string? _previewTmp;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _hookedCore;

    // "Busy" cursor (spinning ring): XAML for the app area, html.busy class
    // inside the page for the WebView2 area, which owns its cursor.
    // Minimum duration: Windows only updates the cursor on mouse move, so a
    // render lasting a few ms would never be visible.
    private const int MinBusyMs = 600;
    private bool _busy;
    private DateTime _busyUntil;
    private int _busyGen;

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            _busyGen++;
            _busy = true;
            _busyUntil = DateTime.UtcNow.AddMilliseconds(MinBusyMs);
            Root.SetBusy(true);
            return;
        }
        if (_busy) _ = EndBusyAsync(_busyGen);
    }

    private async Task EndBusyAsync(int gen)
    {
        int wait = (int)(_busyUntil - DateTime.UtcNow).TotalMilliseconds;
        if (wait > 0) await Task.Delay(wait);
        if (gen != _busyGen) return; // another render has started in the meantime
        _busy = false;
        Root.SetBusy(false);
        _ = ClearPageBusyAsync();
    }

    private async Task ClearPageBusyAsync()
    {
        if (!_webReady) return;
        try { await PreviewView.CoreWebView2.ExecuteScriptAsync("document.documentElement.classList.remove('busy')"); }
        catch (Exception ex) { Debug.WriteLine($"ClearPageBusy: {ex.Message}"); }
    }

    private async void RenderPreview(DocumentInfo doc)
    {
        if (!doc.IsMarkdown || !doc.PreviewVisible) return;
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
                }
            }
            string? baseDir = doc.Path != null ? Path.GetDirectoryName(doc.Path) : null;
            double size = (FontSlider?.Value ?? 15) * _zoom;
            string html = MarkdownHtml.Build(GetEditorText(doc.Editor), baseDir, _dark, size,
                DisplayName(doc).TrimStart('*'));
            if (html.Length > 1500000)
            {
                // NavigateToString has a ~2MB limit: large files go through a temp file
                _previewTmp ??= Path.Combine(Path.GetTempPath(), $"fluent_preview_{Environment.ProcessId}.html");
                File.WriteAllText(_previewTmp, html);
                PreviewView.Source = new Uri(_previewTmp);
            }
            else PreviewView.NavigateToString(html);
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

    // A file dropped onto the preview makes WebView2 navigate to it (like Edge),
    // before the XAML drop handlers ever see it: cancel that and open it as a
    // tab instead (like FluentNotepadMac). The only file:// navigation we ever
    // start ourselves is the large-preview temp file, excluded below; our HTML
    // has no file links (images are data-URI), so anything else is a drop.
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
            if (_previewTmp != null &&
                string.Equals(local, _previewTmp, StringComparison.OrdinalIgnoreCase)) return;
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
        if (d == null) { PreviewView.Visibility = Visibility.Collapsed; return; }
        if (d.PreviewVisible && d.IsMarkdown)
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

    private void ShowPreview(DocumentInfo d, bool show)
    {
        if (!d.IsMarkdown) return;
        d.PreviewVisible = show;
        if (show) RenderPreview(d);
        ShowActiveContent();
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
        FileTypeLabel.Text = isMd ? "Markdown" : Str("FileTypePlain");
        if (isMd && d != null)
        {
            StyleToggle(EditModeBtn, !d.PreviewVisible);
            StyleToggle(PreviewModeBtn, d.PreviewVisible);
        }
    }

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
        if (d?.IsMarkdown == true) { ShowPreview(d, !d.PreviewVisible); e.Handled = true; }
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
            if (doc.IsMarkdown)
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
        string cur = GetEditorText(doc.Editor);
        doc.TextLen = cur.Length;
        doc.TextHash = cur.GetHashCode();
        doc.FileEncoding = "UTF-8"; // FileIO.WriteTextAsync and File.WriteAllText write UTF-8 without BOM
        RefreshHeader(doc);
        RefreshFileLabels();
        return true;
    }

    private void OpenFile(string path, string content)
    {
        if (Tabs.SelectedItem is TabViewItem cur && cur.Tag is DocumentInfo cd &&
            GetEditorText(cd.Editor) == "" && cd.Path == null && !cd.Dirty)
            Tabs.TabItems.Remove(cur);
        NewTab(path, content);
        if (GetActiveDoc() is { } d)
        {
            d.Dirty = false;
            d.LineEnding = DetectLineEnding(content);
            if (File.Exists(path)) d.FileEncoding = DetectEncoding(path);
            RefreshHeader(d);
            RefreshFileLabels();
            // An .md opened from anywhere (association/command line, File > Open,
            // drag & drop) starts directly in Preview
            if (d.IsMarkdown) ShowPreview(d, true);
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

    // ================= PRINT (rendered Markdown preview only, in color) =================
    private async void Print_Click(object s, RoutedEventArgs e)
    {
        var d = GetActiveDoc();
        if (d?.IsMarkdown != true)
        {
            var dlg = new ContentDialog
            {
                Title = "FluentNotepad",
                Content = Str("PrintOnlyMarkdown"),
                CloseButtonText = Str("PrintClose"),
                XamlRoot = Content.XamlRoot
            };
            await dlg.ShowAsync();
            return;
        }
        if (!d.PreviewVisible) ShowPreview(d, true);
        if (PreviewError.Visibility == Visibility.Visible)
        {
            var dlg = new ContentDialog
            {
                Title = "FluentNotepad",
                Content = Str("PrintError") + _lastWebError,
                CloseButtonText = Str("PrintClose"),
                XamlRoot = Content.XamlRoot
            };
            await dlg.ShowAsync();
            return;
        }
        try
        {
            await PreviewView.EnsureCoreWebView2Async();
            await WaitForPreviewPrintReadyAsync();
            // Refresh the title (e.g. after Save As without re-render): without it the
            // browser print header shows the document URL (about:blank for NavigateToString).
            try
            {
                string jsTitle = System.Text.Json.JsonSerializer.Serialize(DisplayName(d).TrimStart('*'));
                await PreviewView.CoreWebView2.ExecuteScriptAsync($"document.title = {jsTitle};");
            }
            catch (Exception ex) { Debug.WriteLine($"Print title: {ex.Message}"); }
            PreviewView.CoreWebView2.ShowPrintUI(
                Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Print: {ex.Message}");
            var dlg = new ContentDialog
            {
                Title = "FluentNotepad",
                Content = Str("PrintError") + ex.Message,
                CloseButtonText = Str("PrintClose"),
                XamlRoot = Content.XamlRoot
            };
            await dlg.ShowAsync();
        }
    }

    // Wait for the current preview navigation so the print dialog prints fresh
    // content instead of the previous page (no wait when nothing is loading).
    private Task WaitForPreviewPrintReadyAsync(int timeoutMs = 5000)
    {
        if (!_busy) return Task.CompletedTask;
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Windows.Foundation.TypedEventHandler<Microsoft.Web.WebView2.Core.CoreWebView2, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            try { PreviewView.CoreWebView2.NavigationCompleted -= handler; } catch { }
            tcs.TrySetResult(null);
        };
        try { PreviewView.CoreWebView2.NavigationCompleted += handler; }
        catch { return Task.CompletedTask; }
        return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ContinueWith(_ =>
        {
            try { PreviewView.CoreWebView2.NavigationCompleted -= handler; } catch { }
        });
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
    private void CopySelection(RichEditBox b)
    {
        var (start, len) = CachedSelection(b);
        if (len == 0) return;
        var dp = new DataPackage();
        // CRLF on the clipboard: plain \n pastes as one line in some apps
        dp.SetText(GetEditorText(b).Substring(start, len).Replace("\n", "\r\n"));
        Clipboard.SetContent(dp);
    }
    private void Cut_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        if (len == 0) return;
        CopySelection(b);
        b.Document.BeginUndoGroup();
        try
        {
            SetSelection(b, start, len);
            b.Document.Selection.Text = "";
        }
        finally { try { b.Document.EndUndoGroup(); } catch (Exception ex) { Debug.WriteLine($"UndoGroup: {ex.Message}"); } }
    }
    private void Copy_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox(); if (b != null) CopySelection(b);
    }
    private async void Paste_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox(); if (b == null) return;
        var dv = Clipboard.GetContent();
        if (!dv.Contains(StandardDataFormats.Text)) return;
        string t;
        try { t = await dv.GetTextAsync(); } catch (Exception ex) { Debug.WriteLine($"Paste: {ex.Message}"); return; }
        var (ps, pl) = CachedSelection(b);
        b.Document.BeginUndoGroup();
        try
        {
            SetSelection(b, ps, pl);
            b.Document.Selection.Text = t;
        }
        finally { try { b.Document.EndUndoGroup(); } catch (Exception ex) { Debug.WriteLine($"UndoGroup: {ex.Message}"); } }
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

    private void Find_Click(object s, RoutedEventArgs e) => ShowFind();
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
        if (d is { IsMarkdown: true, PreviewVisible: true }) RenderPreview(d);
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
        if (d is { IsMarkdown: true, PreviewVisible: true }) RenderPreview(d);
    }

    private void ThemeButton_Click(object s, RoutedEventArgs e) =>
        // Switch to the explicit opposite of the effective theme: the combo saves+applies by itself
        ThemeCombo.SelectedIndex = _dark ? 0 : 1;
    private void ThemeCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string mode = ThemeCombo.SelectedIndex == 0 ? "light"
            : ThemeCombo.SelectedIndex == 1 ? "dark" : "system";
        SaveThemeSetting(mode);
        ApplyThemeSetting(mode);
    }

    private static readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentNotepad", "settings.json");

    private sealed class AppSettings
    {
        public string Theme { get; set; } = "system";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
        public bool HasWindow { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
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
            s.HasWindow = s.Width >= 300 && s.Height >= 200;
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

    private static string LoadThemeSetting() => LoadSettings().Theme;

    private static void SaveThemeSetting(string mode)
    {
        var s = LoadSettings();
        s.Theme = mode;
        SaveSettings(s);
    }

    // ================= WINDOW GEOMETRY =================
    private void RestoreWindowGeometry()
    {
        var s = LoadSettings();
        try
        {
            if (!s.HasWindow)
            {
                AppWindow.Resize(new SizeInt32(1000, 650));
                _restoreBounds = new RectInt32(100, 100, 1000, 650);
                return;
            }
            var bounds = new RectInt32(s.X, s.Y, s.Width, s.Height);
            if (!IntersectsAnyDisplay(bounds))
            {
                AppWindow.Resize(new SizeInt32(1000, 650));
                return;
            }
            _restoreBounds = bounds;
            _restoreValid = true;
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
                _restoreValid = true;
            }
            if (e.DidSizeChange || e.DidPositionChange || e.DidPresenterChange)
            {
                _winSaveTimer?.Stop();
                _winSaveTimer?.Start(); // debounce: save 1s after the last change
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
                    if (_restoreValid)
                    { s.X = _restoreBounds.X; s.Y = _restoreBounds.Y; s.Width = _restoreBounds.Width; s.Height = _restoreBounds.Height; s.HasWindow = true; }
                    SaveSettings(s);
                    return;
                }
            }
            s.Maximized = false;
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            if (size.Width >= 300 && size.Height >= 200)
            { s.X = pos.X; s.Y = pos.Y; s.Width = size.Width; s.Height = size.Height; s.HasWindow = true; }
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

    private void Wrap_Changed(object s, RoutedEventArgs e) => ApplyWrap();
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
        SetZoom(_zoom, renderPreview: false);
        // The WebView2 preview is expensive: debounce instead of rendering on every drag tick
        var d = GetActiveDoc();
        if (d is { IsMarkdown: true, PreviewVisible: true }) { _previewTimer.Stop(); _previewTimer.Start(); }
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
