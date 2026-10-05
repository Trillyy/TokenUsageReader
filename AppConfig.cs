using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TokenUsageReader.Providers;

namespace TokenUsageReader;

public sealed partial class AppConfig
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenUsageReader");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    public const int MinRefreshMinutes = 1, MaxRefreshMinutes = 1440;
    public const int MinPremiumQuota = 1, MaxPremiumQuota = 100_000;

    /// <summary>UI language; defaults to Italian on an Italian Windows, English otherwise.</summary>
    public AppLanguage Language { get; set; } = SystemLanguage();
    public bool AutoRefresh { get; set; } = true;
    public int RefreshMinutes { get; set; } = 5;
    public ClaudeConfig Claude { get; set; } = new();
    public CopilotConfig Copilot { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Loads the config, never throwing for a bad file: an unreadable one is moved aside to config.json.bak
    /// and replaced by the defaults, with <paramref name="warning"/> telling the user what happened.
    /// </summary>
    public static AppConfig Load(out string? warning)
    {
        var cfg = LoadCore(out var warn);
        // The warning is worded only now, once the language to use is known.
        Strings.SetLanguage(cfg.Language);
        warning = warn?.Invoke();
        return cfg;
    }

    private static AppConfig LoadCore(out Func<string>? warning)
    {
        warning = null;
        Directory.CreateDirectory(Dir);

        AppConfig? cfg = null;
        var save = !File.Exists(FilePath);
        if (!save)
        {
            try
            {
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                try
                {
                    File.Copy(FilePath, FilePath + ".bak", overwrite: true);
                    save = true; // only overwrite the broken file once a copy of it is safe
                    warning = () => Strings.ConfigInvalidReset(ex.Message);
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                {
                    warning = () => Strings.ConfigInvalidNotSaved(ex.Message);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = () => Strings.ConfigUnreadable(ex.Message);
            }
        }

        cfg ??= new AppConfig();
        cfg.Normalize();

        try
        {
            cfg.Copilot.Token = WindowsCredentials.ReadSecret(CopilotConfig.CredentialTarget);
        }
        catch (Exception ex)
        {
            warning ??= () => Strings.CredentialReadFailed(ex.Message);
            return cfg; // saving now could delete the token we failed to read
        }

        // Older versions stored the token in plain text in config.json: move it to the Credential Manager.
        if (cfg.Copilot.LegacyToken is { } legacy)
        {
            if (string.IsNullOrWhiteSpace(cfg.Copilot.Token)) cfg.Copilot.Token = legacy;
            save = true;
        }

        if (save)
        {
            try { cfg.Save(); }
            catch (Exception ex) { warning ??= () => Strings.CannotSaveConfig(ex.Message); }
        }
        return cfg;
    }

    /// <summary>Clamps values edited by hand (or nulled sections) into the ranges the app can work with.</summary>
    private void Normalize()
    {
        Claude ??= new ClaudeConfig();
        Copilot ??= new CopilotConfig();
        RefreshMinutes = Math.Clamp(RefreshMinutes, MinRefreshMinutes, MaxRefreshMinutes);
        Copilot.MonthlyPremiumQuota = Math.Clamp(Copilot.MonthlyPremiumQuota, MinPremiumQuota, MaxPremiumQuota);
        if (!Enum.IsDefined(Copilot.Mode)) Copilot.Mode = CopilotMode.Internal;
        if (!Enum.IsDefined(Language)) Language = SystemLanguage();
        Copilot.Username = string.IsNullOrWhiteSpace(Copilot.Username) ? null : Copilot.Username.Trim();
    }

    /// <summary>Stores the token in the Credential Manager and writes config.json atomically.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);

        if (string.IsNullOrWhiteSpace(Copilot.Token)) WindowsCredentials.DeleteSecret(CopilotConfig.CredentialTarget);
        else WindowsCredentials.WriteSecret(CopilotConfig.CredentialTarget, Copilot.Token);
        Copilot.LegacyToken = null;

        // Write next to the target and swap it in, so a crash or full disk never leaves a truncated config.json.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppConfig Clone()
    {
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
        copy.Copilot.Token = Copilot.Token; // not serialized
        return copy;
    }

    private static AppLanguage SystemLanguage() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? AppLanguage.Italian : AppLanguage.English;

    /// <summary>GitHub usernames: alphanumerics and single hyphens, no leading/trailing hyphen, max 39 chars.</summary>
    public static bool IsValidGitHubUsername(string? name) => name is not null && GitHubUsername().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex GitHubUsername();
}

public sealed class ClaudeConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Use the (undocumented) OAuth usage endpoint for Pro/Max limits.</summary>
    public bool ShowSubscriptionLimits { get; set; } = true;
    /// <summary>Sum token usage from the local Claude Code JSONL logs.</summary>
    public bool ShowLocalTokens { get; set; } = true;
}

/// <summary>Stored in config.json as "it" / "en".</summary>
public enum AppLanguage
{
    [JsonStringEnumMemberName("it")] Italian,
    [JsonStringEnumMemberName("en")] English,
}

public enum CopilotMode { Internal, Billing }

public sealed class CopilotConfig
{
    /// <summary>Credential Manager entry holding <see cref="Token"/>.</summary>
    public const string CredentialTarget = "TokenUsageReader:copilot-token";

    public bool Enabled { get; set; } = true;
    /// <summary>
    /// internal: api.github.com/copilot_internal/user (undocumented, gives % of premium quota).
    /// billing: documented premium_request/usage endpoint (needs fine-grained PAT with "Plan: read").
    /// </summary>
    public CopilotMode Mode { get; set; } = CopilotMode.Internal;
    /// <summary>
    /// Leave empty to use the `copilot login` credential, GITHUB_TOKEN env var or `gh auth token`.
    /// Kept in the Windows Credential Manager, never in config.json.
    /// </summary>
    [JsonIgnore]
    public string? Token { get; set; }
    /// <summary>Plaintext token written by older versions; only read, to migrate it.</summary>
    [JsonPropertyName("token"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyToken { get; set; }
    /// <summary>Count premium requests from the local Copilot CLI sessions (~/.copilot/session-state).</summary>
    public bool ShowLocalCliUsage { get; set; } = true;
    /// <summary>Billing mode only.</summary>
    public string? Username { get; set; }
    /// <summary>Billing mode only: monthly premium requests included in your plan (Pro = 300, Pro+ = 1500).</summary>
    public int MonthlyPremiumQuota { get; set; } = 300;
}
