using System.Drawing.Drawing2D;

namespace TokenUsageReader;

/// <summary>Draws a ContextMenuStrip like a native Windows 11 menu: flat, rounded hover pill, light/dark palette.</summary>
internal sealed class Win11MenuRenderer : ToolStripRenderer
{
    private const string CheckGlyph = "";

    public MenuPalette Palette { get; set; } = Theme.MenuPalette;

    private static float Scale(ToolStrip? strip) => (strip?.DeviceDpi ?? 96) / 96f;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Palette.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    // Border, rounded corners and shadow come from DWM (see WindowChrome).
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var s = Scale(e.ToolStrip);
        var rect = new RectangleF(4 * s, 1 * s, e.Item.Width - 8 * s, e.Item.Height - 2 * s);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.RoundedRect(rect, 4 * s);
        using var brush = new SolidBrush(Palette.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Info rows are disabled items: show them as secondary text instead of the washed-out system gray;
        // bold section headers keep the primary color.
        var color = e.Item.Enabled || e.TextFont?.Bold == true ? Palette.Text : Palette.SecondaryText;
        // ToolStrip lays the text rectangle out from the font height and anchors it to the top of the item once
        // the item has extra padding; center it on the whole item height instead.
        var rect = new Rectangle(e.TextRectangle.X, 0, e.Item.Width - e.TextRectangle.X, e.Item.Height);
        var flags = (e.TextFormat & ~(TextFormatFlags.Bottom | TextFormatFlags.WordBreak))
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rect, color, flags);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var s = Scale(e.ToolStrip);
        var y = e.Item.Height / 2;
        using var pen = new Pen(Palette.Separator, Math.Max(1, (int)s));
        e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var s = Scale(e.ToolStrip);
        using var font = Theme.CreateIconFont(12 * s);
        // Like the text, the check rectangle is anchored to the top once the item has extra padding:
        // keep its horizontal position but center it on the whole item height.
        var rect = new Rectangle(e.ImageRectangle.X, 0, e.ImageRectangle.Width, e.Item.Height);
        TextRenderer.DrawText(e.Graphics, CheckGlyph, font, rect, Palette.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
