namespace TokenUsageReader;

/// <summary>
/// Configuration UI. Works on a copy of the config; <see cref="Result"/> is saved only on OK. The language is
/// the exception: it applies (and is saved by the owner) as soon as it is picked, see <see cref="LanguageChanged"/>.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ControlPalette _palette = Theme.ControlPalette;
    // Re-apply every translated text of the form: run once at startup and again on each language change.
    private readonly List<Action> _texts = [];

    // Languages in the order of the combo box; each name is written in its own language.
    private static readonly (AppLanguage Language, string Name)[] Languages =
        [(AppLanguage.Italian, "Italiano"), (AppLanguage.English, "English")];

    // General
    private readonly FluentComboBox _language = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly FluentCheckBox _autoRefresh = new();
    private readonly FluentNumberBox _refreshMinutes = Number(AppConfig.MinRefreshMinutes, AppConfig.MaxRefreshMinutes);

    // Claude
    private readonly FluentCheckBox _claudeEnabled = new();
    private readonly FluentCheckBox _claudeLimits = new();
    private readonly FluentCheckBox _claudeTokens = new();
    private readonly FluentNumberBox _claudeBudget = Number(0, AppConfig.MaxApiBudgetUsd);

    // Copilot
    private readonly FluentCheckBox _copilotEnabled = new();
    private readonly FluentComboBox _copilotMode = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly FluentTextBox _copilotToken = new()
    {
        UseSystemPasswordChar = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly FluentCheckBox _copilotLocal = new();
    private readonly FluentTextBox _copilotUsername = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly FluentNumberBox _copilotQuota = Number(AppConfig.MinPremiumQuota, AppConfig.MaxPremiumQuota);

    public AppConfig Result => _cfg;

    /// <summary>Raised as soon as another language is picked, without waiting for Save.</summary>
    public event Action<AppLanguage>? LanguageChanged;

    public SettingsForm(AppConfig config)
    {
        _cfg = config.Clone();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.CreateUiFont(9f);
        Localize(this, () => Strings.SettingsTitle);
        ShowIcon = false;
        // Hidden in the title bar, but the taskbar button shows it: use the exe's icon, not the WinForms default.
        if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 440);
        BackColor = _palette.Window;
        ForeColor = _palette.Text;

        _language.AddItems(Languages.Select(l => l.Name).ToArray());
        _copilotMode.AddItems(Strings.CopilotModeInternal, Strings.CopilotModeBilling);
        _texts.Add(() => _copilotMode.SetItems(Strings.CopilotModeInternal, Strings.CopilotModeBilling));
        _copilotToken.PlaceholderText = Strings.CopilotTokenPlaceholder;
        _texts.Add(() => _copilotToken.PlaceholderText = Strings.CopilotTokenPlaceholder);
        Localize(_autoRefresh, () => Strings.AutoRefresh);
        Localize(_claudeEnabled, () => Strings.ClaudeEnabled);
        Localize(_claudeLimits, () => Strings.ClaudeLimits);
        Localize(_claudeTokens, () => Strings.ClaudeLocalTokens);
        Localize(_copilotEnabled, () => Strings.CopilotEnabled);
        Localize(_copilotLocal, () => Strings.CopilotLocal);

        // "Claude" and "Copilot" are product names: the same in every language.
        var navigation = Navigation(
            Page(() => Strings.PageGeneral,
                Row(() => Strings.Language, _language),
                Row(null, _autoRefresh),
                Row(() => Strings.RefreshInterval, _refreshMinutes),
                Hint(() => Strings.HintGeneral)),
            Page(() => "Claude",
                Row(null, _claudeEnabled),
                Row(null, _claudeLimits),
                Row(null, _claudeTokens),
                Row(() => Strings.ApiBudget, _claudeBudget),
                Hint(() => Strings.HintClaude)),
            Page(() => "Copilot",
                Row(null, _copilotEnabled),
                Row(() => Strings.Mode, _copilotMode),
                Row(() => Strings.GitHubToken, _copilotToken),
                Row(() => Strings.Username, _copilotUsername),
                Row(() => Strings.PremiumQuota, _copilotQuota),
                Row(null, _copilotLocal),
                Hint(() => Strings.HintCopilot)));

        var ok = Localize(new FluentButton { Accent = true, Margin = new Padding(4, 3, 4, 3) }, () => Strings.Save);
        var cancel = Localize(new FluentButton { Margin = new Padding(4, 3, 0, 3) }, () => Strings.Cancel);
        var accent = _palette.Accent;
        var openFile = new LinkLabel
        {
            AutoSize = true,
            Margin = new Padding(3, 10, 12, 3),
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = accent,
            ActiveLinkColor = accent,
            VisitedLinkColor = accent,
        };
        Localize(openFile, () => Strings.OpenConfigFile);
        ok.Click += (_, _) => Save();
        // The form is shown modeless (Show, not ShowDialog), so DialogResult alone wouldn't close it.
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        openFile.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppConfig.FilePath) { UseShellExecute = true });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(20, 12, 20, 20),
        };
        buttons.Controls.AddRange([cancel, ok, openFile]);

        Controls.Add(navigation);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadValues();
        foreach (var c in new[] { _autoRefresh, _claudeEnabled, _claudeTokens, _copilotEnabled })
            c.CheckedChanged += (_, _) => UpdateEnabled();
        _copilotMode.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _language.SelectedIndexChanged += (_, _) => OnLanguagePicked();
        UpdateEnabled();
        ResumeLayout(true);
    }

    /// <summary>Applies the picked language right away, unlike every other setting.</summary>
    private void OnLanguagePicked()
    {
        if (_language.SelectedIndex < 0) return;
        var language = Languages[_language.SelectedIndex].Language;
        if (language == _cfg.Language) return;
        _cfg.Language = language;
        Strings.SetLanguage(language);
        LanguageChanged?.Invoke(language);

        SuspendLayout();
        foreach (var apply in _texts) apply();
        ResumeLayout(true);
    }

    /// <summary>Sets a control's text now and again after every language change.</summary>
    private T Localize<T>(T control, Func<string> text) where T : Control
    {
        control.Text = text();
        _texts.Add(() => control.Text = text());
        return control;
    }

    private void LoadValues()
    {
        _language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Language == _cfg.Language));
        _autoRefresh.Checked = _cfg.AutoRefresh;
        _refreshMinutes.Value = _cfg.RefreshMinutes;

        _claudeEnabled.Checked = _cfg.Claude.Enabled;
        _claudeLimits.Checked = _cfg.Claude.ShowSubscriptionLimits;
        _claudeTokens.Checked = _cfg.Claude.ShowLocalTokens;
        _claudeBudget.Value = _cfg.Claude.MonthlyApiBudgetUsd;

        _copilotEnabled.Checked = _cfg.Copilot.Enabled;
        _copilotMode.SelectedIndex = _cfg.Copilot.Mode == CopilotMode.Billing ? 1 : 0;
        _copilotToken.Value = _cfg.Copilot.Token ?? "";
        _copilotUsername.Value = _cfg.Copilot.Username ?? "";
        _copilotQuota.Value = _cfg.Copilot.MonthlyPremiumQuota;
        _copilotLocal.Checked = _cfg.Copilot.ShowLocalCliUsage;
    }

    private void UpdateEnabled()
    {
        _refreshMinutes.Enabled = _autoRefresh.Checked;

        _claudeLimits.Enabled = _claudeTokens.Enabled = _claudeEnabled.Checked;
        _claudeBudget.Enabled = _claudeEnabled.Checked && _claudeTokens.Checked;

        var copilot = _copilotEnabled.Checked;
        var billing = _copilotMode.SelectedIndex == 1;
        _copilotMode.Enabled = _copilotToken.Enabled = _copilotLocal.Enabled = copilot;
        _copilotUsername.Enabled = _copilotQuota.Enabled = copilot && billing;
    }

    private void Save()
    {
        var billing = _copilotMode.SelectedIndex == 1;

        if (_copilotEnabled.Checked && billing && string.IsNullOrWhiteSpace(_copilotUsername.Value))
        {
            Warn(Strings.BillingNeedsUsername);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_copilotUsername.Value) && !AppConfig.IsValidGitHubUsername(_copilotUsername.Value.Trim()))
        {
            Warn(Strings.InvalidUsername);
            return;
        }

        _cfg.Language = Languages[Math.Max(0, _language.SelectedIndex)].Language;
        _cfg.AutoRefresh = _autoRefresh.Checked;
        _cfg.RefreshMinutes = _refreshMinutes.Value;

        _cfg.Claude.Enabled = _claudeEnabled.Checked;
        _cfg.Claude.ShowSubscriptionLimits = _claudeLimits.Checked;
        _cfg.Claude.ShowLocalTokens = _claudeTokens.Checked;
        _cfg.Claude.MonthlyApiBudgetUsd = _claudeBudget.Value;

        _cfg.Copilot.Enabled = _copilotEnabled.Checked;
        _cfg.Copilot.Mode = billing ? CopilotMode.Billing : CopilotMode.Internal;
        _cfg.Copilot.Token = NullIfEmpty(_copilotToken.Value);
        _cfg.Copilot.Username = NullIfEmpty(_copilotUsername.Value);
        _cfg.Copilot.MonthlyPremiumQuota = _copilotQuota.Value;
        _cfg.Copilot.ShowLocalCliUsage = _copilotLocal.Checked;

        try
        {
            _cfg.Save();
        }
        catch (Exception ex)
        {
            Warn(Strings.CannotSaveConfig(ex.Message));
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // --- layout helpers ---

    private static FluentNumberBox Number(int min, int max) => new(min, max) { Width = 130 };

    private static (Func<string>? Label, Control Control) Row(Func<string>? label, Control control) => (label, control);

    private (Func<string>? Label, Control Control) Hint(Func<string> text) =>
        (null, Localize(new Label { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = _palette.SecondaryText, Margin = new Padding(3, 12, 3, 3) }, text));

    /// <summary>Windows 11 Settings-style layout: navigation list on the left, selected page on the right.</summary>
    private Control Navigation(params (Func<string> Title, Control Content)[] pages)
    {
        var dark = Theme.IsDark;
        // Win11 "subtle fill" brushes: translucent white on dark, translucent black on light.
        var selectedFill = dark ? Color.FromArgb(15, Color.White) : Color.FromArgb(10, Color.Black);
        var hoverFill = dark ? Color.FromArgb(11, Color.White) : Color.FromArgb(6, Color.Black);
        var accent = Theme.AccentColor;
        var hoverIndex = -1;

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 0) };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            ItemHeight = LogicalToDeviceUnits(36),
            BackColor = _palette.Window,
            ForeColor = _palette.Text,
        };
        // The items only give the count: the titles are drawn from pages, so a repaint shows the current language.
        list.Items.AddRange(pages.Select((_, i) => (object)i).ToArray());
        _texts.Add(list.Invalidate);

        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            var s = list.DeviceDpi / 96f;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(list.BackColor)) g.FillRectangle(bg, e.Bounds);

            var selected = (e.State & DrawItemState.Selected) != 0;
            var pill = new RectangleF(e.Bounds.X, e.Bounds.Y + 2 * s, e.Bounds.Width - 1, e.Bounds.Height - 4 * s);
            if (selected || e.Index == hoverIndex)
            {
                using var path = Shapes.RoundedRect(pill, 4 * s);
                using var brush = new SolidBrush(selected ? selectedFill : hoverFill);
                g.FillPath(brush, path);
            }
            if (selected)
            {
                var barHeight = 16 * s;
                var bar = new RectangleF(pill.X, pill.Y + (pill.Height - barHeight) / 2, 3 * s, barHeight);
                using var path = Shapes.RoundedRect(bar, 1.5f * s);
                using var brush = new SolidBrush(accent);
                g.FillPath(brush, path);
            }

            var textRect = Rectangle.Round(new RectangleF(pill.X + 14 * s, pill.Y, pill.Width - 16 * s, pill.Height));
            TextRenderer.DrawText(g, pages[e.Index].Title(), list.Font, textRect, list.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        list.MouseMove += (_, e) =>
        {
            var index = list.IndexFromPoint(e.Location);
            if (index == hoverIndex) return;
            hoverIndex = index;
            list.Invalidate();
        };
        list.MouseLeave += (_, _) =>
        {
            hoverIndex = -1;
            list.Invalidate();
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            host.Controls.Clear();
            host.Controls.Add(pages[list.SelectedIndex].Content);
            list.Invalidate();
        };
        list.SelectedIndex = 0;

        var navPanel = new Panel { Dock = DockStyle.Left, Width = 170, Padding = new Padding(8, 12, 0, 8) };
        navPanel.Controls.Add(list);

        var root = new Panel { Dock = DockStyle.Fill };
        root.Controls.Add(host);
        root.Controls.Add(navPanel);
        return root;
    }

    private (Func<string> Title, Control Content) Page(Func<string> title, params (Func<string>? Label, Control Control)[] rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (label, control) in rows)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label is null)
            {
                grid.Controls.Add(control, 0, row);
                grid.SetColumnSpan(control, 2);
            }
            else
            {
                grid.Controls.Add(Localize(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 12, 4) }, label), 0, row);
                control.Margin = new Padding(3, 4, 3, 4);
                grid.Controls.Add(control, 1, row);
            }
        }

        var heading = Localize(new Label
        {
            Font = Theme.CreateTitleFont(15f),
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 12),
        }, title);

        var page = new Panel { Dock = DockStyle.Fill };
        page.Controls.Add(grid);
        page.Controls.Add(heading);
        return (title, page);
    }
}
