using Microsoft.UI.Xaml;

namespace FluentNotepad;

public partial class App : Application
{
    private Window? m_window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        m_window = new MainWindow();
        var main = (MainWindow)m_window;
        main.Activate();
        // Apertura file da riga di comando: FluentNotepad.exe appunto.md
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
