using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FluentNotepad;

/// <summary>
/// Mica reale via DWM (Windows 11 22H2+, build 22621+).
/// Su altri sistemi ritorna false e l'app usa i colori statici + ombra finta.
/// Escape hatch per test: FLUENTNOTEPAD_NO_MICA=1 forza il fallback.
/// </summary>
public static class DwmBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMSBT_MAINWINDOW = 2;   // Mica classico
    private const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt

    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    public static bool IsSupported()
    {
        if (Environment.GetEnvironmentVariable("FLUENTNOTEPAD_NO_MICA") == "1") return false;
        try { return Environment.OSVersion.Version.Build >= 22621; }
        catch { return false; }
    }

    public static bool TryEnable(Window window, bool dark, bool alt = false)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            int backdrop = alt ? DWMSBT_TABBEDWINDOW : DWMSBT_MAINWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
                return false;
            // Estende il frame (col materiale Mica) a tutta l'area client,
            // altrimenti le zone trasparenti restano nere
            var all = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref all) != 0)
                return false;
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            SetDarkMode(window, dark);
            return true;
        }
        catch { return false; }
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }
        catch { }
    }
}
