using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TokenUsageReader;

/// <summary>Draws the tray icon: the highest usage percentage on a Claude-orange background.</summary>
internal static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(double? percent)
    {
        var size = SystemInformation.SmallIconSize.Width * 2; // render at 2x for crispness on scaled displays
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Claude orange (#D97757); gray while there is no data yet.
            var bg = percent is null ? Color.FromArgb(90, 90, 90) : Color.FromArgb(0xD9, 0x77, 0x57);
            using (var brush = new SolidBrush(bg))
            using (var path = Shapes.RoundedRect(new RectangleF(0, 0, size - 1, size - 1), size / 5f))
                g.FillPath(brush, path);

            var text = percent is { } p ? Math.Min(99, (int)Math.Round(p)).ToString() : "–";
            using var font = new Font("Segoe UI", size * (text.Length > 1 ? 0.52f : 0.62f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), fmt);
        }

        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }
}
