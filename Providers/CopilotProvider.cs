using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenUsageReader.Providers;

/// <summary>GitHub Copilot premium request usage.</summary>
public sealed class CopilotProvider(CopilotConfig cfg, HttpClient http) : IUsageProvider
{
    public string Name => "Copilot";
    public TimeSpan MinInterval => TimeSpan.Zero;

    public async Task<UsageResult> FetchAsync(CancellationToken ct)
    {
        var lines = new List<UsageLine>();
        var errors = new List<string>();

        try
        {
            var token = await ResolveTokenAsync(ct)
                ?? throw new InvalidOperationException(Strings.NoGitHubToken);
            lines.AddRange(cfg.Mode == CopilotMode.Billing
                ? await FetchBillingAsync(token, ct)
                : await FetchInternalAsync(token, ct));
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        if (cfg.ShowLocalCliUsage)
        {
            try
            {
                var (today, month) = await Task.Run(() => CopilotCli.ReadPremiumRequests(ct), ct);
                lines.Add(new UsageLine(Strings.LabelCliPremiumToday, today.ToString()));
                lines.Add(new UsageLine(Strings.LabelCliPremiumMonth, month.ToString()));
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        return new UsageResult(Name, lines, errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    private async Task<List<UsageLine>> FetchInternalAsync(string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://api.github.com/copilot_internal/user", token, ct);
        var root = doc.RootElement;
        var lines = new List<UsageLine>();

        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(Strings.UnexpectedGitHubResponse);

        if (root.TryGetProperty("copilot_plan", out var plan) && plan.ValueKind == JsonValueKind.String)
            lines.Add(new UsageLine(Strings.LabelPlan, plan.GetString() ?? "?"));

        if (root.TryGetProperty("quota_snapshots", out var snaps) && snaps.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, label) in new[] { ("premium_interactions", "Premium"), ("chat", "Chat"), ("completions", "Completions") })
            {
                if (!snaps.TryGetProperty(key, out var s) || s.ValueKind != JsonValueKind.Object) continue;
                if (s.TryGetProperty("unlimited", out var unl) && unl.ValueKind == JsonValueKind.True)
                {
                    if (key == "premium_interactions") lines.Add(new UsageLine(label, Strings.Unlimited));
                    continue;
                }
                // A snapshot missing its fields is skipped on its own instead of failing the whole provider.
                if (Number(s, "entitlement") is not { } entitlement) continue;
                // percent_remaining also reflects token-based billing; remaining alone may lag behind.
                double? pct = Number(s, "percent_remaining") is { } pr ? 100 - pr
                    : entitlement <= 0 ? 0
                    : Number(s, "remaining") is { } rem ? (entitlement - rem) / entitlement * 100
                    : null;
                if (pct is not { } p) continue;
                var used = entitlement * p / 100;
                lines.Add(new UsageLine(label, $"{used:0}/{entitlement:0} ({p:0}%)", p));
            }
        }

        if (root.TryGetProperty("quota_reset_date", out var reset) && reset.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(reset.GetString(), out var rd))
            lines.Add(new UsageLine(Strings.LabelReset, rd.LocalDateTime.ToString("dd/MM")));

        return lines;
    }

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private async Task<List<UsageLine>> FetchBillingAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.Username)) throw new InvalidOperationException(Strings.CopilotUsernameMissing);
        if (!AppConfig.IsValidGitHubUsername(cfg.Username)) throw new InvalidOperationException(Strings.CopilotUsernameInvalid);
        var now = DateTime.UtcNow;
        var url = $"https://api.github.com/users/{Uri.EscapeDataString(cfg.Username)}/settings/billing/premium_request/usage?year={now.Year}&month={now.Month}";
        using var doc = await GetJsonAsync(url, token, ct);

        double used = 0, cost = 0;
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("usageItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                used += Number(item, "grossQuantity") ?? 0;
                cost += Number(item, "netAmount") ?? 0;
            }
        }

        var pct = cfg.MonthlyPremiumQuota > 0 ? used / cfg.MonthlyPremiumQuota * 100 : (double?)null;
        var lines = new List<UsageLine> { new("Premium", $"{used:0}/{cfg.MonthlyPremiumQuota} ({pct:0}%)", pct) };
        if (cost > 0) lines.Add(new UsageLine("Extra", $"${cost:0.00}"));
        return lines;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await http.SendAsync(req, ct);
        // A revoked/rotated token: forget the cached `gh` one so the next refresh asks gh again.
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) _ghTokenAt = DateTime.MinValue;
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    // `gh auth token` is cached (failures included) so gh isn't spawned on every refresh,
    // but only for a while, so a logout/re-login is picked up.
    private static readonly TimeSpan GhTokenTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(10);
    private string? _ghToken;
    private DateTime _ghTokenAt = DateTime.MinValue;

    private async Task<string?> ResolveTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Token)) return cfg.Token;
        try
        {
            var cli = CopilotCli.ReadToken();
            if (!string.IsNullOrWhiteSpace(cli)) return cli;
        }
        catch (Exception) { } // unreadable ~/.copilot/config.json: fall through to the other sources
        var env = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        if (DateTime.Now - _ghTokenAt < GhTokenTtl) return _ghToken;
        _ghToken = await RunGhAuthTokenAsync(ct);
        _ghTokenAt = DateTime.Now;
        return _ghToken;
    }

    private static async Task<string?> RunGhAuthTokenAsync(CancellationToken ct)
    {
        if (FindOnPath("gh.exe") is not { } gh) return null; // gh not installed

        var psi = new ProcessStartInfo(gh, "auth token")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // gh must never sit waiting on a prompt
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        p.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GhTimeout);
        try
        {
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token); // drain so gh can't block on a full pipe
            var output = (await stdout).Trim();
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // already exited
            if (ct.IsCancellationRequested) throw;
            return null; // gh timed out
        }
    }

    /// <summary>
    /// Resolves an executable only from absolute PATH entries. Process.Start would also look in the app
    /// and current directories, where a planted gh.exe could run and hand us a token of its choosing.
    /// </summary>
    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0 || !Path.IsPathFullyQualified(d)) continue;
            var candidate = Path.Combine(d, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
