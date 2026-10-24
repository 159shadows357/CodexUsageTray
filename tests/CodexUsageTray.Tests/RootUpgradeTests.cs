using System.Text.Json;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class RootUpgradeTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Plus reads exact server windows without reset-credit entitlement", PlusWindows),
        ("malformed optional quota rows preserve valid Plus windows", MalformedRows),
        ("malformed optional token rows remain unavailable", MalformedUsageRows),
        ("date version preserves daily patch when selecting update asset", CalendarVersion),
        ("date version compares patches numerically", CalendarVersionComparison),
        ("malformed JSON-RPC errors remain typed optional failures", MalformedRpcError),
        ("extreme usage percentages clamp without arithmetic overflow", ExtremePercentages),
        ("legacy settings and forecast choice survive a theme change", SettingsMigration),
        ("reset-credit diagnostics distinguish missing count-only and detailed data", CreditDiagnostics),
        ("tray source supports lowest quota while retaining Frontier selection", TrayQuotaChoice)
    ];

    private static Task PlusWindows()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimitsByLimitId":{"codex":{"limitId":"codex","planType":"plus",
            "primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1792828800},
            "secondary":{"usedPercent":17,"windowDurationMins":10080,"resetsAt":1793382077}}},
            "rateLimitResetCredits":null}
            """);
        var buckets = RateLimitNormalizer.Normalize(document.RootElement);
        Require(buckets.Count == 2 && buckets[0].Name == "5h" && buckets[1].Name == "Weekly", "Plus windows changed.");
        Require(buckets[0].RemainingPercent == 58 && buckets[1].RemainingPercent == 83, "Plus quota mismatch.");
        Require(RateLimitNormalizer.ReadResetCredits(document.RootElement) is null, "Missing credit entitlement invented.");
        return Task.CompletedTask;
    }

    private static Task MalformedRows()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimitsByLimitId":{"null":null,"scalar":4,"array":[],"codex":{
            "primary":{"usedPercent":20,"windowDurationMins":300},"secondary":null}}}
            """);
        Require(RateLimitNormalizer.Normalize(document.RootElement).Count == 1, "Malformed entry hid valid quota.");
        using var nullRoot = JsonDocument.Parse("null");
        Require(RateLimitNormalizer.Normalize(nullRoot.RootElement).Count == 0, "Null response invented quota.");
        Require(RateLimitNormalizer.NormalizeResetCredits(nullRoot.RootElement).AvailableCount is null, "Null credits invented.");
        return Task.CompletedTask;
    }

    private static Task MalformedUsageRows()
    {
        using var document = JsonDocument.Parse("""
            {"summary":{"lifetimeTokens":"unknown"},"dailyUsageBuckets":[null,4,[],
            {"startDate":"2026-10-24","tokens":"bad"},{"startDate":"2026-10-24","tokens":100}]}
            """);
        var activity = RateLimitNormalizer.NormalizeUsage(document.RootElement, new DateOnly(2026, 10, 24));
        Require(activity.TodayOrLatestTokens == 100 && activity.LifetimeTokens is null, "Optional usage was not isolated.");
        return Task.CompletedTask;
    }

    private static Task CalendarVersion()
    {
        using var document = JsonDocument.Parse(Release("2026.10.24.1"));
        var result = GitHubReleaseUpdater.ParseLatestRelease(document.RootElement, new Version(0, 6, 1));
        Require(result.Release?.AssetName == "CodexUsageTray-2026.10.24.1-win-x64.zip", "Daily patch was dropped.");
        Require(result.Release!.Version == new Version(2026, 10, 24, 1), "Daily patch was not preserved.");
        return Task.CompletedTask;
    }

    private static Task CalendarVersionComparison()
    {
        using var newer = JsonDocument.Parse(Release("2026.10.24.10"));
        Require(GitHubReleaseUpdater.ParseLatestRelease(newer.RootElement, new Version(2026, 10, 24, 9)).Status == UpdateCheckStatus.UpdateAvailable,
            "Patch comparison must be numeric.");
        using var old = JsonDocument.Parse(Release("0.6.1"));
        Require(GitHubReleaseUpdater.ParseLatestRelease(old.RootElement, new Version(2026, 10, 24, 1)).Status == UpdateCheckStatus.UpToDate,
            "Old semantic release should not downgrade date version.");
        return Task.CompletedTask;
    }

    private static async Task MalformedRpcError()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexUsageTray.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var input = new StreamReader(new MemoryStream());
            using var outputStream = new MemoryStream();
            using var output = new StreamWriter(outputStream) { AutoFlush = true };
            await using var client = new JsonRpcClient(input, output, new AppLogger(new AppPaths(root)), startReader: false);
            var request = client.RequestAsync("account/usage/read", new { }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Require(client.ProcessLine("{\"id\":1,\"error\":{\"code\":\"bad\",\"message\":{}}}"), "Response ignored.");
            try
            {
                await request.ConfigureAwait(false);
                throw new InvalidOperationException("Malformed error unexpectedly succeeded.");
            }
            catch (JsonRpcException error)
            {
                Require(error.Code == -1 && error.Message == "Unknown JSON-RPC error", "Error shape was not normalized.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Task ExtremePercentages()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":-2147483648},"secondary":{"usedPercent":2147483647}}}
            """);
        var buckets = RateLimitNormalizer.Normalize(document.RootElement);
        Require(buckets[0].RemainingPercent == 100 && buckets[1].RemainingPercent == 0, "Clamping overflowed.");
        return Task.CompletedTask;
    }

    private static Task SettingsMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexUsageTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{"popupStyle":"Paper","updateMode":"Automatic","resetCreditMode":"UseBeforeExpiry","resetCreditLeadTimeMinutes":60}""");
            var store = new JsonStore();
            var legacy = store.ReadSettings(path);
            Require(legacy.ResetForecastEnabled && legacy.PopupStyle == PopupStyle.Paper, "Legacy defaults changed.");
            store.Write(path, legacy with { PopupStyle = PopupStyle.CadraDark, ResetForecastEnabled = false });
            var current = store.ReadSettings(path);
            Require(!current.ResetForecastEnabled && current.PopupStyle == PopupStyle.CadraDark &&
                current.UpdateMode == UpdateMode.Automatic && current.ResetCreditMode == ResetCreditMode.UseBeforeExpiry &&
                current.ResetCreditLeadTimeMinutes == 60, "Settings were lost during theme selection.");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static Task CreditDiagnostics()
    {
        foreach (var (json, expected) in new[]
        {
            ("{}", "unavailable"),
            ("{\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":null}}", "count-only"),
            ("{\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":[]}}", "details")
        })
        {
            using var document = JsonDocument.Parse(json);
            Require(ProtocolCompatibility.Inspect(document.RootElement, null).ResetCreditShape == expected, "Credit capability mismatch.");
        }
        return Task.CompletedTask;
    }

    private static Task TrayQuotaChoice()
    {
        var setting = typeof(AppSettings).GetProperty("TrayQuotaSource");
        var selection = typeof(UsageSnapshot).GetProperty("TrayQuotaSource");
        Require(setting is not null && selection is not null, "The tray quota source choice is missing.");
        Require(setting!.GetValue(new AppSettings())?.ToString() == "Frontier", "Default must follow the user's Frontier choice.");
        var snapshot = new UsageSnapshot
        {
            Status = ConnectionStatus.Connected,
            Buckets =
            [
                new("codex:primary", "Weekly", "codex", "primary", 20, 80, 10080, null, null, null),
                new("spark:primary", "Spark", "spark", "primary", 90, 10, 300, null, null, null)
            ]
        };
        Require(snapshot.TrayBucket?.LimitId == "codex", "The upstream Frontier selector changed.");
        selection!.SetValue(snapshot, Enum.Parse(selection.PropertyType, "LowestRemaining"));
        Require(snapshot.TrayBucket?.LimitId == "spark" && snapshot.TrayRemainingPercent == 10, "The lowest quota was not selected.");
        Require(!TrayTooltipFormatter.Format(snapshot).Contains("Frontier", StringComparison.Ordinal), "A Spark tray value was mislabeled Frontier.");
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateSnapshot(snapshot);
            var labels = AllControls(popup).Select(control => control.Text);
            Require(labels.Any(text => text.StartsWith("Tray · ", StringComparison.Ordinal) && text.Contains("Spark", StringComparison.Ordinal)),
                $"{style} did not identify the actual selected tray source.");
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<System.Windows.Forms.Control> AllControls(System.Windows.Forms.Control parent)
    {
        foreach (System.Windows.Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in AllControls(child)) yield return nested;
        }
    }

    private static string Release(string version) => $$"""
        {"tag_name":"v{{version}}","html_url":"https://github.com/159shadows357/CodexUsageTray/releases/tag/v{{version}}",
        "assets":[{"name":"CodexUsageTray-{{version}}-win-x64.zip",
        "url":"https://api.github.com/repos/159shadows357/CodexUsageTray/releases/assets/1",
        "size":100,"digest":"sha256:{{new string('a', 64)}}"}]}
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
