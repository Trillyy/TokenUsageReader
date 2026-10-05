using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenUsageReader.Providers;

/// <summary>
/// Claude usage from two sources:
///  - Pro/Max rate-limit windows via the OAuth usage endpoint Claude Code's /usage uses (undocumented).
///  - Token totals and their estimated API cost, parsed from the local Claude Code transcripts (~/.claude/projects/**/*.jsonl).
/// </summary>
public sealed class ClaudeProvider(ClaudeConfig cfg, HttpClient http) : IUsageProvider
{
    private static readonly string ClaudeDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    // The usage endpoint is rate limited (HTTP 429): never call it more often than this, whatever the
    // app refresh interval is, and back off as the server asks when it does return 429.
    private static readonly TimeSpan SubscriptionMinInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(5);

    // Static so the backoff survives the provider being recreated when the settings are saved;
    // otherwise saving during a 429 would hit the endpoint again right away.
    private static string? _lastUsageJson;
    private static DateTime _lastUsageAt;
    private static DateTime _nextCallAllowed;
    private static bool _throttled;

    public string Name => "Claude";
    public TimeSpan MinInterval => TimeSpan.Zero;

    public async Task<UsageResult> FetchAsync(CancellationToken ct)
    {
        var lines = new List<UsageLine>();
        var errors = new List<string>();

        if (cfg.ShowSubscriptionLimits)
        {
            try { lines.AddRange(await FetchSubscriptionAsync(ct)); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        if (cfg.ShowLocalTokens)
        {
            try { lines.AddRange(await Task.Run(() => ReadLocalTokens(ct), ct)); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        return new UsageResult(Name, lines, errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    private async Task<IEnumerable<UsageLine>> FetchSubscriptionAsync(CancellationToken ct)
    {
        if (DateTime.Now < _nextCallAllowed)
        {
            if (_lastUsageJson is null)
                throw new InvalidOperationException(Strings.TooManyRequestsRetryAt(_nextCallAllowed));
            return ParseSubscription(_lastUsageJson);
        }

        var credPath = Path.Combine(ClaudeDir, ".credentials.json");
        if (!File.Exists(credPath)) throw new InvalidOperationException(Strings.ClaudeCredentialsNotFound);

        string? token;
        DateTimeOffset expiresAt;
        try
        {
            using var credDoc = JsonDocument.Parse(await File.ReadAllTextAsync(credPath, ct));
            var oauth = credDoc.RootElement.ValueKind == JsonValueKind.Object
                        && credDoc.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.ValueKind == JsonValueKind.Object
                ? o : throw new InvalidOperationException(Strings.ClaudeNotLoggedIn);
            token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(token) || !oauth.TryGetProperty("expiresAt", out var e) || !e.TryGetInt64(out var ms))
                throw new InvalidOperationException(Strings.ClaudeCredentialsIncomplete);
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253402300799999));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(Strings.ClaudeCredentialsUnreadable);
        }
        // We don't refresh the token ourselves to avoid racing with Claude Code; it refreshes on next use.
        if (expiresAt < DateTimeOffset.UtcNow) throw new InvalidOperationException(Strings.ClaudeTokenExpired);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var wait = resp.Headers.RetryAfter?.Delta
                       ?? (resp.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                       ?? DefaultBackoff;
            wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 60, 3600));
            _nextCallAllowed = DateTime.Now + wait;
            _throttled = true;
            if (_lastUsageJson is null)
                throw new InvalidOperationException(Strings.TooManyRequestsRetryAt(_nextCallAllowed));
            return ParseSubscription(_lastUsageJson);
        }
        resp.EnsureSuccessStatusCode();

        _lastUsageJson = await resp.Content.ReadAsStringAsync(ct);
        _lastUsageAt = DateTime.Now;
        _nextCallAllowed = _lastUsageAt + SubscriptionMinInterval;
        _throttled = false;
        return ParseSubscription(_lastUsageJson);
    }

    /// <summary>Parsed from the raw JSON each time so "reset tra …" stays current when serving cached data.</summary>
    private List<UsageLine> ParseSubscription(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<UsageLine>();
        foreach (var (key, label) in new[] { ("five_hour", Strings.LabelSession5h), ("seven_day", Strings.LabelWeek), ("seven_day_opus", Strings.LabelWeekOpus), ("seven_day_sonnet", Strings.LabelWeekSonnet) })
        {
            if (!doc.RootElement.TryGetProperty(key, out var win) || win.ValueKind != JsonValueKind.Object) continue;
            if (!win.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) continue;
            var pct = u.GetDouble();
            var reset = win.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(r.GetString(), out var rd)
                ? Strings.ResetSuffix(FormatReset(rd)) : "";
            result.Add(new UsageLine(label, $"{pct:0}%{reset}", pct));
        }
        if (_throttled)
            result.Add(new UsageLine(Strings.LabelLimits, Strings.LimitsStale(_lastUsageAt, _nextCallAllowed)));
        return result;
    }

    // Local transcripts are read incrementally (see JsonlTail); static so the cache survives the provider
    // being recreated when the settings are saved. Message ids are de-duplicated across files (resumed
    // sessions copy earlier messages), hence one shared set and a full reset if any file gets rewritten.
    private static readonly HashSet<string> SeenMessages = [];
    private static DateTime _cachedMonth;
    private static readonly JsonlTail<Dictionary<DateOnly, DayUsage>> Transcripts = CreateTranscriptCache();

    /// <summary>One day's totals. <see cref="Unpriced"/>: some tokens came from a model missing from <see cref="ClaudePricing"/>.</summary>
    private sealed class DayUsage
    {
        public long Tokens;
        public double Cost;
        public bool Unpriced;

        public void Add(DayUsage other)
        {
            Tokens += other.Tokens;
            Cost += other.Cost;
            Unpriced |= other.Unpriced;
        }
    }

    private static JsonlTail<Dictionary<DateOnly, DayUsage>> CreateTranscriptCache()
    {
        var tail = new JsonlTail<Dictionary<DateOnly, DayUsage>>("\"usage\""u8.ToArray(), AddTokens) { ResetAllOnRewrite = true };
        tail.Cleared += SeenMessages.Clear;
        return tail;
    }

    /// <summary>Adds one transcript line's tokens and estimated cost to its day in the file's per-day totals.</summary>
    private static void AddTokens(Dictionary<DateOnly, DayUsage> days, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("timestamp", out var tsEl) || !tsEl.TryGetDateTime(out var ts)) return;
        ts = ts.ToLocalTime();
        // Older lines can never count again (the cache is dropped when the month changes): don't remember their ids.
        if (ts < MonthStart(DateTime.Today)) return;
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;

        // Streamed responses are logged several times with the same message id: count once.
        var id = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        var reqId = root.TryGetProperty("requestId", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
        if (id != null && !SeenMessages.Add($"{id}:{reqId}")) return;

        var tokens = Get(usage, "input_tokens") + Get(usage, "output_tokens")
                   + Get(usage, "cache_creation_input_tokens") + Get(usage, "cache_read_input_tokens");
        var day = DateOnly.FromDateTime(ts);
        if (!days.TryGetValue(day, out var totals)) days[day] = totals = new DayUsage();
        totals.Tokens += tokens;
        if (tokens == 0) return; // e.g. Claude Code's "<synthetic>" placeholder messages

        var model = msg.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
        if (ClaudePricing.Cost(model, usage) is { } cost) totals.Cost += cost;
        else totals.Unpriced = true;

        static long Get(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    private static DateTime MonthStart(DateTime day) => new(day.Year, day.Month, 1);

    private static IEnumerable<UsageLine> ReadLocalTokens(CancellationToken ct)
    {
        var projects = new DirectoryInfo(Path.Combine(ClaudeDir, "projects"));
        if (!projects.Exists) return [];

        var todayStart = DateTime.Today;
        var monthStart = MonthStart(todayStart);
        if (_cachedMonth != monthStart)
        {
            Transcripts.Clear();
            _cachedMonth = monthStart;
        }

        // Files untouched since the month started can't hold this month's tokens.
        var files = projects.EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTime >= monthStart);
        var today = DateOnly.FromDateTime(todayStart);
        DayUsage todayTotals = new(), monthTotals = new();
        foreach (var days in Transcripts.Update(files, ct))
        {
            if (days.TryGetValue(today, out var t)) todayTotals.Add(t);
            foreach (var d in days.Values) monthTotals.Add(d);
        }

        return [new UsageLine(Strings.LabelTokensToday, FormatUsage(todayTotals)), new UsageLine(Strings.LabelTokensMonth, FormatUsage(monthTotals))];
    }

    /// <summary>"12.3M · ≈ $4.56"; "≥" instead of "≈" when some tokens couldn't be priced.</summary>
    private static string FormatUsage(DayUsage u) =>
        u.Tokens == 0 ? FormatTokens(0) : Strings.TokensWithCost(FormatTokens(u.Tokens), u.Unpriced ? "≥" : "≈", u.Cost);

    internal static string FormatTokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.0}B",
        >= 1_000_000 => $"{n / 1e6:0.0}M",
        >= 1_000 => $"{n / 1e3:0.0}k",
        _ => n.ToString(),
    };

    internal static string FormatReset(DateTimeOffset at)
    {
        var left = at - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return Strings.ResetNow;
        return left.TotalHours >= 24 ? at.LocalDateTime.ToString("ddd HH:mm", Strings.Culture) : Strings.ResetIn((int)left.TotalHours, left.Minutes);
    }
}
