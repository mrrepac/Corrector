using System.Runtime.InteropServices;

namespace Corrector;

internal static class CorrectorIcon
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindow(string className, string? windowName);
    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);

    internal static Icon ForTray()
    {
        // NotifyIcon passes one HICON to Explorer. Select the small-icon frame
        // for the taskbar itself, even when the editor is on another monitor.
        uint dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
        int pixels = dpi == 0 ? SystemInformation.SmallIconSize.Width : GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON
        return Load(new Size(pixels, pixels));
    }

    internal static Icon Load(Size size)
    {
        using var frames = new Icon(typeof(CorrectorIcon), "CorrectorTray.ico");
        return new Icon(frames, size);
    }
}
