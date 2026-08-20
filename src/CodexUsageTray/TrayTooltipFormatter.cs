using System.Globalization;

namespace CodexUsageTray;

public static class TrayTooltipFormatter
{
    private const int MaximumLength = 63;

    public static string Format(UsageSnapshot snapshot)
    {
        var bucket = snapshot.TrayBucket;
        if (bucket is null)
        {
            return snapshot.Buckets.Count > 0 && snapshot.Status is ConnectionStatus.Connected or ConnectionStatus.RateLimitReached
                ? "Codex Frontier · unavailable"
                : $"Codex Frontier · {StatusText(snapshot.Status)}";
        }

        var suffix = snapshot.Status switch
        {
            ConnectionStatus.DataStale => " · Stale",
            ConnectionStatus.CliNotFound or ConnectionStatus.AppServerUnavailable => " · Error",
            _ when bucket.ResetsAtLocal is not null => $" · Reset {bucket.ResetsAtLocal.Value.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)}",
            _ => string.Empty
        };
        var percentage = $" {snapshot.TrayRemainingPercent}% left";
        const string prefix = "Codex Frontier · ";
        var availableNameLength = MaximumLength - prefix.Length - percentage.Length - suffix.Length;
        if (availableNameLength < 1)
        {
            suffix = string.Empty;
            availableNameLength = MaximumLength - prefix.Length - percentage.Length;
        }

        var name = bucket.Name.Length <= availableNameLength
            ? bucket.Name
            : bucket.Name[..availableNameLength];
        return prefix + name + percentage + suffix;
    }

    private static string StatusText(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Loading => "Loading",
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.DataStale => "Data stale",
        ConnectionStatus.NotSignedIn => "Not signed in",
        ConnectionStatus.CliNotFound => "Codex CLI not found",
        ConnectionStatus.AppServerUnavailable => "App Server unavailable",
        ConnectionStatus.RateLimitReached => "Rate limit reached",
        _ => status.ToString()
    };
}
