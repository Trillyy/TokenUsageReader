using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;
using TokenUsageReader.Providers;

namespace TokenUsageReader;

internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TokenUsageReader";

    private AppConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly List<IUsageProvider> _providers = [];
    private readonly Dictionary<IUsageProvider, (DateTime At, UsageResult Result)> _cache = [];
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly Win11MenuRenderer _menuRenderer = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private SettingsForm? _settings;
    private bool _refreshing;
    private bool _refreshAgain;
    private DateTime? _lastRefresh;
    private Font? _bold;

    public TrayContext(AppConfig config, string? startupWarning = null)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TokenUsageReader/1.0");
        _menu.Renderer = _menuRenderer;
        _menu.Font = Theme.CreateUiFont(9f);
        _menu.Padding = new Padding(0, 4, 0, 4);
        _menu.ShowImageMargin = false;
        _menu.ShowCheckMargin = false;
        WindowChrome.ApplyMenuStyle(_menu, () => _menuRenderer.Palette.Border);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _tray = new NotifyIcon
        {
            Icon = TrayIconRenderer.Render(null),
            Text = Strings.TrayLoading,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenuAtCursor();
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _timer.Tick += async (_, _) => await RefreshAsync(force: false);

        ApplyConfig(config);
        UpdateStartupPath();
        BuildMenu();
        if (startupWarning != null)
            _tray.ShowBalloonTip(10_000, Strings.AppName, startupWarning, ToolTipIcon.Warning);
        _ = RefreshAsync(force: true);
    }

    /// <summary>(Re)creates providers and the auto-refresh timer from <paramref name="config"/>.</summary>
    [MemberNotNull(nameof(_config))]
    private void ApplyConfig(AppConfig config)
    {
        _config = config;
        // Before recreating the providers: their labels are worded at fetch time, the cache is cleared below.
        Strings.SetLanguage(config.Language);
        _providers.Clear();
        _cache.Clear();

        if (config.Claude.Enabled) _providers.Add(new ClaudeProvider(config.Claude, _http));
        if (config.Copilot.Enabled) _providers.Add(new CopilotProvider(config.Copilot, _http));

        _timer.Stop();
        // Clamped again here: an out-of-range value would overflow the int and make Interval throw.
        _timer.Interval = Math.Clamp(config.RefreshMinutes, AppConfig.MinRefreshMinutes, AppConfig.MaxRefreshMinutes) * 60_000;
        if (config.AutoRefresh) _timer.Start();
    }

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing)
        {
            // A forced refresh (e.g. after a config change) must not be lost: rerun once the current one ends.
            _refreshAgain |= force;
            return;
        }
        _refreshing = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var tasks = _providers.ToList().Select(async p =>
            {
                if (!force && _cache.TryGetValue(p, out var c) && DateTime.Now - c.At < p.MinInterval) return;
                UsageResult result;
                try { result = await p.FetchAsync(cts.Token); }
                catch (OperationCanceledException) { result = UsageResult.Fail(p.Name, Strings.Timeout); }
                catch (Exception ex) { result = UsageResult.Fail(p.Name, ex.Message); }
                // The settings may have replaced the providers meanwhile: don't cache results of removed ones.
                if (_providers.Contains(p)) _cache[p] = (DateTime.Now, result);
            });
            await Task.WhenAll(tasks);
            _lastRefresh = DateTime.Now;
            UpdateUi();
        }
        catch (Exception ex)
        {
            // Runs from async void handlers (timer, menu): an escaping exception would end the app.
            _tray.Text = Truncate(Strings.TrayError(ex.Message), 127);
        }
        finally
        {
            _refreshing = false;
        }

        if (_refreshAgain)
        {
            _refreshAgain = false;
            await RefreshAsync(force: true);
        }
    }

    private IEnumerable<UsageResult> Results =>
        _providers.Where(_cache.ContainsKey).Select(p => _cache[p].Result);

    private void UpdateUi()
    {
        var max = Results.Max(r => r.MaxPercent);
        var old = _tray.Icon;
        _tray.Icon = TrayIconRenderer.Render(max);
        old?.Dispose();

        // NotifyIcon.Text is capped at 127 chars: one line per provider with only its usage percentages.
        var tip = string.Join("\n", Results.Select(r =>
        {
            var lines = r.Lines.Where(l => l.Percent.HasValue).ToList();
            // A single percentage needs no label; with several (Claude 5h/week) the label tells them apart.
            var percents = lines.Select(l => lines.Count == 1 ? $"{l.Percent:0}%" : $"{l.Label} {l.Percent:0}%").ToList();
            return percents.Count > 0
                ? $"{r.Provider}: {string.Join(" · ", percents)}"
                : $"{r.Provider}: {(r.Error != null ? Strings.TipError : Strings.TipNoData)}";
        }));
        if (tip.Length == 0) tip = Strings.TrayNoProviders;
        _tray.Text = tip.Length > 127 ? tip[..124] + "…" : tip;

        BuildMenu();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _bold ??= new Font(_menu.Font, FontStyle.Bold);
        var bold = _bold;

        foreach (var r in Results)
        {
            _menu.Items.Add(new ToolStripMenuItem(r.Provider) { Enabled = false, Font = bold });
            foreach (var l in r.Lines)
                _menu.Items.Add(new ToolStripMenuItem($"   {l.Label}: {l.Value}") { Enabled = false });
            if (r.Error != null)
                _menu.Items.Add(new ToolStripMenuItem($"   ⚠ {Truncate(r.Error, 80)}") { Enabled = false, ToolTipText = r.Error });
            _menu.Items.Add(new ToolStripSeparator());
        }

        if (_providers.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem(Strings.NoProviders) { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());
        }

        var updated = _lastRefresh is { } t ? Strings.RefreshNowLast(t) : Strings.RefreshNow;
        _menu.Items.Add(updated, null, async (_, _) => await RefreshAsync(force: true));
        var auto = _config.AutoRefresh ? Strings.AutoRefreshEvery(_config.RefreshMinutes) : Strings.AutoRefreshOff;
        _menu.Items.Add(new ToolStripMenuItem(auto) { Enabled = false });
        _menu.Items.Add(Strings.SettingsMenu, null, (_, _) => OpenSettings());
        // The label is the action the item performs, so it flips with the current state.
        _menu.Items.Add(IsStartupEnabled() ? Strings.DontStartWithWindows : Strings.StartWithWindows, null, (_, _) =>
        {
            ToggleStartup();
            BuildMenu();
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Strings.Exit, null, (_, _) => ExitThread());

        // Windows 11 menu metrics: taller rows and roomier separators than the WinForms defaults.
        var scale = _menu.DeviceDpi / 96f;
        foreach (ToolStripItem item in _menu.Items)
        {
            // Info rows (disabled, not bold) are packed tighter than headers and commands.
            var vertical = item is { Enabled: false, Font.Bold: false } ? 2 : 5;
            if (item is ToolStripSeparator) item.Margin = new Padding(0, (int)(2 * scale), 0, (int)(2 * scale));
            // Without check/image margins the text needs its own left inset.
            else item.Padding = new Padding((int)(8 * scale), (int)(vertical * scale), (int)(12 * scale), (int)(vertical * scale));
        }
    }

    /// <summary>Follows light/dark app mode changes made in Windows Settings while the app is running.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        var palette = Theme.MenuPalette;
        if (palette == _menuRenderer.Palette) return;
        _menuRenderer.Palette = palette;
        WindowChrome.UpdateBorderColor(_menu, palette.Border);
        _menu.Invalidate();
    }

    private void ShowMenuAtCursor()
    {
        // NotifyIcon only shows the context menu on right click; reuse its internal method for left click.
        typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_tray, null);
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }

        var form = new SettingsForm(_config);
        form.LanguageChanged += ApplyLanguage;
        form.FormClosed += (_, _) =>
        {
            _settings = null;
            if (form.DialogResult != DialogResult.OK) return;
            ApplyConfig(form.Result);
            BuildMenu();
            _ = RefreshAsync(force: true);
        };
        _settings = form;
        form.Show();
        form.Activate();
    }

    /// <summary>
    /// A language picked in the settings applies and is saved at once, even if the window is then cancelled;
    /// the other settings keep waiting for Save.
    /// </summary>
    private void ApplyLanguage(AppLanguage language)
    {
        if (_config.Language == language) return;
        _config.Language = language;
        Strings.SetLanguage(language);
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5_000, Strings.AppName, Strings.CannotSaveConfig(ex.Message), ToolTipIcon.Warning);
        }

        UpdateUi();
        // The usage lines were worded when fetched: fetch again to reword them (Claude's limits come from its cache).
        _ = RefreshAsync(force: true);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\"";

    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key.GetValue(RunValue) is string) key.DeleteValue(RunValue);
            else key.SetValue(RunValue, StartupCommand);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _tray.ShowBalloonTip(5_000, Strings.AppName, Strings.StartupToggleFailed(ex.Message), ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// If the exe was moved, points the existing startup entry at its current location. An entry whose exe
    /// still exists is left alone, so running another copy (e.g. a dev build) doesn't take it over.
    /// </summary>
    private static void UpdateStartupPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is string current && current != StartupCommand && !File.Exists(current.Trim('"')))
                key.SetValue(RunValue, StartupCommand);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        // SystemEvents is static: unsubscribe or it keeps this context alive.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settings?.Close();
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _http.Dispose();
        base.ExitThreadCore();
    }
}
