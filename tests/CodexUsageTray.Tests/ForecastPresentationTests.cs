using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class ForecastPresentationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("promise watch score and model odds remain distinct in every popup style", () => OnSta(PromisePresentation)),
        ("stale and superseded promises never remain current popup headlines", () => OnSta(InactivePromises)),
        ("model calculation time is distinct from API freshness", () => OnSta(CalculationAge)),
        ("forecast refresh follows visibility publication expiry and provider backoff", RefreshSchedule)
    ];

    private static void PromisePresentation()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(false, style);
            popup.UpdateSnapshot(new() { IsLive = true, Status = ConnectionStatus.Connected });
            popup.UpdateResetForecast(Promise(now));
            var text = Text(popup);
            Require(text.Contains("Reset promised", StringComparison.Ordinal), $"{style}: current promise is missing.");
            Require(text.Contains("Watch 83%", StringComparison.Ordinal), $"{style}: provider Watch score is missing or mislabelled.");
            Require(text.Contains("Model: 24h 44%", StringComparison.Ordinal) && text.Contains("48h 69%", StringComparison.Ordinal), "Model odds were overwritten by the Watch score.");
            Require(!text.Contains("89%", StringComparison.Ordinal), "Website odds were invented from another provider's Watch score.");
            Require(Descendants(popup).OfType<LinkLabel>().Any(link => link.Links.Cast<LinkLabel.Link>().Any(item =>
                item.LinkData as string == "https://x.com/thsottiaux/status/1234567890")), "Promise has no safe source-post link.");
            AssertFits(popup);
            popup.UpdateResetForecast(Promise(now) with { SignalPercent = null });
            Require(Text(popup).Contains("score unavailable", StringComparison.Ordinal), "A conflicting or absent Watch score was invented.");
            AssertFits(popup);
        }
    }

    private static void InactivePromises()
    {
        var now = DateTimeOffset.UtcNow;
        var promise = Promise(now);
        using var popup = new UsagePopupForm(false, PopupStyle.CadraDark);
        popup.UpdateResetForecast(promise);
        Require(Text(popup).Contains("Reset promised", StringComparison.Ordinal), "The fixture never showed a current promise.");
        foreach (var inactive in new[]
        {
            promise with { IsLive = false },
            promise with { PublishedExpiresAt = now.AddSeconds(-1) },
            promise with { LastResetAt = now.AddMinutes(-1) },
            promise with { ObservedAt = now.AddMinutes(-1), AlertKind = "reset", AlertState = "confirmed", ConfirmationSource = "operator_observed" }
        })
        {
            popup.UpdateResetForecast(inactive);
            Require(!Text(popup).Contains("Reset promised", StringComparison.Ordinal), "An inactive or superseded promise was presented as current.");
        }
        popup.UpdateResetForecast(promise with { PublishedExpiresAt = now.AddSeconds(1) });
        popup.RefreshForecastFreshness(now.AddSeconds(2));
        Require(Text(popup).Contains("Stale", StringComparison.Ordinal) && !Text(popup).Contains("Reset promised", StringComparison.Ordinal),
            "Promise remained current after its publication expired without a quota update.");
    }

    private static void CalculationAge()
    {
        using var popup = new UsagePopupForm(false, PopupStyle.CadraLight);
        popup.UpdateResetForecast(Promise(DateTimeOffset.UtcNow) with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-4) });
        Require(Text(popup).Contains("Calculated 4m ago", StringComparison.Ordinal), "Calculation age is missing or described as new evidence.");
        Require(Text(popup).Contains("API fresh", StringComparison.Ordinal), "Freshness does not identify the API response.");
    }

    private static Task RefreshSchedule()
    {
        var now = DateTimeOffset.Parse("2026-10-26T07:00:00Z");
        var type = typeof(UsagePopupForm).Assembly.GetType("CodexUsageTray.ForecastRefreshSchedule");
        var method = type?.GetMethod("NextCheck", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Require(method is not null, "Forecast publication-aware refresh schedule is missing.");
        DateTimeOffset Next(bool visible, DateTimeOffset? expiry, DateTimeOffset allowed) =>
            (DateTimeOffset)method!.Invoke(null, [now, visible, expiry, allowed])!;
        Require(Next(true, now.AddMinutes(3), now.AddMinutes(1)) == now.AddMinutes(1), "Visible forecast did not refresh each allowed minute.");
        Require(Next(false, now.AddMinutes(3), now.AddMinutes(1)) == now.AddMinutes(3).AddSeconds(-15), "Hidden forecast polls after publication expiry.");
        Require(Next(false, null, now) == now.AddMinutes(5), "Hidden forecast lost its normal bounded interval.");
        Require(Next(true, now.AddMinutes(3), now.AddMinutes(10)) == now.AddMinutes(10), "Provider backoff was bypassed.");
        Require(Next(false, now.AddSeconds(-1), now.AddMinutes(1)) == now.AddMinutes(1), "Expired publication bypassed the request throttle.");
        var open = type!.GetMethod("OnPopupOpened", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Require(open is not null, "Opening the popup does not accelerate a hidden forecast deadline.");
        DateTimeOffset Open(DateTimeOffset scheduled, DateTimeOffset allowed) => (DateTimeOffset)open!.Invoke(null, [now, scheduled, allowed])!;
        Require(Open(now.AddMinutes(3), now.AddSeconds(50)) == now.AddSeconds(50), "Opening kept the hidden refresh deadline.");
        Require(Open(now.AddSeconds(10), now.AddSeconds(50)) == now.AddSeconds(10), "Opening postponed an earlier pending refresh.");
        Require(Open(now.AddMinutes(15), now.AddMinutes(10)) == now.AddMinutes(10), "Opening bypassed provider backoff.");
        return Task.CompletedTask;
    }

    internal static ResetForecastSnapshot Promise(DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            mode = "announced",
            updated_at = now.ToString("O"),
            last_reset_at = now.AddDays(-1).ToString("O"),
            confidence = "low",
            probabilities = new { rounded_24h = 44, rounded_48h = 69, model_24h = 44, model_48h = 69, signal_percent = 83 },
            official_signal = new
            {
                tweet_id = "1234567890",
                signal_type = "promise",
                at = now.AddMinutes(-5).ToString("O"),
                url = "https://x.com/thsottiaux/status/1234567890",
                score = new { value = 83 }
            }
        }));
        return ResetForecastClient.ParseForecast(document.RootElement, now);
    }

    private static void AssertFits(UsagePopupForm popup)
    {
        popup.PerformLayout();
        Require(!popup.Controls.OfType<FlowLayoutPanel>().Single().AutoScroll, "Forecast added popup scrollbars.");
        var card = Descendants(popup).Single(control => control.AccessibleName == "Reset forecast");
        var title = card.Controls.Cast<Control>().Single(control => control.Text == "Reset forecast");
        var freshness = card.Controls.Cast<Control>().Single(control => control.Text == "API fresh");
        Require(!title.Bounds.IntersectsWith(freshness.Bounds), "The forecast title covers its API freshness badge.");
        foreach (var control in Descendants(popup))
        {
            Require(control.Right <= control.Parent!.ClientSize.Width && control.Bottom <= control.Parent.ClientSize.Height,
                $"Forecast content is clipped: {control.Text}.");
            if (control is Label && control.Text.Length > 0)
                Require(TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.NoPadding).Width <= control.Width,
                    $"Forecast text is clipped: {control.Text}.");
        }
    }

    private static string Text(Control parent) => string.Join('\n', Descendants(parent).Select(control => control.Text));
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
