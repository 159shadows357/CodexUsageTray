using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class VisibleRefreshTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("visible popup refresh retains native content and keyboard focus", () => OnSta(VisibleRefresh)),
        ("hidden and closed popups cannot reappear during background refresh", () => OnSta(HiddenRefresh)),
        ("outside activation still closes a refreshed popup", () => OnSta(OutsideActivation))
    ];

    private static void VisibleRefresh()
    {
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = Create(style);
            popup.ShowNearTray();
            MoveAwayFromCursor(popup);
            FocusAction(popup);
            Pump(650);
            foreach (var refresh in new Action[]
            {
                () => popup.UpdateOpenAiStatus(new() { IsLive = true, Codex = OpenAiServiceHealth.Operational, ChatGpt = OpenAiServiceHealth.Operational }),
                () => popup.UpdateSnapshot(Snapshot(21)),
                () => popup.UpdateResetForecast(Forecast()),
                () => popup.RefreshForecastFreshness(DateTimeOffset.UtcNow.AddMinutes(10))
            })
            {
                MoveAwayFromCursor(popup);
                FocusAction(popup);
                refresh();
                Pump(25);
                AssertVisible(popup, style.ToString());
                Require(popup.ContainsFocus, $"{style}: refresh lost keyboard focus.");
            }
        }
    }

    private static void HiddenRefresh()
    {
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = Create(style);
            popup.ShowNearTray();
            Pump(25);
            var close = Descendants(popup).OfType<Button>().Single(button => button.Text == "Close");
            close.PerformClick();
            Require(!popup.Visible && !IsWindowVisible(popup.Handle), $"{style}: Close did not hide the native popup.");
            popup.UpdateSnapshot(Snapshot(40));
            popup.UpdateResetForecast(Forecast());
            popup.RefreshForecastFreshness(DateTimeOffset.UtcNow.AddHours(1));
            Pump(25);
            Require(!popup.Visible && !IsWindowVisible(popup.Handle), $"{style}: background refresh resurrected a closed popup.");
            popup.ShowNearTray();
            AssertVisible(popup, style.ToString());
        }
    }

    private static void OutsideActivation()
    {
        using var popup = Create(PopupStyle.CadraDark);
        popup.ShowNearTray();
        MoveAwayFromCursor(popup);
        FocusAction(popup);
        Pump(650);
        popup.UpdateSnapshot(Snapshot(30));
        AssertVisible(popup, "outside-action precondition");
        MoveAwayFromCursor(popup);
        using var outside = new Form { Text = "Popup focus regression fixture", Width = 200, Height = 100 };
        outside.Show();
        outside.Activate();
        Pump(50);
        Require(GetActiveWindow() == outside.Handle, "Outside-action fixture did not activate its other window.");
        Require(!popup.Visible && !IsWindowVisible(popup.Handle), "An outside activation no longer closes the refreshed popup.");
        popup.UpdateSnapshot(Snapshot(31));
        Require(!IsWindowVisible(popup.Handle), "Refresh resurrected a popup dismissed by an outside action.");
    }

    private static UsagePopupForm Create(PopupStyle style)
    {
        var popup = new UsagePopupForm(closeOnDeactivate: true, popupStyle: style, showInTaskbarForSmokeTest: true);
        popup.UpdateSnapshot(Snapshot(20));
        popup.UpdateResetForecast(Forecast());
        return popup;
    }

    private static UsageSnapshot Snapshot(int used) => new()
    {
        IsLive = true,
        Status = ConnectionStatus.Connected,
        PlanType = "plus",
        Buckets = [new("codex:primary", "Weekly", "codex", "primary", used, 100 - used, 10080,
            DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds(), DateTimeOffset.Now.AddDays(6), null)]
    };

    private static ResetForecastSnapshot Forecast() => new()
    {
        IsLive = true,
        CheckedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        PublishedExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
        Probability24Hours = 39,
        Probability48Hours = 63,
        Confidence = "low"
    };

    private static void AssertVisible(UsagePopupForm popup, string label)
    {
        Require(popup.Visible && IsWindowVisible(popup.Handle), $"{label}: managed/native popup visibility diverged during refresh.");
        var controls = Descendants(popup).ToList();
        var visibleControls = controls.Where(control => control.Visible).ToList();
        Require(visibleControls.Count > 10 && visibleControls.OfType<Button>().Any(button => button.Text == "Close"),
            $"{label}: readable popup content or actions were lost.");
        foreach (var control in visibleControls)
            Require(control.IsHandleCreated && IsWindowVisible(control.Handle), $"{label}: visible content has no visible native handle ({control.Text}).");
    }

    private static void FocusAction(UsagePopupForm popup)
    {
        popup.Activate();
        Descendants(popup).OfType<Button>().First(button => button.Enabled).Focus();
        Pump(10);
        Require(popup.ContainsFocus, "Popup fixture could not acquire child focus.");
    }

    private static void MoveAwayFromCursor(Form popup)
    {
        var area = Screen.FromControl(popup).WorkingArea;
        popup.Location = new System.Drawing.Point(Cursor.Position.X < area.Left + area.Width / 2
            ? area.Right - popup.Width - 8 : area.Left + 8, area.Top + 8);
        Require(!popup.Bounds.Contains(Cursor.Position), "Popup fixture must keep the pointer outside its bounds.");
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds);
    }

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

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}
