using System.Text.Json;

namespace TokenUsageReader.Providers;

/// <summary>Local data of the GitHub Copilot CLI (`copilot` command), stored under ~/.copilot.</summary>
internal static class CopilotCli
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// OAuth token saved by `copilot login`: it lives in the Credential Manager under
    /// "{host}:{login}.copilot-cli", where host/login come from lastLoggedInUser in ~/.copilot/config.json.
    /// Only github.com logins are used: the token is sent to api.github.com, so a GitHub Enterprise
    /// token must never be picked up here.
    /// </summary>
    public static string? ReadToken()
    {
        var configPath = Path.Combine(Dir, "config.json");
        if (!File.Exists(configPath)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath), Lenient);
        if (!doc.RootElement.TryGetProperty("lastLoggedInUser", out var user) || user.ValueKind != JsonValueKind.Object) return null;
        var host = user.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        var login = user.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(login) || !IsGitHubDotCom(host)) return null;

        return WindowsCredentials.ReadSecret($"{host}:{login}.copilot-cli");
    }

    private static bool IsGitHubDotCom(string host) =>
        Uri.TryCreate(host.Contains("://") ? host : "https://" + host, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Premium requests consumed by CLI sessions today and this month. Each session's events.jsonl logs
    /// cumulative totals (session.usage_checkpoint / session.shutdown); per session we take the growth of
    /// the counter inside each period. Usage from IDEs or github.com is not included.
    /// </summary>
    public static (int Today, int Month) ReadPremiumRequests(CancellationToken ct)
    {
        var sessions = new DirectoryInfo(Path.Combine(Dir, "session-state"));
        if (!sessions.Exists) return (0, 0);

        var todayStart = DateTime.Today;
        var monthStart = new DateTime(todayStart.Year, todayStart.Month, 1);
        var today = DateOnly.FromDateTime(todayStart);
        var firstOfMonth = DateOnly.FromDateTime(monthStart);
        int todayCount = 0, monthCount = 0;

        var files = sessions.EnumerateFiles("events.jsonl", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTime >= monthStart);
        foreach (var s in SessionLogs.Update(files, ct))
        {
            // The counter only grows: its value before a period is the highest one logged on an earlier day.
            monthCount += s.Last - s.HighestBefore(firstOfMonth);
            todayCount += s.Last - s.HighestBefore(today);
        }

        return (todayCount, monthCount);
    }

    /// <summary>Premium request counter of one session, with its value at the end of each day it was logged.</summary>
    private sealed class SessionCounter
    {
        public int Last;
        public readonly Dictionary<DateOnly, int> EndOfDay = [];

        public int HighestBefore(DateOnly day) => EndOfDay.Where(d => d.Key < day).Select(d => d.Value).DefaultIfEmpty(0).Max();
    }

    // Incremental reader (see JsonlTail): only lines appended since the previous refresh are parsed.
    // The pre-filter matters: these lines are rare, the others can be several hundred KB.
    private static readonly JsonlTail<SessionCounter> SessionLogs = new("\"totalPremiumRequests\""u8.ToArray(), AddCheckpoint);

    private static void AddCheckpoint(SessionCounter s, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (type is not ("session.usage_checkpoint" or "session.shutdown")) return;
        if (!root.TryGetProperty("timestamp", out var tsEl) || !tsEl.TryGetDateTime(out var ts)) return;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("totalPremiumRequests", out var n)
            || n.ValueKind != JsonValueKind.Number) return;

        var value = (int)Math.Clamp(n.GetDouble(), 0, int.MaxValue);
        s.Last = Math.Max(s.Last, value);
        s.EndOfDay[DateOnly.FromDateTime(ts.ToLocalTime())] = s.Last;
    }
}
