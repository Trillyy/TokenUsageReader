using System.Text.Json;

namespace TokenUsageReader.Providers;

/// <summary>
/// Claude API list prices, to estimate what the tokens in the local transcripts would cost at API rates.
/// Source: https://platform.claude.com/docs/en/about-claude/pricing (checked 2026-10-05). Update when prices change.
/// </summary>
internal static class ClaudePricing
{
    /// <summary>USD per million tokens. Cache writes are derived from input: 1.25x (5 min) and 2x (1 h).</summary>
    private sealed record Rates(double Input, double Output, double CacheRead);

    // First prefix that matches wins, so more specific ids come before the shorter ones they start with.
    private static readonly (string Prefix, Rates Rates)[] Table =
    [
        ("claude-fable-5-1", new(10, 50, 0.25)),
        ("claude-mythos-5-1", new(10, 50, 0.25)),
        ("claude-fable-5", new(10, 50, 1)),
        ("claude-mythos-5", new(10, 50, 1)),
        ("claude-opus-5-5", new(4, 20, 0.20)),
        ("claude-opus-5", new(5, 25, 0.50)),
        ("claude-opus-4-8", new(5, 25, 0.50)),
        ("claude-opus-4-7", new(5, 25, 0.50)),
        ("claude-opus-4-6", new(5, 25, 0.50)),
        ("claude-opus-4-5", new(5, 25, 0.50)),
        ("claude-opus-4", new(15, 75, 1.50)), // Opus 4 and 4.1
        ("claude-sonnet-5", new(2, 10, 0.20)), // Sonnet 5 and 5.5
        ("claude-sonnet-4", new(3, 15, 0.30)), // Sonnet 4, 4.5 and 4.6
        ("claude-haiku-4", new(1, 5, 0.10)),
        ("claude-3-5-haiku", new(0.80, 4, 0.08)),
    ];

    private const double WebSearchUsd = 10.0 / 1000;

    /// <summary>
    /// Estimated USD cost of one response's <paramref name="usage"/>, or null when the model isn't in the table.
    /// </summary>
    public static double? Cost(string? model, JsonElement usage)
    {
        if (model is null) return null;
        Rates? rates = null;
        foreach (var (prefix, r) in Table)
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { rates = r; break; }
        if (rates is null) return null;

        // The per-TTL split is only in newer transcripts; older ones count every cache write as 5 min.
        long write5m, write1h;
        if (usage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
        {
            write5m = Get(cc, "ephemeral_5m_input_tokens");
            write1h = Get(cc, "ephemeral_1h_input_tokens");
        }
        else
        {
            write5m = Get(usage, "cache_creation_input_tokens");
            write1h = 0;
        }

        var tokensUsd = (Get(usage, "input_tokens") * rates.Input
                         + write5m * rates.Input * 1.25
                         + write1h * rates.Input * 2
                         + Get(usage, "cache_read_input_tokens") * rates.CacheRead
                         + Get(usage, "output_tokens") * rates.Output) / 1e6;

        // Fast mode doubles every token rate; US-only inference adds 10%. Both stack with the cache multipliers.
        if (Str(usage, "speed") == "fast") tokensUsd *= 2;
        if (Str(usage, "inference_geo") == "us") tokensUsd *= 1.1;

        var searches = usage.TryGetProperty("server_tool_use", out var stu) && stu.ValueKind == JsonValueKind.Object
            ? Get(stu, "web_search_requests") : 0;
        return tokensUsd + searches * WebSearchUsd;
    }

    private static long Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
