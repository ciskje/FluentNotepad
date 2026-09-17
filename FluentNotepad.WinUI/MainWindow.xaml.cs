using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
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

    private sealed class DocumentInfo
    {
        public TabViewItem Tab = null!;
        public TextBox Editor = null!;
        public string? Path;
        public bool IsMarkdown;
        public bool PreviewVisible;
        public bool Dirty;
        public string LineEnding = "Windows (CRLF)";
        public string FileEncoding = "UTF-8";
    }

    public MainWindow()
    {
        InitializeComponent();

        // Title bar estesa + Mica di sistema (niente P/Invoke: è built-in)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        AppWindow.Resize(new SizeInt32(1000, 650));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += App_Closing;
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

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionInfo.Text = $"FluentNotepad {v?.ToString(3) ?? "?"} — anteprima Markdown via WebView2.";

        // Ctrl++ / Ctrl+- sulla tastiera principale (187=OemPlus, 189=OemMinus)
        var plus = new KeyboardAccelerator { Key = (VirtualKey)187, Modifiers = VirtualKeyModifiers.Control };
        plus.Invoked += ZoomInAccel_Invoked;
        Root.KeyboardAccelerators.Add(plus);
        var minus = new KeyboardAccelerator { Key = (VirtualKey)189, Modifiers = VirtualKeyModifiers.Control };
        minus.Invoked += ZoomOutAccel_Invoked;
        Root.KeyboardAccelerators.Add(minus);

        DetectWindowsTheme();
        ApplyTheme(_dark);
        NewTab(null);
        _loading = false;
        GetActiveBox()?.Focus(FocusState.Programmatic);
    }

    public void OpenFileFromArgs(string path)
    {
        try
        {
            OpenFile(path, File.ReadAllText(path));
            var d = GetActiveDoc();
            if (d?.IsMarkdown == true) ShowPreview(d, true);
        }
        catch (Exception ex) { Debug.WriteLine($"OpenFileFromArgs: {ex.Message}"); }
    }

    // ================= DOCUMENTI / TAB =================
    private DocumentInfo? GetActiveDoc() => Tabs.SelectedItem as TabViewItem is { Tag: DocumentInfo d } ? d : null;
    private TextBox? GetActiveBox() => GetActiveDoc()?.Editor;

    // Selezione "ricordata": il click su un pulsante/menu sposta il focus e lo stato
    // live del TextBox nel gestore non e' affidabile — la cache e' aggiornata a ogni SelectionChanged.
    private (int Start, int Length) CachedSelection(TextBox b)
    {
        int textLen = b.Text.Length;
        int start = Math.Clamp(_selStart, 0, textLen);
        return (start, Math.Clamp(_selLen, 0, textLen - start));
    }

    public static bool IsMarkdownFile(string? path) =>
        path != null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(DocumentInfo d) =>
        (d.Dirty ? "*" : "") + (d.Path != null ? System.IO.Path.GetFileName(d.Path) : "Senza titolo");

    private void RefreshHeader(DocumentInfo d) => d.Tab.Header = DisplayName(d);

    private static string DetectLineEnding(string text) =>
        text.Contains("\r\n") ? "Windows (CRLF)" : text.Contains('\n') ? "Unix (LF)" : "Windows (CRLF)";

    private static string DetectEncoding(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int b0 = fs.ReadByte(), b1 = fs.ReadByte(), b2 = fs.ReadByte();
            if (b0 == 0xEF && b1 == 0xBB && b2 == 0xBF) return "UTF-8 con BOM";
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
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Cascadia Code, Consolas"),
            FontSize = 15,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        // NOTA: Text DOPO AcceptsReturn, altrimenti WinUI taglia tutto dopo il primo \n
        box.Text = content;
        var doc = new DocumentInfo { Editor = box, Path = path, IsMarkdown = IsMarkdownFile(path) };
        var tab = new TabViewItem { Tag = doc };
        doc.Tab = tab;
        RefreshHeader(doc);
        box.Tag = doc;
        box.TextChanged += (_, _) =>
        {
            if (box.Tag is DocumentInfo d)
            {
                d.Dirty = true;
                RefreshHeader(d);
                if (d.IsMarkdown && d.PreviewVisible) { _previewTimer.Stop(); _previewTimer.Start(); }
            }
            UpdateStatus();
        };
        box.SelectionChanged += (_, _) =>
        {
            _selStart = box.SelectionStart;
            _selLen = box.SelectionLength;
            UpdateStatus();
        };
        // Snapshot quando il focus lascia l'editor: il Click sul pulsante/menu arriva dopo,
        // e in quel momento lo stato live della selezione non e' piu' affidabile
        box.LosingFocus += (_, _) =>
        {
            _selStart = box.SelectionStart;
            _selLen = box.SelectionLength;
        };

        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
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
            Content = $"Salvare le modifiche a {DisplayName(doc).TrimStart('*')}?",
            PrimaryButtonText = "Salva",
            SecondaryButtonText = "Non salvare",
            CloseButtonText = "Annulla",
            XamlRoot = Content.XamlRoot
        };
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.Primary)
            return await SaveTabAsync(doc) ? SaveAction.Save : SaveAction.Cancel;
        if (r == ContentDialogResult.Secondary) return SaveAction.Discard;
        return SaveAction.Cancel;
    }

    // NOTA: TabView NON rimuove la tab da solo: bisogna farlo qui (vale anche per Annulla = non rimuovere)
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
        if (_closeConfirmed) return;
        var dirty = Tabs.TabItems.OfType<TabViewItem>()
            .Select(t => t.Tag as DocumentInfo).Where(d => d?.Dirty == true).ToList();
        if (dirty.Count == 0) return;
        e.Cancel = true;
        foreach (var d in dirty)
        {
            Tabs.SelectedItem = d!.Tab;
            if (await AskSaveAsync(d) == SaveAction.Cancel) return;
        }
        _closeConfirmed = true;
        Close();
    }

    // ================= ANTEPRIMA MARKDOWN (WebView2) =================
    private readonly SemaphoreSlim _webLock = new(1, 1);
    private string _lastWebError = "";
    private string? _previewTmp;

    private async void RenderPreview(DocumentInfo doc)
    {
        if (!doc.IsMarkdown || !doc.PreviewVisible) return;
        PreviewError.Visibility = Visibility.Collapsed;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await TryRenderAsync(doc)) return;
            if (!doc.PreviewVisible) return;
            if (attempt < 2) await Task.Delay(1500);
        }
        PreviewView.Visibility = Visibility.Collapsed;
        PreviewErrorText.Text = "Anteprima non disponibile (WebView2). " + _lastWebError;
        PreviewError.Visibility = Visibility.Visible;
    }

    private async Task<bool> TryRenderAsync(DocumentInfo doc)
    {
        await _webLock.WaitAsync();
        try
        {
            if (!_webReady) { await PreviewView.EnsureCoreWebView2Async(); _webReady = true; }
            string? baseDir = doc.Path != null ? Path.GetDirectoryName(doc.Path) : null;
            double size = (FontSlider?.Value ?? 15) * _zoom;
            string html = MarkdownHtml.Build(doc.Editor.Text, baseDir, _dark, size);
            if (html.Length > 1500000)
            {
                // NavigateToString ha un limite ~2MB: file grandi via temp file
                _previewTmp ??= Path.Combine(Path.GetTempPath(), $"fluent_preview_{Environment.ProcessId}.html");
                File.WriteAllText(_previewTmp, html);
                PreviewView.Source = new Uri(_previewTmp);
            }
            else PreviewView.NavigateToString(html);
            PreviewView.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex) { _webReady = false; _lastWebError = ex.Message; return false; }
        finally { _webLock.Release(); }
    }

    private void PreviewRetry_Click(object s, RoutedEventArgs e)
    {
        if (GetActiveDoc() is { } d) RenderPreview(d);
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
            // Non ri-aggiungere l'editor se e' gia' ospitato: il re-parenting
            // azzera selezione (e scroll), e i tag finivano a inizio file
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
        FileTypeLabel.Text = isMd ? "Markdown" : "Testo normale";
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
                picker.SuggestedFileName = "Senza titolo";
                picker.DefaultFileExtension = ".md";
            }
            else
            {
                picker.FileTypeChoices.Add("File di testo", new List<string> { ".txt" });
                picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
                picker.SuggestedFileName = "Senza titolo";
                picker.DefaultFileExtension = ".txt";
            }
            var file = await picker.PickSaveFileAsync();
            if (file == null) return false;
            doc.Path = file.Path;
            doc.IsMarkdown = IsMarkdownFile(doc.Path);
            RefreshHeader(doc);
            RefreshMdUi();
            await FileIO.WriteTextAsync(file, doc.Editor.Text);
        }
        else
        {
            File.WriteAllText(doc.Path, doc.Editor.Text);
        }
        doc.Dirty = false;
        doc.LineEnding = DetectLineEnding(doc.Editor.Text);
        doc.FileEncoding = "UTF-8"; // FileIO.WriteTextAsync e File.WriteAllText scrivono UTF-8 senza BOM
        RefreshHeader(doc);
        RefreshFileLabels();
        return true;
    }

    private void OpenFile(string path, string content)
    {
        if (Tabs.SelectedItem is TabViewItem cur && cur.Tag is DocumentInfo cd &&
            cd.Editor.Text == "" && cd.Path == null && !cd.Dirty)
            Tabs.TabItems.Remove(cur);
        NewTab(path, content);
        if (GetActiveDoc() is { } d)
        {
            d.Dirty = false;
            d.LineEnding = DetectLineEnding(content);
            if (File.Exists(path)) d.FileEncoding = DetectEncoding(path);
            RefreshHeader(d);
            RefreshFileLabels();
        }
    }

    private void NewTab_Click(object s, RoutedEventArgs e) => NewTab(null);
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

    private void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        ShowActiveContent();
        var ab = GetActiveBox();
        if (ab != null) { _selStart = ab.SelectionStart; _selLen = ab.SelectionLength; }
        UpdateStatus();
        RefreshFileLabels();
        RefreshMdUi();
    }

    // ================= MODIFICA =================
    private TextBox? EditableBox()
    {
        var d = GetActiveDoc();
        if (d == null) return null;
        if (d.PreviewVisible) ShowPreview(d, false);
        return d.Editor;
    }

    private void Undo_Click(object s, RoutedEventArgs e) { var b = GetActiveBox(); if (b?.CanUndo == true) b.Undo(); }
    private void Redo_Click(object s, RoutedEventArgs e) { var b = GetActiveBox(); if (b?.CanRedo == true) b.Redo(); }
    private void CopySelection(TextBox b)
    {
        var (start, len) = CachedSelection(b);
        if (len == 0) return;
        var dp = new DataPackage();
        dp.SetText(b.Text.Substring(start, len));
        Clipboard.SetContent(dp);
    }
    private void Cut_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        if (len == 0) return;
        CopySelection(b);
        b.Select(start, len);
        b.SelectedText = "";
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
        b.Select(ps, pl);
        b.SelectedText = t;
    }
    private void SelectAll_Click(object s, RoutedEventArgs e) => GetActiveBox()?.SelectAll();

    // ================= TROVA =================
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
        var text = b.Text; var q = FindBox.Text;
        var (cs, cl) = CachedSelection(b);
        int start = up ? Math.Max(0, cs - 1) : cs + cl;
        int i = up ? text.LastIndexOf(q, start, StringComparison.OrdinalIgnoreCase)
                   : text.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && !up) i = text.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && up && text.Length > 0) i = text.LastIndexOf(q, text.Length - 1, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) { b.Select(i, q.Length); if (focusEditor) b.Focus(FocusState.Programmatic); }
    }

    // ================= ZOOM / STATUS =================
    private void SetZoom(double z, bool renderPreview = true)
    {
        _zoom = Math.Clamp(z, 0.5, 3.0);
        var b = GetActiveBox();
        if (b != null) b.FontSize = (FontSlider?.Value ?? 15) * _zoom;
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
            LnCol.Text = "Riga 1, col. 1";
            CharCount.Text = "0 caratteri";
            return;
        }
        var text = b.Text ?? "";
        CharCount.Text = $"{text.Length} caratteri";
        int caret = Math.Clamp(b.SelectionStart, 0, text.Length);
        int line = 1, col = 1;
        for (int k = 0; k < caret; k++)
        {
            if (text[k] == '\n') { line++; col = 1; }
            else col++;
        }
        LnCol.Text = $"Riga {line}, col. {col}";
    }

    // ================= TEMA =================
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
        // Titolo = Mica, sfondo = bianco/#282828, menu = via di mezzo.
        var editorBg = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 40, 40, 40)      // #282828
            : Windows.UI.Color.FromArgb(255, 255, 255, 255)); // bianco
        var menuBg = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 36, 36, 36)      // #242424: tra #202020 e #282828
            : Windows.UI.Color.FromArgb(255, 249, 249, 249)); // #F9F9F9: tra #F3F3F3 e bianco
        EditorHost.Background = editorBg;
        TitleBarGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); // Mica a vista
        MenuRow.Background = menuBg;
        StatusRow.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)      // #202020
            : Windows.UI.Color.FromArgb(255, 243, 243, 243)); // #F3F3F3
        // Toggle Modifica/Anteprima: stessi toni del menu, mai bianco in dark
        MdToggle.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 45, 45, 45)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        MdToggle.BorderBrush = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 58, 58, 58)
            : Windows.UI.Color.FromArgb(255, 225, 221, 221));
        RefreshMdUi();
        ThemeIcon.Glyph = dark ? "\uE706" : "\uE708";
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 38, 38, 38);
        tb.ButtonInactiveForegroundColor = Colors.Gray;
        var d = GetActiveDoc();
        if (d is { IsMarkdown: true, PreviewVisible: true }) RenderPreview(d);
    }

    private void ThemeButton_Click(object s, RoutedEventArgs e) => ApplyTheme(!_dark);
    private void ThemeCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeCombo.SelectedIndex == 0) ApplyTheme(false);
        else if (ThemeCombo.SelectedIndex == 1) ApplyTheme(true);
        else { DetectWindowsTheme(); ApplyTheme(_dark); }
    }

    // ================= IMPOSTAZIONI / WRAP / FONT / DRAGDROP =================
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
            if ((t.Tag as DocumentInfo)?.Editor is TextBox b)
                b.TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }
    private void FontSlider_Changed(object s, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        SetZoom(_zoom, renderPreview: false);
        // L'anteprima WebView2 e' costosa: debounce invece di un render a ogni tick del drag
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

    // ================= BARRA FORMATTAZIONE =================
    private void WrapSelection(string before, string after, string placeholder = "testo")
    {
        var b = EditableBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        string sel = len > 0 ? b.Text.Substring(start, len) : placeholder;
        b.Select(start, len);
        b.SelectedText = before + sel + after;
        if (len == 0) b.Select(start + before.Length, sel.Length);
        b.Focus(FocusState.Programmatic);
    }

    private void PrefixLines(string prefix)
    {
        var b = EditableBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        int lineStart = start;
        while (lineStart > 0 && b.Text[lineStart - 1] != '\n') lineStart--;
        int lineEnd = start + len;
        while (lineEnd < b.Text.Length && b.Text[lineEnd] != '\n') lineEnd++;
        var chunk = b.Text[lineStart..lineEnd];
        var lines = chunk.Split('\n');
        for (int k = 0; k < lines.Length; k++)
            lines[k] = lines[k].StartsWith(prefix) ? lines[k][prefix.Length..] : prefix + lines[k];
        var joined = string.Join('\n', lines);
        b.Select(lineStart, lineEnd - lineStart);
        b.SelectedText = joined;
        b.Select(lineStart, joined.Length);
        b.Focus(FocusState.Programmatic);
    }

    private void FormatHeading_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        var (caret, _) = CachedSelection(b);
        int ls = caret;
        while (ls > 0 && b.Text[ls - 1] != '\n') ls--;
        b.Select(ls, 0);
        b.SelectedText = "# ";
        b.Select(caret + 2, 0);
        b.Focus(FocusState.Programmatic);
    }
    private void FormatBold_Click(object s, RoutedEventArgs e) => WrapSelection("**", "**");
    private void FormatItalic_Click(object s, RoutedEventArgs e) => WrapSelection("*", "*");
    private void FormatStrike_Click(object s, RoutedEventArgs e) => WrapSelection("~~", "~~");
    private void FormatCode_Click(object s, RoutedEventArgs e) => WrapSelection("`", "`", "codice");
    private void FormatLink_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        var (start, len) = CachedSelection(b);
        string sel = len > 0 ? b.Text.Substring(start, len) : "testo";
        b.Select(start, len);
        b.SelectedText = $"[{sel}](https://)";
        b.Select(start + 1, sel.Length);
        b.Focus(FocusState.Programmatic);
    }
    private void FormatQuote_Click(object s, RoutedEventArgs e) => PrefixLines("> ");
    private void FormatBullets_Click(object s, RoutedEventArgs e) => PrefixLines("- ");
    private void FormatNumbers_Click(object s, RoutedEventArgs e) => PrefixLines("1. ");
}
