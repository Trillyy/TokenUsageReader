using System.Runtime.InteropServices;

namespace TokenUsageReader;

/// <summary>Windows 11 window styling for WinForms-drawn popups (ContextMenuStrip is not a native menu).</summary>
internal static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRoundSmall = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Gives the popup the same small rounded corners, border and shadow as native Windows 11 menus.
    /// No-op on Windows 10, where these attributes don't exist.
    /// </summary>
    public static void ApplyMenuStyle(Control popup, Func<Color> borderColor)
    {
        void Apply()
        {
            var corner = DwmwcpRoundSmall;
            DwmSetWindowAttribute(popup.Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var border = ColorTranslator.ToWin32(borderColor());
            DwmSetWindowAttribute(popup.Handle, DwmwaBorderColor, ref border, sizeof(int));
        }

        if (popup.IsHandleCreated) Apply();
        popup.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Re-applies the border color after a theme change (only if the window already exists).</summary>
    public static void UpdateBorderColor(Control popup, Color color)
    {
        if (!popup.IsHandleCreated) return;
        var border = ColorTranslator.ToWin32(color);
        DwmSetWindowAttribute(popup.Handle, DwmwaBorderColor, ref border, sizeof(int));
    }
}
