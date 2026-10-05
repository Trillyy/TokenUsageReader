using Microsoft.Win32;

namespace TokenUsageReader;

/// <summary>Colors of a Windows 11 context menu.</summary>
internal sealed record MenuPalette(
    Color Background, Color Text, Color SecondaryText, Color Hover, Color Separator, Color Border)
{
    public static readonly MenuPalette Light = new(
        Background: Rgb(0xF9F9F9), Text: Rgb(0x1A1A1A), SecondaryText: Rgb(0x5D5D5D),
        Hover: Rgb(0xECECEC), Separator: Rgb(0xE5E5E5), Border: Rgb(0xD9D9D9));

    public static readonly MenuPalette Dark = new(
        Background: Rgb(0x2C2C2C), Text: Rgb(0xFFFFFF), SecondaryText: Rgb(0xC5C5C5),
        Hover: Rgb(0x3A3A3A), Separator: Rgb(0x404040), Border: Rgb(0x454545));

    private static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// <summary>Colors of Windows 11 (WinUI) controls, flattened onto the window background.</summary>
internal sealed record ControlPalette(
    Color Window, Color Text, Color SecondaryText, Color DisabledText,
    Color Fill, Color FillHover, Color FillPressed, Color FillDisabled, Color FillInputFocused,
    Color Border, Color BorderBottom, Color StrongBorder, Color Accent, Color AccentHover, Color AccentPressed,
    Color TextOnAccent, Color SubtleHover)
{
    public static ControlPalette Create(bool dark, Color accent) => dark
        ? new(
            Window: Rgb(0x202020), Text: Rgb(0xFFFFFF), SecondaryText: Rgb(0xC5C5C5), DisabledText: Rgb(0x787878),
            Fill: Rgb(0x2D2D2D), FillHover: Rgb(0x323232), FillPressed: Rgb(0x272727), FillDisabled: Rgb(0x2A2A2A),
            FillInputFocused: Rgb(0x1F1F1F),
            Border: Rgb(0x373737), BorderBottom: Rgb(0x9A9A9A), StrongBorder: Rgb(0x9E9E9E),
            Accent: accent, AccentHover: Blend(accent, Rgb(0x202020), 0.9), AccentPressed: Blend(accent, Rgb(0x202020), 0.8),
            TextOnAccent: Rgb(0x000000), SubtleHover: Color.FromArgb(20, Color.White))
        : new(
            Window: Rgb(0xF3F3F3), Text: Rgb(0x1A1A1A), SecondaryText: Rgb(0x5D5D5D), DisabledText: Rgb(0xA0A0A0),
            Fill: Rgb(0xFBFBFB), FillHover: Rgb(0xF6F6F6), FillPressed: Rgb(0xF5F5F5), FillDisabled: Rgb(0xF5F5F5),
            FillInputFocused: Rgb(0xFFFFFF),
            Border: Rgb(0xE5E5E5), BorderBottom: Rgb(0x868686), StrongBorder: Rgb(0x8A8A8A),
            Accent: accent, AccentHover: Blend(accent, Rgb(0xF3F3F3), 0.9), AccentPressed: Blend(accent, Rgb(0xF3F3F3), 0.8),
            TextOnAccent: Rgb(0xFFFFFF), SubtleHover: Color.FromArgb(15, Color.Black));

    private static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static Color Blend(Color top, Color bottom, double alpha) => Color.FromArgb(
        (int)(top.R * alpha + bottom.R * (1 - alpha)),
        (int)(top.G * alpha + bottom.G * (1 - alpha)),
        (int)(top.B * alpha + bottom.B * (1 - alpha)));
}

internal static class Theme
{
    public static ControlPalette ControlPalette => ControlPalette.Create(IsDark, AccentColor);

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    /// <summary>"Choose your app mode" in Settings > Personalization > Colors.</summary>
    public static bool IsDark
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
    }

    public static MenuPalette MenuPalette => IsDark ? MenuPalette.Dark : MenuPalette.Light;

    /// <summary>
    /// System accent color in the shade Windows 11 uses for the current mode. AccentPalette holds 8 RGBA
    /// entries (Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, unused): dark mode uses Light2, light mode Dark1.
    /// </summary>
    public static Color AccentColor
    {
        get
        {
            var dark = IsDark;
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
            {
                var i = (dark ? 1 : 4) * 4;
                return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
            }
            return dark ? Color.FromArgb(0x60, 0xCD, 0xFF) : Color.FromArgb(0x00, 0x5F, 0xB8);
        }
    }

    /// <summary>Segoe UI Variable on Windows 11, Segoe UI elsewhere.</summary>
    public static Font CreateUiFont(float size, FontStyle style = FontStyle.Regular) =>
        new(HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI", size, style);

    /// <summary>Page title font (Segoe UI Variable Display Semibold on Windows 11).</summary>
    public static Font CreateTitleFont(float size) =>
        HasFont("Segoe UI Variable Display Semib")
            ? new Font("Segoe UI Variable Display Semib", size)
            : new Font("Segoe UI Semibold", size);

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    public static Font CreateIconFont(float sizePx) =>
        new(HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", sizePx, GraphicsUnit.Pixel);

    private static bool HasFont(string name) =>
        FontFamily.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
