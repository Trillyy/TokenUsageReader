using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace TokenUsageReader;

// Windows 11 (WinUI 3) look-alikes for the WinForms controls used by the settings window.
// Metrics follow WinUI: 32px tall, 4px corner radius, 1px border, accent bottom line on focused inputs.

internal static class Fluent
{
    public const float Radius = 4;
    public const string GlyphCheck = "";
    public const string GlyphChevronDown = "";
    public const string GlyphChevronUp = "";

    public static float Scale(Control c) => c.DeviceDpi / 96f;

    /// <summary>Paints the area outside the rounded shape with the parent's background.</summary>
    public static void ClearToParent(Control c, Graphics g) =>
        g.Clear(c.Parent?.BackColor ?? c.BackColor);

    public static void DrawGlyph(Graphics g, string glyph, float sizePx, Rectangle bounds, Color color)
    {
        using var font = Theme.CreateIconFont(sizePx);
        TextRenderer.DrawText(g, glyph, font, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static void DrawFocusRing(Graphics g, RectangleF bounds, float s, Color color)
    {
        using var path = Shapes.RoundedRect(RectangleF.Inflate(bounds, -s, -s), (Radius + 1) * s);
        using var pen = new Pen(color, 2 * s);
        g.DrawPath(pen, path);
    }
}

/// <summary>Rounded input frame that hosts a borderless TextBox (TextBox, NumberBox).</summary>
internal abstract class FluentInputBase : Control
{
    protected readonly ControlPalette P = Theme.ControlPalette;
    protected readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    private bool _hover;

    protected FluentInputBase()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // focus goes to the inner TextBox
        Size = new Size(200, 32);
        Controls.Add(Box);

        Box.GotFocus += (_, _) => StateChanged();
        Box.LostFocus += (_, _) => StateChanged();
        Box.MouseEnter += (_, _) => SetHover(true);
        Box.MouseLeave += (_, _) => SetHover(Bounds.Contains(Parent?.PointToClient(Cursor.Position) ?? Point.Empty));
        StateChanged();
    }

    private Color CurrentFill => !Enabled ? P.FillDisabled : Box.Focused ? P.FillInputFocused : _hover ? P.FillHover : P.Fill;

    /// <summary>Space reserved on the right for inline buttons.</summary>
    protected virtual int RightReserve => 0;

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        StateChanged();
    }

    protected void StateChanged()
    {
        Box.BackColor = CurrentFill;
        Box.ForeColor = Enabled ? P.Text : P.DisabledText;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        StateChanged();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); SetHover(true); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(false); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.X < Width - RightReserve) Box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = Fluent.Scale(this);
        var left = (int)(11 * s);
        Box.SetBounds(left, (Height - Box.Height) / 2, Math.Max(10, Width - left - RightReserve - (int)(6 * s)), Box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Fluent.Scale(this);
        Fluent.ClearToParent(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Shapes.RoundedRect(rect, Fluent.Radius * s);
        using (var fill = new SolidBrush(CurrentFill)) g.FillPath(fill, path);
        using (var pen = new Pen(Enabled ? P.Border : P.FillDisabled, 1)) g.DrawPath(pen, path);

        if (Enabled)
        {
            // Bottom stroke, clipped to the rounded shape: 1px gray, 2px accent while focused.
            var thickness = Box.Focused ? 2 * s : 1;
            g.SetClip(path);
            using var bottom = new SolidBrush(Box.Focused ? P.Accent : P.BorderBottom);
            g.FillRectangle(bottom, 0, Height - thickness, Width, thickness);
            g.ResetClip();
        }

        PaintExtras(g, s);
    }

    protected virtual void PaintExtras(Graphics g, float s) { }
}

internal sealed class FluentTextBox : FluentInputBase
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get => Box.Text; set => Box.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseSystemPasswordChar { get => Box.UseSystemPasswordChar; set => Box.UseSystemPasswordChar = value; }
}

/// <summary>WinUI NumberBox with inline up/down buttons.</summary>
internal sealed class FluentNumberBox : FluentInputBase
{
    private int _value;
    private int _hoverButton = -1; // 0 = up, 1 = down

    public FluentNumberBox(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
        _value = minimum;
        Box.Text = minimum.ToString();
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) Commit();
        };
        Box.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
        Box.LostFocus += (_, _) => Commit();
        Box.MouseWheel += (_, e) => Step(Math.Sign(e.Delta));
    }

    public int Minimum { get; }
    public int Maximum { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get
        {
            Commit();
            return _value;
        }
        set
        {
            _value = Math.Clamp(value, Minimum, Maximum);
            Box.Text = _value.ToString();
        }
    }

    private int ButtonWidth => (int)(28 * Fluent.Scale(this));
    protected override int RightReserve => ButtonWidth * 2 + (int)(4 * Fluent.Scale(this));

    private void Commit()
    {
        Value = int.TryParse(Box.Text, out var v) ? v : _value;
    }

    private void Step(int delta)
    {
        if (!Enabled) return;
        Commit();
        Value = _value + delta;
        Box.SelectionStart = Box.TextLength;
    }

    private Rectangle ButtonRect(int index)
    {
        var s = Fluent.Scale(this);
        var x = Width - (int)(4 * s) - ButtonWidth * (2 - index);
        return new Rectangle(x, (int)(4 * s), ButtonWidth, Height - (int)(8 * s));
    }

    private int HitButton(Point p) => ButtonRect(0).Contains(p) ? 0 : ButtonRect(1).Contains(p) ? 1 : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitButton(e.Location);
        if (hit == _hoverButton) return;
        _hoverButton = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverButton = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        switch (HitButton(e.Location))
        {
            case 0: Step(1); break;
            case 1: Step(-1); break;
        }
    }

    protected override void PaintExtras(Graphics g, float s)
    {
        for (var i = 0; i < 2; i++)
        {
            var rect = ButtonRect(i);
            if (Enabled && i == _hoverButton)
            {
                using var path = Shapes.RoundedRect(rect, Fluent.Radius * s);
                using var brush = new SolidBrush(P.SubtleHover);
                g.FillPath(brush, path);
            }
            Fluent.DrawGlyph(g, i == 0 ? Fluent.GlyphChevronUp : Fluent.GlyphChevronDown, 10 * s, rect,
                Enabled ? P.SecondaryText : P.DisabledText);
        }
    }
}

/// <summary>WinUI ComboBox: a button that opens a Windows 11-styled menu with the choices.</summary>
internal sealed class FluentComboBox : Control
{
    private readonly ControlPalette _p = Theme.ControlPalette;
    private readonly List<string> _items = [];
    private int _selectedIndex = -1;
    private bool _hover, _pressed, _open;

    public FluentComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(200, 32);
    }

    public event EventHandler? SelectedIndexChanged;

    public void AddItems(params string[] items)
    {
        _items.AddRange(items);
        Invalidate();
    }

    /// <summary>Replaces the item texts (e.g. after a language change) keeping the selection when it still exists.</summary>
    public void SetItems(params string[] items)
    {
        _items.Clear();
        _items.AddRange(items);
        if (_selectedIndex >= _items.Count) SelectedIndex = _items.Count - 1;
        Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            value = Math.Clamp(value, -1, _items.Count - 1);
            if (value == _selectedIndex) return;
            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenDropDown()
    {
        if (!Enabled || _items.Count == 0) return;
        var renderer = new Win11MenuRenderer();
        var menu = new ContextMenuStrip
        {
            Renderer = renderer,
            Font = Font,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Padding = new Padding(0, 4, 0, 4),
            MinimumSize = new Size(Width, 0),
        };
        WindowChrome.ApplyMenuStyle(menu, () => renderer.Palette.Border);
        var s = Fluent.Scale(this);
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(_items[i], null, (_, _) => SelectedIndex = index)
            {
                Checked = i == _selectedIndex,
                Padding = new Padding(0, (int)(5 * s), (int)(8 * s), (int)(5 * s)),
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            _open = false;
            Invalidate();
            BeginInvoke(menu.Dispose);
        };
        _open = true;
        Invalidate();
        menu.Show(this, new Point(0, Height + (int)(2 * s)));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed) return;
        _pressed = false;
        Invalidate();
        if (ClientRectangle.Contains(e.Location)) OpenDropDown();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Up: SelectedIndex = Math.Max(0, _selectedIndex - 1); break;
            case Keys.Down when e.Alt: OpenDropDown(); break;
            case Keys.Down: SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1); break;
            case Keys.Space or Keys.F4: OpenDropDown(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Fluent.Scale(this);
        Fluent.ClearToParent(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var fill = !Enabled ? _p.FillDisabled : _pressed || _open ? _p.FillPressed : _hover ? _p.FillHover : _p.Fill;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Shapes.RoundedRect(rect, Fluent.Radius * s))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using (var pen = new Pen(_p.Border, 1)) g.DrawPath(pen, path);
        }

        var textColor = Enabled ? _p.Text : _p.DisabledText;
        var chevronWidth = (int)(32 * s);
        var textRect = new Rectangle((int)(11 * s), 0, Width - (int)(11 * s) - chevronWidth, Height);
        if (_selectedIndex >= 0)
        {
            TextRenderer.DrawText(g, _items[_selectedIndex], Font, textRect, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        Fluent.DrawGlyph(g, Fluent.GlyphChevronDown, 10 * s, new Rectangle(Width - chevronWidth, 0, chevronWidth - (int)(4 * s), Height),
            Enabled ? _p.SecondaryText : _p.DisabledText);

        if (Focused && ShowFocusCues) Fluent.DrawFocusRing(g, ClientRectangle, s, _p.Text);
    }
}

/// <summary>WinUI CheckBox: 20px rounded box, accent fill and check glyph when checked.</summary>
internal sealed class FluentCheckBox : CheckBox
{
    private readonly ControlPalette _p = Theme.ControlPalette;
    private bool _hover, _pressed;

    public FluentCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = true;
    }

    private const int BoxSize = 20;
    private const int Gap = 8;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = Fluent.Scale(this);
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size((int)((BoxSize + Gap) * s) + text.Width + (int)(4 * s),
            Math.Max((int)((BoxSize + 12) * s), text.Height));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Fluent.Scale(this);
        Fluent.ClearToParent(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var size = BoxSize * s;
        var box = new RectangleF(1, (Height - size) / 2, size - 1, size - 1);
        using (var path = Shapes.RoundedRect(box, Fluent.Radius * s))
        {
            if (Checked)
            {
                var fill = !Enabled ? _p.DisabledText : _pressed ? _p.AccentPressed : _hover ? _p.AccentHover : _p.Accent;
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }
            else
            {
                var fill = !Enabled ? _p.FillDisabled : _pressed ? _p.FillPressed : _hover ? _p.FillHover : _p.FillInputFocused;
                using var brush = new SolidBrush(fill);
                using var pen = new Pen(Enabled ? _p.StrongBorder : _p.DisabledText, 1);
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
        if (Checked)
            Fluent.DrawGlyph(g, Fluent.GlyphCheck, 12 * s, Rectangle.Round(box), Enabled ? _p.TextOnAccent : _p.FillDisabled);

        var textRect = new Rectangle((int)((BoxSize + Gap) * s), 0, Width - (int)((BoxSize + Gap) * s), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? _p.Text : _p.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues) Fluent.DrawFocusRing(g, RectangleF.Inflate(box, 3 * s, 3 * s), s, _p.Text);
    }
}

/// <summary>
/// WinUI Button; <see cref="Accent"/> gives the filled primary style. Not derived from Button: in dark color
/// mode WinForms paints Buttons with its own renderer and ignores OnPaint overrides.
/// </summary>
internal sealed class FluentButton : Control, IButtonControl
{
    private readonly ControlPalette _p = Theme.ControlPalette;
    private bool _hover, _pressed, _keyPressed;

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Size = new Size(96, 32);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value) => Invalidate();

    public void PerformClick()
    {
        if (CanSelect) OnClick(EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != DialogResult.None && FindForm() is { } form) form.DialogResult = DialogResult;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Space) return;
        _keyPressed = _pressed = true;
        Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode != Keys.Space || !_keyPressed) return;
        _keyPressed = _pressed = false;
        Invalidate();
        PerformClick();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _keyPressed = _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Fluent.Scale(this);
        Fluent.ClearToParent(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Shapes.RoundedRect(rect, Fluent.Radius * s);
        Color fill, text;
        if (Accent)
        {
            fill = !Enabled ? _p.FillDisabled : _pressed ? _p.AccentPressed : _hover ? _p.AccentHover : _p.Accent;
            text = Enabled ? _p.TextOnAccent : _p.DisabledText;
        }
        else
        {
            fill = !Enabled ? _p.FillDisabled : _pressed ? _p.FillPressed : _hover ? _p.FillHover : _p.Fill;
            text = Enabled ? _pressed ? _p.SecondaryText : _p.Text : _p.DisabledText;
        }
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (!Accent || !Enabled)
        {
            using var pen = new Pen(_p.Border, 1);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues) Fluent.DrawFocusRing(g, ClientRectangle, s, _p.Text);
    }
}
