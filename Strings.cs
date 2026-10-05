using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace TokenUsageReader;

/// <summary>
/// User-visible texts, from Strings.resx (English, also the fallback) and Strings.it.resx (Italian).
/// Every member is named after its resource key; the members with arguments fill the {0}, {1}… placeholders.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Resources = new("TokenUsageReader.Strings", typeof(Strings).Assembly);

    /// <summary>Culture of the texts and of culture-dependent formats such as day names.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static void SetLanguage(AppLanguage language) =>
        Culture = CultureInfo.GetCultureInfo(language == AppLanguage.Italian ? "it" : "en");

    private static string Get([CallerMemberName] string key = "") => Resources.GetString(key, Culture) ?? key;
    private static string Format(object? a, [CallerMemberName] string key = "") => string.Format(Culture, Get(key), a);
    private static string Format(object? a, object? b, [CallerMemberName] string key = "") => string.Format(Culture, Get(key), a, b);

    /// <summary>Product name: the same in every language.</summary>
    public const string AppName = "Token Usage Reader";

    // --- App and tray ---
    public static string ErrorTitle => Get();
    public static string TrayLoading => Get();
    public static string TrayNoProviders => Get();
    public static string TrayError(string message) => Format(message);
    public static string TipError => Get();
    public static string TipNoData => Get();
    public static string NoProviders => Get();
    public static string RefreshNow => Get();
    public static string RefreshNowLast(DateTime last) => Format(last);
    public static string AutoRefreshEvery(int minutes) => Format(minutes);
    public static string AutoRefreshOff => Get();
    public static string SettingsMenu => Get();
    public static string StartWithWindows => Get();
    public static string DontStartWithWindows => Get();
    public static string Exit => Get();
    public static string StartupToggleFailed(string message) => Format(message);
    public static string Timeout => Get();

    // --- Settings ---
    public static string SettingsTitle => Get();
    public static string PageGeneral => Get();
    public static string AutoRefresh => Get();
    public static string RefreshInterval => Get();
    public static string Language => Get();
    public static string HintGeneral => Get();
    public static string ClaudeEnabled => Get();
    public static string ClaudeLimits => Get();
    public static string ClaudeLocalTokens => Get();
    public static string HintClaude => Get();
    public static string CopilotEnabled => Get();
    public static string Mode => Get();
    public static string CopilotModeInternal => Get();
    public static string CopilotModeBilling => Get();
    public static string GitHubToken => Get();
    public static string CopilotTokenPlaceholder => Get();
    public static string Username => Get();
    public static string PremiumQuota => Get();
    public static string CopilotLocal => Get();
    public static string HintCopilot => Get();
    public static string Save => Get();
    public static string Cancel => Get();
    public static string OpenConfigFile => Get();
    public static string BillingNeedsUsername => Get();
    public static string InvalidUsername => Get();
    public static string CannotSaveConfig(string message) => Format(message);

    // --- Config loading ---
    public static string ConfigInvalidReset(string message) => Format(message);
    public static string ConfigInvalidNotSaved(string message) => Format(message);
    public static string ConfigUnreadable(string message) => Format(message);
    public static string CredentialReadFailed(string message) => Format(message);

    // --- Claude ---
    public static string TooManyRequestsRetryAt(DateTime at) => Format(at);
    public static string ClaudeCredentialsNotFound => Get();
    public static string ClaudeNotLoggedIn => Get();
    public static string ClaudeCredentialsIncomplete => Get();
    public static string ClaudeCredentialsUnreadable => Get();
    public static string ClaudeTokenExpired => Get();
    public static string LabelSession5h => Get();
    public static string LabelWeek => Get();
    public static string LabelWeekOpus => Get();
    public static string LabelWeekSonnet => Get();
    public static string ResetSuffix(string when) => Format(when);
    public static string LabelLimits => Get();
    public static string LimitsStale(DateTime dataAt, DateTime retryAt) => Format(dataAt, retryAt);
    public static string LabelTokensToday => Get();
    public static string LabelTokensMonth => Get();
    public static string TokensWithCost(string tokens, string approx, double usd) => string.Format(Culture, Get(), tokens, approx, usd);
    public static string ResetNow => Get();
    public static string ResetIn(int hours, int minutes) => Format(hours, minutes);

    // --- Copilot ---
    public static string NoGitHubToken => Get();
    public static string UnexpectedGitHubResponse => Get();
    public static string LabelPlan => Get();
    public static string Unlimited => Get();
    public static string LabelReset => Get();
    public static string LabelCliPremiumToday => Get();
    public static string LabelCliPremiumMonth => Get();
    public static string CopilotUsernameMissing => Get();
    public static string CopilotUsernameInvalid => Get();
}
