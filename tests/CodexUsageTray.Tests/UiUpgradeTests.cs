using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class UiUpgradeTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Cadra light and dark render native quota cards", () => OnSta(CadraStyles)),
        ("all popup styles fit four and eight server buckets without scrolling or clipped forecast", () => OnSta(MultipleBucketsFit)),
        ("forecast remains a separate attributed community prediction in every style", () => OnSta(ForecastIsSeparate)),
        ("forecast shows stale unavailable and disabled states honestly", () => OnSta(ForecastStates)),
        ("forecast distinguishes an operator observation from account confirmation", () => OnSta(ForecastSignals)),
        ("dense quota pages keep every server bucket and forecast reachable at 96 and 192 DPI", () => OnSta(DenseQuotaPages)),
        ("forecast freshness updates at publication expiry without an account refresh", () => OnSta(ForecastFreshness)),
        ("Cadra preserves Frontier ordering and quota and status freshness after upstream merge", () => OnSta(CadraFrontierOrdering)),
        ("Cadra and dense cards match the reached flag on the selected tray bucket", () => OnSta(CadraReachedFlag))
    ];

    private static void CadraStyles()
    {
        foreach (var (name, background) in new[] { ("CadraLight", Color.FromArgb(239, 239, 236)), ("CadraDark", Color.FromArgb(11, 11, 11)) })
        {
            Require(Enum.TryParse<PopupStyle>(name, out var style), $"{name} is unavailable.");
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateSnapshot(Snapshot(2));
            popup.Show();
            Application.DoEvents();
            Require(popup.BackColor == background, $"{name} did not apply its background.");
            Require(Descendants(popup).OfType<UsageProgressBar>().Select(bar => bar.Value).Order().SequenceEqual(new[] { 43, 83 }), $"{name} lost actual quota values.");
            using var bitmap = new Bitmap(popup.Width, popup.Height);
            popup.DrawToBitmap(bitmap, popup.ClientRectangle);
            Require(bitmap.GetPixel(popup.Width / 2, 100 * popup.DeviceDpi / 96).ToArgb() != background.ToArgb(), $"{name} rendered a blank surface.");
            AssertFits(popup);
        }
    }

    private static void MultipleBucketsFit()
    {
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            foreach (var bucketCount in new[] { 4, 8 })
            {
                popup.UpdateSnapshot(Snapshot(bucketCount));
                popup.CreateControl();
                Require(Descendants(popup).OfType<UsageProgressBar>().Count() == bucketCount, $"{style} omitted a server bucket.");
                AssertFits(popup);
            }
        }
    }

    private static void ForecastIsSeparate()
    {
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateSnapshot(Snapshot(2));
            SetForecast(popup, 62, 88, DateTimeOffset.UtcNow, true);
            var controls = Descendants(popup).ToList();
            var text = string.Join("\n", controls.Select(control => control.Text));
            Require(text.Contains("24h 62%", StringComparison.Ordinal) && text.Contains("48h 88%", StringComparison.Ordinal), $"{style} omitted forecast probabilities.");
            Require(text.Contains("Community prediction", StringComparison.Ordinal), $"{style} omitted the prediction qualifier.");
            Require(controls.OfType<LinkLabel>().Any(link => link.Text == "Data: codex-reset.com" && link.Links[0].LinkData is string url && url == "https://codex-reset.com/"), $"{style} omitted clickable provider attribution.");
            Require(controls.OfType<UsageProgressBar>().Select(bar => bar.Value).Order().SequenceEqual(new[] { 43, 83 }), $"{style} changed quota because of a forecast.");
            Require(text.Contains("High", StringComparison.Ordinal), $"{style} omitted confidence.");
            AssertFits(popup);
        }
    }

    private static void ForecastStates()
    {
        using var popup = new UsagePopupForm();
        popup.UpdateSnapshot(Snapshot(0));
        SetForecast(popup, 62, 88, DateTimeOffset.UtcNow.AddDays(-2), true);
        Require(AllText(popup).Contains("Stale", StringComparison.Ordinal), "Old forecast was presented as fresh.");
        SetForecast(popup, null, null, null, false);
        Require(AllText(popup).Contains("24h unavailable", StringComparison.Ordinal), "Missing forecast became a percentage.");
        SetForecast(popup, 62, 88, DateTimeOffset.UtcNow, true, false);
        Require(!Descendants(popup).Any(control => control.AccessibleName == "Reset forecast"), "Disabled forecast remains visible.");
        AssertFits(popup);
    }

    private static void ForecastSignals()
    {
        var local = new DateTime(2026, 10, 23, 17, 40, 0, DateTimeKind.Unspecified);
        var lastReset = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateSnapshot(Snapshot(4));
            SetForecast(popup, 62, 88, DateTimeOffset.UtcNow, true, lastReset: lastReset,
                alertKind: "reset", alertState: "confirmed", confirmationSource: "operator_observed");
            var text = AllText(popup);
            Require(text.Contains("Last reported reset: Oct 23, 2026 17:40 local", StringComparison.Ordinal), $"{style} omitted the reported reset time.");
            Require(text.Contains("Signal: operator observed a reset", StringComparison.Ordinal), $"{style} lost operator attribution.");
            Require(!text.Contains("confirmed", StringComparison.OrdinalIgnoreCase), $"{style} turned an operator observation into confirmation.");
            AssertFits(popup);
            SetForecast(popup, null, null, null, false, alertKind: new string('x', 10000));
            Require(AllText(popup).Contains("Last reported reset: unavailable", StringComparison.Ordinal), "Missing reset time was invented.");
            Require(AllText(popup).Contains("Signal: unavailable", StringComparison.Ordinal), "Unknown signal leaked raw source text.");
            AssertFits(popup);
        }
    }

    private static void DenseQuotaPages()
    {
        foreach (var style in Enum.GetValues<PopupStyle>())
            foreach (var dpi in new[] { 96, 192 })
                foreach (var count in new[] { 8, 12, 20 })
                {
                    var snapshot = Snapshot(count) with
                    {
                        Error = "A synthetic error that needs visible account and forecast controls.",
                        ResetCreditsAvailable = 1,
                        ResetCredits = [new("test-credit", "codexRateLimits", "available", 0, null, null, DateTimeOffset.Now.AddDays(1), null, null)]
                    };
                    using var popup = new UsagePopupForm(false, style);
                    popup.UpdateSnapshot(snapshot);
                    popup.Show();
                    Application.DoEvents();
                    if (dpi != popup.DeviceDpi) SendDpiChange(popup, dpi);
                    Require(popup.DeviceDpi == dpi, "Synthetic WM_DPICHANGED did not set the requested DPI.");
                    var seen = new HashSet<string>();
                    for (var page = 0; page < count; page++)
                    {
                        AssertFits(popup);
                        Require(Descendants(popup).Any(control => control.AccessibleName == "Reset forecast"), "Dense layout lost forecast.");
                        Require(Descendants(popup).Any(control => control.AccessibleName == "Usage actions"), "Dense layout lost footer.");
                        foreach (var bucket in Descendants(popup).OfType<UsageProgressBar>()) seen.Add(bucket.AccessibleName!);
                        var next = Descendants(popup).OfType<Button>().SingleOrDefault(button => button.AccessibleName == "Next quota page");
                        if (next?.Enabled != true) break;
                        next.PerformClick();
                    }
                    Require(seen.Count == count, $"{style} at {dpi} DPI exposed only {seen.Count}/{count} server buckets.");
                }
    }

    private static void ForecastFreshness()
    {
        using var popup = new UsagePopupForm(popupStyle: PopupStyle.CadraDark);
        var now = DateTimeOffset.UtcNow;
        popup.UpdateResetForecast(new()
        {
            CheckedAt = now,
            UpdatedAt = now,
            PublishedExpiresAt = now.AddSeconds(1),
            IsLive = true,
            Probability24Hours = 62,
            Probability48Hours = 88
        });
        var refresh = typeof(UsagePopupForm).GetMethod("RefreshForecastFreshness", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(refresh is not null, "Forecast freshness refresh entry point is unavailable.");
        var originalCard = Descendants(popup).Single(control => control.AccessibleName == "Reset forecast");
        refresh!.Invoke(popup, [now.AddMilliseconds(500)]);
        Require(ReferenceEquals(originalCard, Descendants(popup).Single(control => control.AccessibleName == "Reset forecast")), "Fresh forecast rebuilt without a state change.");
        refresh.Invoke(popup, [now.AddSeconds(2)]);
        Require(AllText(popup).Contains("Stale", StringComparison.Ordinal), "Expired forecast remains Live until an account redraw.");
        Require(!AllText(popup).Split('\n').Contains("Live"), "Expired forecast still has a Live badge.");
        var staleCard = Descendants(popup).Single(control => control.AccessibleName == "Reset forecast");
        refresh.Invoke(popup, [now.AddSeconds(3)]);
        Require(ReferenceEquals(staleCard, Descendants(popup).Single(control => control.AccessibleName == "Reset forecast")), "Stale forecast rebuilt without a state change.");
    }

    private static void CadraFrontierOrdering()
    {
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(0) with
        {
            Buckets =
            [
                new("spark:primary", "GPT-5.3-Codex-Spark · 5h", "spark", "primary", 46, 54, 300, null, null, null),
                new("codex:primary", "Weekly", "codex", "primary", 9, 91, 10080, null, null, null)
            ]
        };
        foreach (var style in new[] { PopupStyle.CadraLight, PopupStyle.CadraDark })
        {
            using var popup = new UsagePopupForm(false, style);
            popup.UpdateOpenAiStatus(new() { IsLive = true, CheckedAt = now, Codex = OpenAiServiceHealth.Operational, ChatGpt = OpenAiServiceHealth.Operational });
            popup.UpdateSnapshot(snapshot);
            var controls = Descendants(popup).ToList();
            var firstQuota = controls.First(control => control.AccessibleName?.Contains("% left", StringComparison.Ordinal) == true);
            Require(firstQuota.AccessibleName!.Contains("Frontier", StringComparison.Ordinal), "Cadra lost Frontier-first quota ordering.");
            Require(AllText(popup).Contains("5h · GPT-5.3-Codex-Spark", StringComparison.Ordinal), "Cadra lost the Spark window label.");
            Require(AllText(popup).Contains("Quota updated ", StringComparison.Ordinal), "Cadra lost quota freshness.");
            Require(AllText(popup).Contains("OpenAI status · checked ", StringComparison.Ordinal), "Cadra lost independently checked service freshness.");
            var quotaIndex = controls.IndexOf(firstQuota);
            var statusIndex = controls.FindIndex(control => control.AccessibleName?.StartsWith("OpenAI status, ", StringComparison.Ordinal) == true);
            Require(statusIndex > quotaIndex, "Cadra placed service health before account quotas.");
            AssertFits(popup);
        }
    }

    private static void CadraReachedFlag()
    {
        var source = Snapshot(8);
        var snapshot = source with
        {
            Buckets = source.Buckets.Select((bucket, index) => bucket with
            {
                LimitId = index == 0 ? "codex" : $"other-{index}",
                RemainingPercent = index == 0 ? 80 : 90,
                RateLimitReachedType = index == 0 ? "hard_limit" : null
            }).ToList()
        };
        foreach (var style in new[] { PopupStyle.CadraLight, PopupStyle.CadraDark, PopupStyle.Classic })
        {
            using var popup = new UsagePopupForm(false, style);
            popup.UpdateSnapshot(snapshot);
            popup.Show();
            Application.DoEvents();
            if (style == PopupStyle.Classic) SendDpiChange(popup, 192);
            var card = Descendants(popup).Single(control => control.AccessibleName?.StartsWith("Tray indicator, ", StringComparison.Ordinal) == true);
            Require(card.AccessibleName!.Contains("0% left", StringComparison.Ordinal), $"{style} accessibility disagrees with the tray reached flag.");
            Require(Descendants(card).OfType<UsageProgressBar>().Single().Value == 0, $"{style} progress disagrees with the tray reached flag.");
            Require(AllText(card).Contains("0% left", StringComparison.Ordinal), $"{style} percentage disagrees with the tray reached flag.");
            Require(snapshot.Buckets[0].RemainingPercent == 80, "Popup mutated the server bucket while displaying its effective quota.");
            AssertFits(popup);
        }
    }

    private static void SendDpiChange(Form popup, int dpi)
    {
        var rectangle = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
        try
        {
            Marshal.StructureToPtr(new NativeRectangle { Right = popup.Width, Bottom = popup.Height }, rectangle, false);
            SendMessage(popup.Handle, 0x02E0, (nint)(dpi | dpi << 16), rectangle);
            Application.DoEvents();
        }
        finally { Marshal.FreeHGlobal(rectangle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    private static void SetForecast(UsagePopupForm popup, int? first, int? second, DateTimeOffset? updated, bool live, bool enabled = true,
        DateTimeOffset? lastReset = null, string? alertKind = null, string? alertState = null, string? confirmationSource = null)
    {
        var modelType = typeof(UsagePopupForm).Assembly.GetType("CodexUsageTray.ResetForecastSnapshot");
        Require(modelType is not null, "Reset forecast model is unavailable.");
        var model = Activator.CreateInstance(modelType!)!;
        foreach (var (name, value) in new (string, object?)[]
        {
            ("CheckedAt", DateTimeOffset.UtcNow), ("UpdatedAt", updated), ("Probability24Hours", first),
            ("Probability48Hours", second), ("Confidence", "High"), ("IsLive", live),
            ("LastResetAt", lastReset), ("AlertKind", alertKind), ("AlertState", alertState), ("ConfirmationSource", confirmationSource)
        })
            modelType!.GetProperty(name)!.SetValue(model, value);
        var update = typeof(UsagePopupForm).GetMethod("UpdateResetForecast", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(update is not null, "Reset forecast update entry point is unavailable.");
        update!.Invoke(popup, [model, enabled]);
    }

    private static UsageSnapshot Snapshot(int bucketCount)
    {
        var now = DateTimeOffset.UtcNow;
        return new UsageSnapshot
        {
            IsLive = true,
            Status = ConnectionStatus.Connected,
            PlanType = "plus",
            Buckets = Enumerable.Range(0, bucketCount).Select(index => new RateLimitBucket(
                $"bucket-{index}", index == 0 ? "Weekly" : $"Quota {index} unusually long server-returned model name",
                "codex", "primary", index == 0 ? 57 : 17, index == 0 ? 43 : 83, 10080,
                now.AddDays(2).ToUnixTimeSeconds(), now.AddDays(2), null)).ToList(),
            TokenActivity = new(123_456, "2026-10-24", 1_234_567, 456_789, true),
            ApiEquivalent = new()
            {
                IsAvailable = true,
                TotalTokens = 1_234_567,
                EstimatedUsd = 12.34m,
                ProjectCount = 3,
                Projects = [new("A long project name", 100_000, 1.25m, 0), new("Second", 200_000, 2.5m, 0), new("Third", 300_000, 3.75m, 0)]
            }
        };
    }

    private static void AssertFits(UsagePopupForm popup)
    {
        popup.PerformLayout();
        var content = popup.Controls.OfType<FlowLayoutPanel>().Single();
        content.PerformLayout();
        Require(!content.AutoScroll, "Popup enabled scrolling.");
        Require(popup.ClientSize.Height <= 1000 * popup.DeviceDpi / 96, "Popup exceeds a 1080p desktop budget.");
        foreach (var control in Descendants(content))
        {
            var viewport = control.Parent!.ClientSize;
            Require(control.Bottom <= viewport.Height && control.Right <= viewport.Width,
                $"{control.AccessibleName ?? control.Text} is clipped ({control.Bounds}, viewport {viewport}).");
        }
    }

    private static string AllText(Control parent) => string.Join("\n", Descendants(parent).Select(control => control.Text));
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
