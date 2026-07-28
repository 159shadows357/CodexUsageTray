namespace CodexUsageTray;

public sealed class AdaptiveRefreshPolicy
{
    private const int IdleRefreshMinutes = 5;
    private int _unchangedRefreshes;

    public int UnchangedRefreshes => _unchangedRefreshes;

    public TimeSpan RecordSuccess(
        UsageSnapshot? previous,
        UsageSnapshot current,
        int configuredMinutes,
        bool usageSurfaceVisible,
        PowerLineStatus powerLineStatus)
    {
        var changed = HasUsageChanged(previous, current);
        if (changed)
        {
            _unchangedRefreshes = 0;
        }
        else
        {
            _unchangedRefreshes++;
        }

        if (changed || usageSurfaceVisible)
        {
            return TimeSpan.FromMinutes(1);
        }

        var configured = Math.Clamp(configuredMinutes, 1, 60);
        if (powerLineStatus == PowerLineStatus.Offline || _unchangedRefreshes >= 3)
        {
            return TimeSpan.FromMinutes(Math.Max(configured, IdleRefreshMinutes));
        }

        return TimeSpan.FromMinutes(configured);
    }

    internal static bool HasUsageChanged(UsageSnapshot? previous, UsageSnapshot current)
    {
        if (previous is null || !previous.IsLive)
        {
            return true;
        }

        if (previous.Status != current.Status ||
            !string.Equals(previous.PlanType, current.PlanType, StringComparison.Ordinal) ||
            previous.ResetCreditsAvailable != current.ResetCreditsAvailable ||
            previous.TokenActivity.TodayOrLatestTokens != current.TokenActivity.TodayOrLatestTokens ||
            previous.TokenActivity.TodayOrLatestDate != current.TokenActivity.TodayOrLatestDate ||
            previous.TokenActivity.LifetimeTokens != current.TokenActivity.LifetimeTokens ||
            previous.ApiEquivalent.TotalTokens != current.ApiEquivalent.TotalTokens ||
            previous.ApiEquivalent.UnpricedTokens != current.ApiEquivalent.UnpricedTokens ||
            previous.ApiEquivalent.EstimatedUsd != current.ApiEquivalent.EstimatedUsd ||
            previous.Buckets.Count != current.Buckets.Count)
        {
            return true;
        }

        var previousBuckets = previous.Buckets
            .OrderBy(bucket => bucket.Key, StringComparer.Ordinal)
            .Select(ToFingerprint);
        var currentBuckets = current.Buckets
            .OrderBy(bucket => bucket.Key, StringComparer.Ordinal)
            .Select(ToFingerprint);
        if (!previousBuckets.SequenceEqual(currentBuckets))
        {
            return true;
        }

        var previousCredits = previous.ResetCredits?
            .OrderBy(credit => credit.ExpiresAtUnix)
            .ThenBy(credit => credit.Id, StringComparer.Ordinal)
            .Select(ToFingerprint);
        var currentCredits = current.ResetCredits?
            .OrderBy(credit => credit.ExpiresAtUnix)
            .ThenBy(credit => credit.Id, StringComparer.Ordinal)
            .Select(ToFingerprint);
        return previousCredits is null != currentCredits is null ||
               previousCredits is not null && currentCredits is not null && !previousCredits.SequenceEqual(currentCredits);
    }

    private static BucketFingerprint ToFingerprint(RateLimitBucket bucket) => new(
        bucket.Key,
        bucket.UsedPercent,
        bucket.RemainingPercent,
        bucket.ResetsAtUnix,
        bucket.RateLimitReachedType);

    private static ResetCreditFingerprint ToFingerprint(RateLimitResetCredit credit) => new(
        credit.Id,
        credit.ResetType,
        credit.Status,
        credit.ExpiresAtUnix);

    private readonly record struct BucketFingerprint(
        string Key,
        int UsedPercent,
        int RemainingPercent,
        long? ResetsAtUnix,
        string? ReachedType);

    private readonly record struct ResetCreditFingerprint(
        string Id,
        string ResetType,
        string Status,
        long? ExpiresAtUnix);
}
