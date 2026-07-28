using System.Globalization;
using System.Text.Json;

namespace CodexUsageTray;

public static class RateLimitNormalizer
{
    public static IReadOnlyList<RateLimitBucket> Normalize(JsonElement response)
    {
        var buckets = new List<RateLimitBucket>();
        if (response.TryGetProperty("rateLimitsByLimitId", out var byLimitId) &&
            byLimitId.ValueKind == JsonValueKind.Object &&
            byLimitId.EnumerateObject().Any())
        {
            foreach (var entry in byLimitId.EnumerateObject())
            {
                AddSnapshot(buckets, entry.Name, entry.Value);
            }
        }
        else if (response.TryGetProperty("rateLimits", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
        {
            AddSnapshot(buckets, "default", fallback);
        }

        return buckets;
    }

    public static long? ReadResetCredits(JsonElement response)
        => NormalizeResetCredits(response).AvailableCount;

    public static ResetCreditSummary NormalizeResetCredits(JsonElement response)
    {
        if (response.TryGetProperty("rateLimitResetCredits", out var credits) &&
            credits.ValueKind == JsonValueKind.Object &&
            credits.TryGetProperty("availableCount", out var countElement) &&
            countElement.ValueKind == JsonValueKind.Number &&
            countElement.TryGetInt64(out var count))
        {
            List<RateLimitResetCredit>? details = null;
            if (credits.TryGetProperty("credits", out var detailRows) && detailRows.ValueKind == JsonValueKind.Array)
            {
                details = [];
                foreach (var item in detailRows.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = ReadString(item, "id");
                    var resetType = ReadString(item, "resetType");
                    var status = ReadString(item, "status");
                    var grantedAt = ReadNullableInt64(item, "grantedAt");
                    if (string.IsNullOrWhiteSpace(id) ||
                        string.IsNullOrWhiteSpace(resetType) ||
                        string.IsNullOrWhiteSpace(status) ||
                        grantedAt is null)
                    {
                        continue;
                    }

                    var expiresAt = ReadNullableInt64(item, "expiresAt");
                    details.Add(new RateLimitResetCredit(
                        id,
                        resetType,
                        status,
                        grantedAt.Value,
                        ToLocalTimestamp(grantedAt),
                        expiresAt,
                        ToLocalTimestamp(expiresAt),
                        ReadString(item, "title"),
                        ReadString(item, "description")));
                }
            }

            return new ResetCreditSummary(Math.Max(0L, count), details);
        }

        return new ResetCreditSummary(null, null);
    }

    public static TokenActivity NormalizeUsage(JsonElement? response, DateOnly localToday)
    {
        if (response is null || response.Value.ValueKind != JsonValueKind.Object)
        {
            return new(null, null, null, null, false);
        }

        var root = response.Value;
        long? lifetime = null;
        long? peak = null;
        var hasRecognizedShape = false;
        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            hasRecognizedShape = true;
            lifetime = ReadNullableInt64(summary, "lifetimeTokens");
            peak = ReadNullableInt64(summary, "peakDailyTokens");
        }

        (string Date, long Tokens)? latest = null;
        (string Date, long Tokens)? today = null;
        if (root.TryGetProperty("dailyUsageBuckets", out var daily) && daily.ValueKind == JsonValueKind.Array)
        {
            hasRecognizedShape = true;
            foreach (var item in daily.EnumerateArray())
            {
                if (!item.TryGetProperty("startDate", out var dateElement) || dateElement.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("tokens", out var tokensElement) || !tokensElement.TryGetInt64(out var tokens))
                {
                    continue;
                }

                var date = dateElement.GetString();
                if (string.IsNullOrWhiteSpace(date))
                {
                    continue;
                }

                if (latest is null || string.CompareOrdinal(date, latest.Value.Date) > 0)
                {
                    latest = (date, tokens);
                }

                if (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed == localToday)
                {
                    today = (date, tokens);
                }
            }
        }

        if (!hasRecognizedShape)
        {
            return new(null, null, null, null, false);
        }

        var selected = today ?? latest;
        return new(selected?.Tokens, selected?.Date, lifetime, peak, true);
    }

    public static string FormatWindow(long? minutes)
    {
        if (minutes is null || minutes <= 0)
        {
            return "Rate limit";
        }

        if (minutes == 300)
        {
            return "5h";
        }

        if (minutes == 10_080)
        {
            return "Weekly";
        }

        if (minutes % 10_080 == 0)
        {
            var weeks = minutes / 10_080;
            return $"{weeks} week{(weeks == 1 ? string.Empty : "s")}";
        }

        if (minutes % 1_440 == 0)
        {
            var days = minutes / 1_440;
            return $"{days} day{(days == 1 ? string.Empty : "s")}";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes} min";
    }

    private static void AddSnapshot(List<RateLimitBucket> buckets, string sourceKey, JsonElement snapshot)
    {
        var limitId = ReadString(snapshot, "limitId") ?? sourceKey;
        var limitName = ReadString(snapshot, "limitName");
        var reachedType = ReadString(snapshot, "rateLimitReachedType");
        var windows = new List<(string Slot, JsonElement Window)>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (snapshot.TryGetProperty(slot, out var window) && window.ValueKind == JsonValueKind.Object)
            {
                windows.Add((slot, window));
            }
        }

        foreach (var (slot, window) in windows)
        {
            if (!TryGetInt32(window, "usedPercent", out var usedRaw))
            {
                continue;
            }

            var used = Math.Clamp(usedRaw, 0, 100);
            var duration = ReadNullableInt64(window, "windowDurationMins");
            var resetsAt = ReadNullableInt64(window, "resetsAt");
            var localReset = ToLocalTimestamp(resetsAt);

            var durationName = FormatWindow(duration);
            var name = string.IsNullOrWhiteSpace(limitName)
                ? durationName
                : windows.Count > 1 ? $"{limitName} · {durationName}" : limitName;
            buckets.Add(new(
                $"{sourceKey}:{slot}",
                name,
                limitId,
                slot,
                used,
                Math.Clamp(100 - usedRaw, 0, 100),
                duration,
                resetsAt,
                localReset,
                reachedType));
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadNullableInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static DateTimeOffset? ToLocalTimestamp(long? unixSeconds)
    {
        if (unixSeconds is not >= 0)
        {
            return null;
        }

        try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }
}
