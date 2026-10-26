namespace CodexUsageTray;

internal static class ForecastRefreshSchedule
{
    internal static DateTimeOffset OnPopupOpened(DateTimeOffset now, DateTimeOffset scheduled,
        DateTimeOffset nextAllowedRequestAt)
    {
        var allowed = nextAllowedRequestAt > now ? nextAllowedRequestAt : now;
        return allowed < scheduled ? allowed : scheduled;
    }

    internal static DateTimeOffset NextCheck(DateTimeOffset now, bool popupVisible,
        DateTimeOffset? publishedExpiresAt, DateTimeOffset nextAllowedRequestAt)
    {
        var next = now.AddMinutes(popupVisible ? 1 : 5);
        if (publishedExpiresAt is { } expiresAt)
        {
            var beforeExpiry = expiresAt.AddSeconds(-15);
            if (beforeExpiry < next) next = beforeExpiry;
        }
        var earliest = now.AddMinutes(1);
        if (nextAllowedRequestAt > earliest) earliest = nextAllowedRequestAt;
        return next < earliest ? earliest : next;
    }
}
