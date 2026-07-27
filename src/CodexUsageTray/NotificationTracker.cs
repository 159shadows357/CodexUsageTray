namespace CodexUsageTray;

using System.Text.Json;

public sealed record ThresholdAlert(RateLimitBucket Bucket, int Threshold);

public sealed class NotificationTracker
{
    private static readonly int[] Thresholds = [20, 5, 0];
    private readonly Dictionary<string, WindowState> _states = new(StringComparer.Ordinal);
    private readonly string? _statePath;

    public NotificationTracker(string? statePath = null)
    {
        _statePath = statePath;
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, StoredWindowState>>(File.ReadAllText(statePath));
            if (stored is null) return;
            foreach (var (key, state) in stored)
            {
                _states[key] = new(state.ResetKey, state.PreviousRemaining, state.Notified.ToHashSet());
            }
        }
        catch (Exception error) when (error is JsonException or IOException) { }
    }

    public IReadOnlyList<ThresholdAlert> Evaluate(IEnumerable<RateLimitBucket> buckets, bool isLive)
    {
        if (!isLive)
        {
            return [];
        }

        var alerts = new List<ThresholdAlert>();
        foreach (var bucket in buckets)
        {
            var resetKey = bucket.ResetsAtUnix?.ToString() ?? "unknown";
            if (!_states.TryGetValue(bucket.Key, out var state) || state.ResetKey != resetKey)
            {
                _states[bucket.Key] = new(resetKey, bucket.RemainingPercent, []);
                continue;
            }

            if (bucket.RemainingPercent < state.PreviousRemaining)
            {
                foreach (var threshold in Thresholds)
                {
                    if (state.PreviousRemaining > threshold && bucket.RemainingPercent <= threshold && state.Notified.Add(threshold))
                    {
                        alerts.Add(new(bucket, threshold));
                    }
                }
            }

            state.PreviousRemaining = bucket.RemainingPercent;
        }

        Save();
        return alerts;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_statePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var stored = _states.ToDictionary(
                pair => pair.Key,
                pair => new StoredWindowState(pair.Value.ResetKey, pair.Value.PreviousRemaining, pair.Value.Notified.Order().ToArray()),
                StringComparer.Ordinal);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored));
            File.Move(temp, _statePath, true);
        }
        catch (IOException) { }
    }

    private sealed class WindowState(string resetKey, int previousRemaining, HashSet<int> notified)
    {
        public string ResetKey { get; } = resetKey;
        public int PreviousRemaining { get; set; } = previousRemaining;
        public HashSet<int> Notified { get; } = notified;
    }

    private sealed record StoredWindowState(string ResetKey, int PreviousRemaining, int[] Notified);
}
