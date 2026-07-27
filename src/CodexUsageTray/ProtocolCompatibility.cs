using System.Text.Json;

namespace CodexUsageTray;

public sealed record ProtocolCompatibilityReport(
    string RateLimitShape,
    string UsageShape,
    List<string> Warnings)
{
    public string Status => $"rateLimits={RateLimitShape}; usage={UsageShape}";
}

public static class ProtocolCompatibility
{
    public static ProtocolCompatibilityReport Inspect(JsonElement limits, JsonElement? usage)
    {
        var warnings = new List<string>();
        var rateLimitShape = InspectRateLimits(limits, warnings);
        var usageShape = InspectUsage(usage, warnings);
        return new(rateLimitShape, usageShape, warnings);
    }

    private static string InspectRateLimits(JsonElement limits, List<string> warnings)
    {
        if (limits.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("Rate-limit response is not an object.");
            return "unknown";
        }

        if (limits.TryGetProperty("rateLimitsByLimitId", out var map) &&
            map.ValueKind == JsonValueKind.Object &&
            map.EnumerateObject().Any())
        {
            if (!map.EnumerateObject().Any(entry => HasKnownWindow(entry.Value)))
            {
                warnings.Add("rateLimitsByLimitId has no recognized primary or secondary usage window.");
            }
            return "rateLimitsByLimitId";
        }

        if (limits.TryGetProperty("rateLimits", out var fallback) &&
            fallback.ValueKind == JsonValueKind.Object)
        {
            if (!HasKnownWindow(fallback))
            {
                warnings.Add("rateLimits has no recognized primary or secondary usage window.");
            }
            return "rateLimits";
        }

        warnings.Add("No supported rate-limit container was returned.");
        return "unknown";
    }

    private static string InspectUsage(JsonElement? usage, List<string> warnings)
    {
        if (usage is null)
        {
            return "unavailable";
        }
        if (usage.Value.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("Usage response is not an object.");
            return "unknown";
        }

        var hasSummary = usage.Value.TryGetProperty("summary", out var summary) &&
                         summary.ValueKind == JsonValueKind.Object;
        var hasDaily = usage.Value.TryGetProperty("dailyUsageBuckets", out var daily) &&
                       daily.ValueKind == JsonValueKind.Array;
        if (!hasSummary && !hasDaily)
        {
            warnings.Add("Usage response has no recognized summary or dailyUsageBuckets fields.");
            return "unknown";
        }

        return hasSummary && hasDaily ? "summary+daily" : hasSummary ? "summary" : "daily";
    }

    private static bool HasKnownWindow(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return new[] { "primary", "secondary" }.Any(slot =>
            snapshot.TryGetProperty(slot, out var window) &&
            window.ValueKind == JsonValueKind.Object &&
            window.TryGetProperty("usedPercent", out var used) &&
            used.ValueKind == JsonValueKind.Number);
    }
}
