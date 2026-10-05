namespace TokenUsageReader.Providers;

public sealed record UsageLine(string Label, string Value, double? Percent = null);

public sealed record UsageResult(string Provider, IReadOnlyList<UsageLine> Lines, string? Error = null)
{
    public static UsageResult Fail(string provider, string error) => new(provider, [], error);

    public double? MaxPercent => Lines.Max(l => l.Percent);
}

public interface IUsageProvider
{
    string Name { get; }
    /// <summary>Minimum interval between real fetches; cached result is returned otherwise.</summary>
    TimeSpan MinInterval { get; }
    Task<UsageResult> FetchAsync(CancellationToken ct);
}
