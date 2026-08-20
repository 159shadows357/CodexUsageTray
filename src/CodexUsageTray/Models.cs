using System.Text.Json.Serialization;

namespace CodexUsageTray;

public enum ConnectionStatus
{
    Loading,
    Connected,
    DataStale,
    NotSignedIn,
    CliNotFound,
    AppServerUnavailable,
    RateLimitReached
}

public enum PopupStyle
{
    Classic,
    MacOS,
    Paper
}

public enum UpdateMode
{
    Off,
    NotifyOnly,
    Automatic
}

public enum ResetCreditMode
{
    Off,
    NotifyBeforeExpiry,
    UseBeforeExpiry
}

public enum ResetCreditConsumeOutcome
{
    Reset,
    NothingToReset,
    NoCredit,
    AlreadyRedeemed,
    Unknown
}

public sealed record RateLimitBucket(
    string Key,
    string Name,
    string? LimitId,
    string Slot,
    int UsedPercent,
    int RemainingPercent,
    long? WindowDurationMins,
    long? ResetsAtUnix,
    DateTimeOffset? ResetsAtLocal,
    string? RateLimitReachedType);

public sealed record RateLimitResetCredit(
    string Id,
    string ResetType,
    string Status,
    long GrantedAtUnix,
    DateTimeOffset? GrantedAtLocal,
    long? ExpiresAtUnix,
    DateTimeOffset? ExpiresAtLocal,
    string? Title,
    string? Description);

public sealed record ResetCreditSummary(
    long? AvailableCount,
    IReadOnlyList<RateLimitResetCredit>? Credits);

public sealed record TokenActivity(
    long? TodayOrLatestTokens,
    string? TodayOrLatestDate,
    long? LifetimeTokens,
    long? PeakDailyTokens,
    bool IsAvailable);

public sealed record DailyUsageEstimate(
    DateOnly Date,
    long TotalTokens,
    long PricedTokens,
    long UnpricedTokens,
    decimal EstimatedUsd);

public sealed record ProjectUsageEstimate(
    string Name,
    long TotalTokens,
    decimal EstimatedUsd,
    long UnpricedTokens)
{
    public List<DailyUsageEstimate> DailyUsage { get; init; } = [];
}

public sealed record ApiEquivalentEstimate
{
    public bool IsAvailable { get; init; }
    public long TotalTokens { get; init; }
    public long PricedTokens { get; init; }
    public long UnpricedTokens { get; init; }
    public decimal EstimatedUsd { get; init; }
    public int ProjectCount { get; init; }
    public List<ProjectUsageEstimate> Projects { get; init; } = [];
    public List<DailyUsageEstimate> DailyUsage { get; init; } = [];
    public long UndatedTokens { get; init; }
    public bool UsesProxyPricing { get; init; }
    public string PricingReferenceDate { get; init; } = ApiCostEstimator.PricingReferenceDate;
}

public sealed record UsageSnapshot
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public bool IsLive { get; init; }
    public string? PlanType { get; init; }
    public List<RateLimitBucket> Buckets { get; init; } = [];
    public long? ResetCreditsAvailable { get; init; }
    [JsonIgnore]
    public List<RateLimitResetCredit>? ResetCredits { get; init; }
    public TokenActivity TokenActivity { get; init; } = new(null, null, null, null, false);
    public ApiEquivalentEstimate ApiEquivalent { get; init; } = new();
    [JsonConverter(typeof(JsonStringEnumConverter<ConnectionStatus>))]
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Loading;
    public string? Error { get; init; }

    [JsonIgnore]
    public RateLimitBucket? MostConstrained => Buckets.OrderBy(bucket => bucket.RemainingPercent).FirstOrDefault();

    [JsonIgnore]
    public RateLimitBucket? TrayBucket => SelectTrayBucket(Buckets);

    [JsonIgnore]
    public int? TrayRemainingPercent => TrayBucket is { } bucket ? EffectiveRemainingPercent(bucket) : null;

    internal static RateLimitBucket? SelectTrayBucket(IEnumerable<RateLimitBucket> buckets)
        => OrderTrayCandidates(buckets.Where(IsFrontierBucket)).FirstOrDefault();

    internal static bool IsFrontierBucket(RateLimitBucket bucket) =>
        string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase);

    internal static int EffectiveRemainingPercent(RateLimitBucket bucket) =>
        string.IsNullOrWhiteSpace(bucket.RateLimitReachedType) ? bucket.RemainingPercent : 0;

    private static IOrderedEnumerable<RateLimitBucket> OrderTrayCandidates(IEnumerable<RateLimitBucket> buckets) =>
        buckets
            .OrderBy(EffectiveRemainingPercent)
            .ThenBy(bucket => bucket.Slot.Equals("primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(bucket => bucket.Key, StringComparer.Ordinal);
}

public sealed record AppSettings
{
    public int RefreshIntervalMinutes { get; init; } = 2;
    public int StaleAfterMinutes { get; init; } = 10;
    public TrayIconStyle TrayIconStyle { get; init; } = TrayIconStyle.Ring;
    public PopupStyle PopupStyle { get; init; } = PopupStyle.Classic;
    public UpdateMode UpdateMode { get; init; } = UpdateMode.Off;
    public ResetCreditMode ResetCreditMode { get; init; } = ResetCreditMode.Off;
    public int ResetCreditLeadTimeMinutes { get; init; } = ResetCreditAutomation.DefaultLeadTimeMinutes;
}

public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public bool IsMethodUnavailable => Code == -32601 || Message.Contains("experimentalApi", StringComparison.OrdinalIgnoreCase);
}

public sealed class CodexCliNotFoundException(string message) : Exception(message);
