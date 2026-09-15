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
    private int _untitled = 0;
    private double _zoom = 1.0;
    private bool _dark = false;
    private bool _loading = true;
    private bool _closeInFlight = false;
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
        catch { }
    }

    // ================= DOCUMENTI / TAB =================
    private DocumentInfo? GetActiveDoc() => Tabs.SelectedItem as TabViewItem is { Tag: DocumentInfo d } ? d : null;
    private TextBox? GetActiveBox() => GetActiveDoc()?.Editor;

    public static bool IsMarkdownFile(string? path) =>
        path != null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(DocumentInfo d) =>
        (d.Dirty ? "*" : "") + (d.Path != null ? System.IO.Path.GetFileName(d.Path) : "Senza titolo");

    private void RefreshHeader(DocumentInfo d) => d.Tab.Header = DisplayName(d);

    private void NewTab(string? path, string content = "")
    {
        _untitled++;
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = WrapCheck?.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
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
        box.SelectionChanged += (_, _) => UpdateStatus();

        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateStatus();
        RefreshMdUi();
    }

    private void EnsureTab()
    {
        if (!_closeInFlight && Tabs.TabItems.Count == 0) NewTab(null);
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

    private async void Tabs_Close(TabView sender, TabViewTabCloseRequestedEventArgs e)
    {
        if (e.Tab is not TabViewItem item || item.Tag is not DocumentInfo doc) return;
        if (!doc.Dirty) { DispatcherQueue.TryEnqueue(EnsureTab); return; }
        _closeInFlight = true;
        try
        {
            var a = await AskSaveAsync(doc);
            if (a == SaveAction.Cancel)
            {
                int idx = sender.TabItems.IndexOf(item);
                if (idx < 0) sender.TabItems.Add(item);
                else sender.TabItems.Insert(idx, item);
                sender.SelectedItem = item;
            }
        }
        finally { _closeInFlight = false; EnsureTab(); }
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
                string tmp = Path.Combine(Path.GetTempPath(), "fluent_preview.html");
                File.WriteAllText(tmp, html);
                PreviewView.Source = new Uri(tmp);
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
        EditorHost.Children.Clear();
        PreviewError.Visibility = Visibility.Collapsed;
        var d = GetActiveDoc();
        if (d == null) { PreviewView.Visibility = Visibility.Collapsed; return; }
        if (d.PreviewVisible && d.IsMarkdown)
        {
            PreviewView.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewView.Visibility = Visibility.Collapsed;
            EditorHost.Children.Add(d.Editor);
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
            catch { }
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
        RefreshHeader(doc);
        return true;
    }

    private void OpenFile(string path)
    {
        try { OpenFile(path, File.ReadAllText(path)); }
        catch { }
    }

    private void OpenFile(string path, string content)
    {
        if (Tabs.SelectedItem is TabViewItem cur && cur.Tag is DocumentInfo cd &&
            cd.Editor.Text == "" && cd.Path == null && !cd.Dirty)
            Tabs.TabItems.Remove(cur);
        NewTab(path, content);
        if (GetActiveDoc() is { } d) { d.Dirty = false; RefreshHeader(d); }
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
    private void CloseTab_Click(object s, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem item)
        {
            int idx = Tabs.TabItems.IndexOf(item);
            Tabs.TabItems.Remove(item);
            var tag = item.Tag as DocumentInfo;
            if (tag?.Dirty == true)
            {
                _closeInFlight = true;
                _ = HandleCloseAsync(item, tag, idx);
            }
            else EnsureTab();
        }
    }

    private async Task HandleCloseAsync(TabViewItem item, DocumentInfo doc, int idx)
    {
        try
        {
            var a = await AskSaveAsync(doc);
            if (a == SaveAction.Cancel)
            {
                Tabs.TabItems.Insert(Math.Min(idx, Tabs.TabItems.Count), item);
                Tabs.SelectedItem = item;
            }
        }
        finally { _closeInFlight = false; EnsureTab(); }
    }

    private void Exit_Click(object s, RoutedEventArgs e) => Close();

    private void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        ShowActiveContent();
        UpdateStatus();
        RefreshMdUi();
        EnsureTab();
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
        if (!string.IsNullOrEmpty(b.SelectedText))
        {
            var dp = new DataPackage();
            dp.SetText(b.SelectedText);
            Clipboard.SetContent(dp);
        }
    }
    private void Cut_Click(object s, RoutedEventArgs e)
    {
        var b = GetActiveBox(); if (b == null) return;
        CopySelection(b);
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
        try { t = await dv.GetTextAsync(); } catch { return; }
        if (b.SelectionLength > 0) b.SelectedText = t;
        else { int pos = b.SelectionStart; b.Text = b.Text.Insert(pos, t); b.Select(pos + t.Length, 0); }
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
    private void FindBox_TextChanged(object s, TextChangedEventArgs e) => FindNext(false);
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

    private void FindNext(bool up)
    {
        var b = GetActiveBox();
        if (b == null || string.IsNullOrEmpty(FindBox.Text)) return;
        var text = b.Text; var q = FindBox.Text;
        int start = up ? Math.Max(0, b.SelectionStart - 1) : b.SelectionStart + b.SelectionLength;
        int i = up ? text.LastIndexOf(q, start, StringComparison.OrdinalIgnoreCase)
                   : text.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && !up) i = text.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (i < 0 && up && text.Length > 0) i = text.LastIndexOf(q, text.Length - 1, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) { b.Select(i, q.Length); b.Focus(FocusState.Programmatic); }
    }

    // ================= ZOOM / STATUS =================
    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.5, 3.0);
        var b = GetActiveBox();
        if (b != null) b.FontSize = (FontSlider?.Value ?? 15) * _zoom;
        if (ZoomLabel != null) ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
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
        if (b == null) return;
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
        EditorHost.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 40, 40, 40)
            : Windows.UI.Color.FromArgb(255, 249, 249, 249));
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
    private void FontSlider_Changed(object s, RangeBaseValueChangedEventArgs e) => SetZoom(_zoom);

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
                catch { }
            }
        }
    }

    // ================= BARRA FORMATTAZIONE =================
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
        b.Focus(FocusState.Programmatic);
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
        var joined = string.Join('\n', lines);
        b.Text = b.Text[..lineStart] + joined + b.Text[lineEnd..];
        b.Select(lineStart, joined.Length);
        b.Focus(FocusState.Programmatic);
    }

    private void FormatHeading_Click(object s, RoutedEventArgs e)
    {
        var b = EditableBox(); if (b == null) return;
        int caret = b.SelectionStart;
        int ls = caret;
        while (ls > 0 && b.Text[ls - 1] != '\n') ls--;
        b.Text = b.Text.Insert(ls, "# ");
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
        string sel = string.IsNullOrEmpty(b.SelectedText) ? "testo" : b.SelectedText;
        int start = b.SelectionStart;
        string rep = $"[{sel}](https://)";
        if (b.SelectionLength > 0) b.SelectedText = rep;
        else b.Text = b.Text.Insert(start, rep);
        b.Select(start + 1, sel.Length);
        b.Focus(FocusState.Programmatic);
    }
    private void FormatQuote_Click(object s, RoutedEventArgs e) => PrefixLines("> ");
    private void FormatBullets_Click(object s, RoutedEventArgs e) => PrefixLines("- ");
    private void FormatNumbers_Click(object s, RoutedEventArgs e) => PrefixLines("1. ");
}
