using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FluentNotepad;

public partial class App : Application
{
    private readonly List<Window> _windows = new();
    private static int _cascade; // new windows would land exactly on the previous one (shared geometry)

    public App()
    {
        InitializeComponent();
    }

    public MainWindow NewWindow()
    {
        var win = new MainWindow();
        _windows.Add(win);
        win.Closed += (_, _) => _windows.Remove(win);
        win.Activate();
        // Cascade: every window restores the same saved geometry, offset the
        // 2nd+ so they don't stack pixel-perfect (like the OS does natively).
        if (_cascade > 0 && win.AppWindow.Presenter is OverlappedPresenter p &&
            p.State == OverlappedPresenterState.Restored)
        {
            try
            {
                var pos = win.AppWindow.Position;
                win.AppWindow.Move(new Windows.Graphics.PointInt32(
                    pos.X + 36 * _cascade, pos.Y + 36 * _cascade));
            }
            catch (Exception ex) { Debug.WriteLine($"Cascade: {ex.Message}"); }
        }
        _cascade++;
        return win;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var main = NewWindow();
        // Command-line file open: FluentNotepad.exe notes.md
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (File.Exists(arg))
            {
                main.OpenFileFromArgs(arg);
                break;
            }
        }
    }
}
