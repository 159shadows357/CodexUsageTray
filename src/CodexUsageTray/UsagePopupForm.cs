using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace CodexUsageTray;

public sealed partial class UsagePopupForm : Form
{
    private const int ClassicContentWidth = 350;
    private const int MacContentWidth = 374;
    private const int PaperContentWidth = 370;

    private static readonly Color ClassicBackground = Color.FromArgb(22, 24, 29);
    private static readonly Color MacBackground = Color.FromArgb(10, 39, 68);
    private static readonly Color MacHeader = Color.FromArgb(20, 56, 82);
    private static readonly Color MacCard = Color.FromArgb(31, 57, 78);
    private static readonly Color MacSelected = Color.FromArgb(27, 71, 111);
    private static readonly Color MacMuted = Color.FromArgb(116, 143, 169);
    private static readonly Color MacText = Color.FromArgb(235, 241, 248);
    private static readonly Color MacBlue = Color.FromArgb(31, 132, 255);
    private static readonly Color PaperBackground = Color.FromArgb(250, 247, 241);
    private static readonly Color PaperInk = Color.FromArgb(17, 24, 39);
    private static readonly Color PaperMuted = Color.FromArgb(71, 85, 105);
    private static readonly Color PaperTrack = Color.FromArgb(224, 221, 216);

    private readonly FlowLayoutPanel _content;
    private readonly bool _closeOnDeactivate;
    private UsageSnapshot _snapshot = new();
    private OpenAiStatusSnapshot _openAiStatus = new();
    private DateTimeOffset _shownAt;
    private PopupStyle _popupStyle;
    private int _renderDepth;

    public UsagePopupForm(
        bool closeOnDeactivate = true,
        PopupStyle popupStyle = PopupStyle.Classic,
        bool showInTaskbarForSmokeTest = false)
    {
        _closeOnDeactivate = closeOnDeactivate;
        _popupStyle = popupStyle;
        Name = "UsagePopupForm";
        AccessibleName = "Codex Usage";
        Text = "Codex Usage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = showInTaskbarForSmokeTest;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ForeColor = Color.FromArgb(238, 241, 246);
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _content = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            AccessibleName = "Usage details"
        };
        Controls.Add(_content);
        ApplyVisualStyle();

        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape) Hide();
        };
    }

    public event EventHandler? RefreshRequested;

    internal DateTimeOffset LastHiddenAt { get; private set; } = DateTimeOffset.MinValue;

    public void SetPopupStyle(PopupStyle style)
    {
        if (_popupStyle == style) return;
        _popupStyle = style;
        UpdateSnapshot(_snapshot);
        if (Visible)
        {
            PositionNearTray();
            Invalidate();
        }
    }

    public void UpdateSnapshot(UsageSnapshot snapshot) => RenderSnapshot(snapshot, DateTimeOffset.UtcNow);

    private void RenderSnapshot(UsageSnapshot snapshot, DateTimeOffset now)
    {
        _snapshot = snapshot;
        _forecastFreshnessState = ForecastFreshnessState(now);
        var displaySnapshot = snapshot with { Buckets = PopupBuckets(snapshot).ToList() };
        var restoreFocus = _renderDepth == 0 && Visible && ContainsFocus;
        _renderDepth++;
        try
        {
            SuspendLayout();
            _content.SuspendLayout();
            try
            {
                ClearContent();
                ApplyVisualStyle();

                if (IsCadraStyle || DeviceDpi != 96)
                {
                    RenderDashboardStyle(displaySnapshot, !IsCadraStyle);
                }
                else if (_popupStyle == PopupStyle.MacOS)
                {
                    RenderMacStyle(displaySnapshot);
                }
                else if (_popupStyle == PopupStyle.Paper)
                {
                    RenderPaperStyle(displaySnapshot);
                }
                else
                {
                    RenderClassicStyle(displaySnapshot);
                }

                AddResetForecast();
                if (IsCadraStyle || _compactLayout) AddDashboardFooter(displaySnapshot);

                if (DesiredContentHeight() > MaximumPopupHeight())
                {
                    ClearContent();
                    ApplyCompactLayout();
                    RenderDashboardStyle(displaySnapshot, true);
                    AddResetForecast();
                    AddDashboardFooter(displaySnapshot);
                }
            }
            finally
            {
                _content.ResumeLayout(true);
                ResumeLayout(true);
            }

            FitWindowToContent();

            if (Visible) PositionNearTray();
        }
        finally
        {
            try
            {
                if (restoreFocus && Visible && !IsDisposed && IsHandleCreated)
                {
                    // Disposing the focused action can deactivate the form while its
                    // replacement controls are built. Restore only focus we owned.
                    Activate();
                    _content.SelectNextControl(null, true, true, true, false);
                }
                if (Visible) Invalidate(true);
            }
            finally { _renderDepth--; }
        }
    }

    internal void UpdateOpenAiStatus(OpenAiStatusSnapshot status)
    {
        _openAiStatus = status;
        UpdateSnapshot(_snapshot);
    }

    internal void UpdateResetForecast(ResetForecastSnapshot snapshot, bool enabled = true)
    {
        _resetForecast = snapshot;
        _resetForecastEnabled = enabled;
        UpdateSnapshot(_snapshot);
    }

    internal void RefreshForecastFreshness(DateTimeOffset now)
    {
        if (!_resetForecastEnabled || ForecastFreshnessState(now) == _forecastFreshnessState) return;
        RenderSnapshot(_snapshot, now);
    }

    public void ShowNearTray()
    {
        PositionNearTray();
        _shownAt = DateTimeOffset.Now;
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs eventArgs)
    {
        base.OnDeactivate(eventArgs);
        if (_renderDepth > 0 || !_closeOnDeactivate || !Visible || !IsHandleCreated || IsDisposed || Disposing) return;
        // WinForms raises Deactivate before the focused child loses focus. Check
        // after the activation messages settle, without queuing render-induced events.
        var shownAt = _shownAt;
        BeginInvoke((Action)(() => { if (_shownAt == shownAt) HideIfInactive(); }));
    }

    private void HideIfInactive()
    {
        if (IsDisposed || Disposing || _renderDepth > 0) return;
        if (ShouldHideOnDeactivate(
            _closeOnDeactivate,
            Visible,
            ContainsFocus,
            DateTimeOffset.Now - _shownAt,
            Bounds,
            Cursor.Position))
        {
            Hide();
        }
    }

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        base.OnVisibleChanged(eventArgs);
        if (!Visible) LastHiddenAt = DateTimeOffset.Now;
    }

    internal static bool ShouldHideOnDeactivate(
        bool closeOnDeactivate,
        bool visible,
        bool containsFocus,
        TimeSpan shownAge,
        Rectangle bounds,
        Point cursorPosition) =>
        closeOnDeactivate &&
        visible &&
        !containsFocus &&
        shownAge > TimeSpan.FromMilliseconds(500) &&
        !bounds.Contains(cursorPosition);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_popupStyle != PopupStyle.MacOS && !IsCadraStyle) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPanel.CreateRoundedRectangle(
            new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 3), Math.Max(1, ClientSize.Height - 3)),
            22);
        using var pen = new Pen(IsCadraStyle ? DashboardPalette.Line : Color.FromArgb(31, 105, 153), 1.5f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateSnapshot(_snapshot);
        if (Visible) PositionNearTray();
    }

    private void ApplyVisualStyle()
    {
        _compactLayout = false;
        _denseLayout = false;
        if (IsCadraStyle || DeviceDpi != 96)
        {
            ApplyCompactLayout();
        }
        else if (_popupStyle == PopupStyle.MacOS)
        {
            BackColor = MacBackground;
            _content.BackColor = MacBackground;
            _content.AutoScroll = false;
            _content.Padding = Padding.Empty;
            ClientSize = new Size(430, 710);
            Padding = new Padding(18, 16, 18, 14);
        }
        else if (_popupStyle == PopupStyle.Paper)
        {
            BackColor = PaperInk;
            _content.BackColor = PaperBackground;
            _content.AutoScroll = false;
            _content.Padding = new Padding(20, 22, 20, 18);
            ClientSize = new Size(430, 620);
            Padding = new Padding(4, 4, 14, 14);
        }
        else
        {
            BackColor = ClassicBackground;
            _content.BackColor = ClassicBackground;
            _content.AutoScroll = false;
            _content.Padding = Padding.Empty;
            ClientSize = new Size(400, 540);
            Padding = new Padding(16);
        }

        UpdateWindowRegion();
    }

    private void FitWindowToContent()
    {
        _content.AutoScroll = false;
        ClientSize = new Size(ClientSize.Width, Math.Min(DesiredContentHeight(), MaximumPopupHeight()));
        _content.PerformLayout();
    }

    private int DesiredContentHeight() => _content.Controls.Cast<Control>()
        .Sum(control => control.Height + control.Margin.Vertical) + _content.Padding.Vertical + Padding.Vertical + 2;

    private int MaximumPopupHeight()
    {
        var screen = Visible ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        return Math.Max(300, Math.Min(ScaleUi(1000), screen.WorkingArea.Height - 16));
    }

    private void UpdateWindowRegion()
    {
        Region? next = null;
        if ((_popupStyle == PopupStyle.MacOS || IsCadraStyle) && ClientSize.Width > 1 && ClientSize.Height > 1)
        {
            using var path = RoundedPanel.CreateRoundedRectangle(ClientRectangle, 24);
            next = new Region(path);
        }

        var previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private void PositionNearTray()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        var x = Math.Clamp(area.Right - Width - 8, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(area.Bottom - Height - 8, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
    }

    private bool CanFitAdditionalContent(int additionalHeight)
    {
        var contentHeight = _content.Controls.Cast<Control>()
            .Sum(control => control.Height + control.Margin.Vertical);
        var screen = Visible ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        var maxHeight = Math.Max(360, screen.WorkingArea.Height - 16);
        var insets = Padding.Vertical + (_popupStyle == PopupStyle.Paper ? _content.Padding.Vertical : 2);
        return contentHeight + additionalHeight + insets <= maxHeight;
    }

    private void ClearContent()
    {
        while (_content.Controls.Count > 0)
        {
            var control = _content.Controls[0];
            _content.Controls.RemoveAt(0);
            control.Dispose();
        }
    }

    private void RenderClassicStyle(UsageSnapshot snapshot)
    {
        AddClassicLabel("Codex Usage", 20, FontStyle.Bold, Color.White, 42);
        if (!string.IsNullOrWhiteSpace(snapshot.PlanType))
        {
            AddClassicLabel($"Plan: {snapshot.PlanType}", 9, FontStyle.Regular, Color.FromArgb(164, 174, 190), 25);
        }
        AddClassicLabel(StatusText(snapshot.Status), 10, FontStyle.Bold, StatusColor(snapshot.Status), 30);

        if (snapshot.Buckets.Count == 0)
        {
            AddClassicLabel("No rate-limit buckets returned", 10, FontStyle.Regular, Color.FromArgb(190, 197, 209), 40);
        }
        else
        {
            foreach (var bucket in PopupBuckets(snapshot))
            {
                AddClassicBucket(bucket, snapshot.TrayBucket?.Key == bucket.Key);
            }
        }

        AddClassicSeparator();
        AddClassicOpenAiStatus();
        AddClassicSeparator();
        AddClassicLabel(
            snapshot.ResetCreditsAvailable is null ? "Reset credits: unavailable" : $"Reset credits: {snapshot.ResetCreditsAvailable}",
            10,
            FontStyle.Regular,
            Color.FromArgb(213, 218, 226),
            28);
        var nextCredit = NextExpiringResetCredit(snapshot);
        if (nextCredit?.ExpiresAtLocal is not null)
        {
            AddClassicLabel(
                $"Next expiry: {FormatResetCreditExpiry(nextCredit.ExpiresAtLocal.Value)}",
                9,
                FontStyle.Regular,
                Color.FromArgb(164, 174, 190),
                26);
        }

        var showExtendedDetails = CanFitAdditionalContent(304 + (string.IsNullOrWhiteSpace(snapshot.Error) ? 0 : 48));
        if (showExtendedDetails && snapshot.TokenActivity.IsAvailable)
        {
            var activity = snapshot.TokenActivity;
            var latestLabel = activity.TodayOrLatestTokens is null
                ? "Latest tokens: unavailable"
                : $"Tokens ({activity.TodayOrLatestDate}): {FormatNumber(activity.TodayOrLatestTokens)}";
            AddClassicLabel(latestLabel, 10, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
            AddClassicLabel($"Lifetime tokens: {FormatNumber(activity.LifetimeTokens)}", 10, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
            AddClassicLabel($"Peak daily tokens: {FormatNumber(activity.PeakDailyTokens)}", 10, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
        }
        else if (showExtendedDetails)
        {
            AddClassicLabel("Token activity unavailable", 10, FontStyle.Regular, Color.FromArgb(164, 174, 190), 28);
        }

        if (showExtendedDetails)
        {
            AddClassicSeparator();
            AddClassicApiEquivalent(snapshot.ApiEquivalent);
        }
        else
        {
            AddClassicLabel("Token and API details: Usage details", 9, FontStyle.Regular, Color.FromArgb(164, 174, 190), 28);
        }
        AddClassicSeparator();
        AddClassicLabel($"Quota updated {snapshot.UpdatedAt.ToLocalTime():MMM d, yyyy HH:mm:ss}", 9, FontStyle.Regular, Color.FromArgb(164, 174, 190), 24);
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            AddClassicLabel(SecretSanitizer.Sanitize(snapshot.Error), 9, FontStyle.Regular, Color.FromArgb(244, 129, 129), 48);
        }
        AddClassicActions();
    }

    private void AddClassicBucket(RateLimitBucket bucket, bool isTrayBucket)
    {
        var displayName = FormatBucketDisplayName(bucket, isTrayBucket);
        var remainingPercent = isTrayBucket ? UsageSnapshot.EffectiveRemainingPercent(bucket) : bucket.RemainingPercent;
        var panel = new Panel
        {
            Width = ClassicContentWidth,
            Height = 92,
            Margin = new Padding(0, 4, 0, 4),
            BackColor = Color.FromArgb(31, 34, 41),
            AccessibleName = $"{(isTrayBucket ? "Tray indicator, " : string.Empty)}{displayName}, {remainingPercent}% left"
        };
        var title = CreateLabel(displayName, 10, FontStyle.Bold, Color.White);
        title.Location = new Point(12, 9);
        title.Size = new Size(235, 24);
        title.AutoEllipsis = true;

        var percent = CreateLabel($"{remainingPercent}% left", 10, FontStyle.Bold, ClassicPercentColor(remainingPercent));
        percent.TextAlign = ContentAlignment.TopRight;
        percent.Location = new Point(245, 9);
        percent.Size = new Size(92, 24);

        var progress = new UsageProgressBar
        {
            Value = remainingPercent,
            Location = new Point(12, 39),
            Size = new Size(325, 8)
        };
        var reset = CreateLabel(
            bucket.ResetsAtLocal is null ? "Reset unavailable" : $"Reset {bucket.ResetsAtLocal.Value:MMM d, yyyy HH:mm}",
            8.5f,
            FontStyle.Regular,
            Color.FromArgb(164, 174, 190));
        reset.Location = new Point(12, 56);
        reset.Size = new Size(325, 24);

        panel.Controls.AddRange([title, percent, progress, reset]);
        _content.Controls.Add(panel);
    }

    private void AddClassicLabel(string text, float size, FontStyle style, Color color, int height)
    {
        var label = CreateLabel(text, size, style, color);
        label.Width = ClassicContentWidth;
        label.Height = height;
        label.Margin = new Padding(0);
        label.AutoEllipsis = true;
        _content.Controls.Add(label);
    }

    private void AddClassicSeparator() => _content.Controls.Add(new Panel
    {
        BackColor = Color.FromArgb(55, 59, 69),
        Width = ClassicContentWidth,
        Height = 1,
        Margin = new Padding(0, 8, 0, 8)
    });

    private void AddClassicActions()
    {
        var panel = new Panel
        {
            Width = ClassicContentWidth,
            Height = 38,
            BackColor = ClassicBackground,
            Margin = new Padding(0, 4, 0, 0),
            AccessibleName = "Popup actions"
        };
        var refresh = CreateButton(
            "Refresh",
            Color.FromArgb(119, 180, 255),
            ClassicBackground,
            Color.FromArgb(45, 49, 58),
            Color.FromArgb(55, 59, 69));
        refresh.Location = new Point(202, 4);
        refresh.Size = new Size(78, 30);
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var close = CreateButton(
            "Close",
            Color.FromArgb(213, 218, 226),
            ClassicBackground,
            Color.FromArgb(45, 49, 58),
            Color.FromArgb(55, 59, 69));
        close.Location = new Point(284, 4);
        close.Size = new Size(66, 30);
        close.Click += (_, _) => Hide();
        panel.Controls.AddRange([refresh, close]);
        _content.Controls.Add(panel);
    }

    private void AddClassicOpenAiStatus()
    {
        var panel = new Panel
        {
            Width = ClassicContentWidth,
            Height = 80,
            Margin = new Padding(0, 2, 0, 8),
            BackColor = Color.FromArgb(31, 34, 41),
            AccessibleName = $"OpenAI status, Codex {OpenAiStatusClient.DisplayText(_openAiStatus.Codex)}, " +
                $"ChatGPT {OpenAiStatusClient.DisplayText(_openAiStatus.ChatGpt)}"
        };
        var title = CreateLabel(OpenAiStatusHeading(), 9, FontStyle.Bold, Color.FromArgb(190, 197, 209));
        title.Location = new Point(12, 5);
        title.Size = new Size(326, 20);
        AddOpenAiStatusGraphRow(
            panel,
            28,
            "Codex",
            _openAiStatus.Codex,
            Color.FromArgb(55, 59, 69),
            OpenAiStatusColor(_openAiStatus.Codex),
            Color.White,
            OpenAiStatusColor(_openAiStatus.Codex));
        AddOpenAiStatusGraphRow(
            panel,
            52,
            "ChatGPT",
            _openAiStatus.ChatGpt,
            Color.FromArgb(55, 59, 69),
            OpenAiStatusColor(_openAiStatus.ChatGpt),
            Color.White,
            OpenAiStatusColor(_openAiStatus.ChatGpt));
        panel.Controls.Add(title);
        _content.Controls.Add(panel);
    }

    private void AddClassicApiEquivalent(ApiEquivalentEstimate estimate)
    {
        if (!estimate.IsAvailable)
        {
            AddClassicLabel("API-equivalent estimate unavailable", 10, FontStyle.Regular, Color.FromArgb(164, 174, 190), 28);
            return;
        }

        AddClassicLabel(
            $"API equivalent: {FormatEstimatedUsd(estimate.EstimatedUsd, estimate.UnpricedTokens)}",
            10.5f,
            FontStyle.Bold,
            Color.FromArgb(119, 224, 177),
            28);
        AddClassicLabel($"Local history: {FormatCompactNumber(estimate.TotalTokens)} tokens", 9.5f, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
        AddClassicLabel("Project breakdown: Usage details", 9, FontStyle.Regular, Color.FromArgb(190, 197, 209), 25);
        AddClassicLabel(FormatApiEstimateNote(estimate, 0), 8.5f, FontStyle.Regular, Color.FromArgb(164, 174, 190), 25);
    }

    private void RenderPaperStyle(UsageSnapshot snapshot)
    {
        AddPaperHeader(snapshot);

        if (snapshot.Buckets.Count == 0)
        {
            AddPaperEmptyState();
        }
        else
        {
            foreach (var bucket in PopupBuckets(snapshot))
            {
                AddPaperBucket(bucket, snapshot.TrayBucket?.Key == bucket.Key);
            }
        }

        AddPaperDivider();
        AddPaperOpenAiStatus();
        AddPaperDivider();
        AddPaperCredits(snapshot);
        AddPaperActions();
        if (CanFitAdditionalContent(300 + (string.IsNullOrWhiteSpace(snapshot.Error) ? 0 : 60)))
        {
            AddPaperDivider();
            AddPaperTokenActivity(snapshot.TokenActivity);
            AddPaperDivider();
            AddPaperApiEquivalent(snapshot.ApiEquivalent);
        }
        else if (CanFitAdditionalContent(54 + (string.IsNullOrWhiteSpace(snapshot.Error) ? 0 : 60)))
        {
            AddPaperDetailsHint();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            AddPaperError(snapshot.Error);
        }
    }

    private void AddPaperHeader(UsageSnapshot snapshot)
    {
        var header = new Panel
        {
            Width = PaperContentWidth,
            Height = 76,
            BackColor = PaperBackground,
            Margin = new Padding(0, 0, 0, 16),
            AccessibleName = "Codex account summary"
        };
        var title = CreatePaperLabel("Codex", 15, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(90, 31);

        var plan = CreatePaperLabel($"· {FormatPlanName(snapshot.PlanType)}", 10.5f, FontStyle.Bold, PaperMuted, monospace: true);
        plan.Location = new Point(90, 5);
        plan.Size = new Size(202, 27);
        plan.AutoEllipsis = true;

        var updated = CreatePaperLabel($"Quota updated {FormatUpdatedAge(snapshot.UpdatedAt)}", 10, FontStyle.Regular, PaperMuted);
        updated.Location = new Point(0, 38);
        updated.Size = new Size(230, 28);

        var statusColor = snapshot.Status == ConnectionStatus.Connected ? PaperMuted : Color.FromArgb(185, 28, 28);
        var status = CreatePaperLabel(StatusText(snapshot.Status), 9, FontStyle.Bold, statusColor, monospace: true);
        status.TextAlign = ContentAlignment.TopRight;
        status.Location = new Point(230, 41);
        status.Size = new Size(140, 25);

        header.Controls.AddRange([title, plan, updated, status]);
        _content.Controls.Add(header);
    }

    private void AddPaperBucket(RateLimitBucket bucket, bool isTrayBucket)
    {
        var displayName = FormatBucketDisplayName(bucket, isTrayBucket);
        var remainingPercent = isTrayBucket ? UsageSnapshot.EffectiveRemainingPercent(bucket) : bucket.RemainingPercent;
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = 88,
            BackColor = PaperBackground,
            Margin = new Padding(0, 0, 0, 18),
            AccessibleName = $"{(isTrayBucket ? "Tray indicator, " : string.Empty)}{displayName}, {remainingPercent}% left"
        };
        var title = CreatePaperLabel(displayName, 11.5f, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(238, 28);
        title.AutoEllipsis = true;

        var percent = CreatePaperLabel($"{remainingPercent}% left", 11.5f, FontStyle.Bold, PaperInk, monospace: true);
        percent.TextAlign = ContentAlignment.TopRight;
        percent.Location = new Point(238, 0);
        percent.Size = new Size(132, 28);

        var progress = new UsageProgressBar
        {
            Value = remainingPercent,
            Location = new Point(0, 36),
            Size = new Size(PaperContentWidth, 9),
            TrackColor = PaperTrack,
            FillColor = PaperInk,
            Rounded = true
        };

        var reset = CreatePaperLabel(FormatPaperReset(bucket.ResetsAtLocal), 9.5f, FontStyle.Regular, PaperMuted);
        reset.Location = new Point(0, 54);
        reset.Size = new Size(PaperContentWidth, 28);
        reset.AutoEllipsis = true;

        panel.Controls.AddRange([title, percent, progress, reset]);
        _content.Controls.Add(panel);
    }

    private void AddPaperEmptyState()
    {
        var label = CreatePaperLabel("No rate-limit buckets returned", 10, FontStyle.Regular, PaperMuted);
        label.Width = PaperContentWidth;
        label.Height = 48;
        label.Margin = new Padding(0, 0, 0, 16);
        label.TextAlign = ContentAlignment.MiddleLeft;
        _content.Controls.Add(label);
    }

    private void AddPaperDivider() => _content.Controls.Add(new Panel
    {
        BackColor = PaperInk,
        Width = PaperContentWidth,
        Height = 1,
        Margin = new Padding(0, 2, 0, 16)
    });

    private void AddPaperOpenAiStatus()
    {
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = 80,
            BackColor = PaperBackground,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"OpenAI status, Codex {OpenAiStatusClient.DisplayText(_openAiStatus.Codex)}, " +
                $"ChatGPT {OpenAiStatusClient.DisplayText(_openAiStatus.ChatGpt)}"
        };
        var title = CreatePaperLabel(OpenAiStatusHeading(), 10, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(PaperContentWidth, 24);
        AddOpenAiStatusGraphRow(
            panel,
            28,
            "Codex",
            _openAiStatus.Codex,
            PaperTrack,
            PaperOpenAiStatusColor(_openAiStatus.Codex),
            PaperInk,
            PaperOpenAiStatusColor(_openAiStatus.Codex),
            monospace: true);
        AddOpenAiStatusGraphRow(
            panel,
            54,
            "ChatGPT",
            _openAiStatus.ChatGpt,
            PaperTrack,
            PaperOpenAiStatusColor(_openAiStatus.ChatGpt),
            PaperInk,
            PaperOpenAiStatusColor(_openAiStatus.ChatGpt),
            monospace: true);
        panel.Controls.Add(title);
        _content.Controls.Add(panel);
    }

    private void AddPaperCredits(UsageSnapshot snapshot)
    {
        var nextCredit = NextExpiringResetCredit(snapshot);
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = 66,
            BackColor = PaperBackground,
            Margin = new Padding(0, 0, 0, 14),
            AccessibleName = "Reset credits"
        };
        var title = CreatePaperLabel("Reset Credits", 12, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(225, 29);
        var value = CreatePaperLabel(
            snapshot.ResetCreditsAvailable is null ? "unavailable" : $"{snapshot.ResetCreditsAvailable} available",
            10.5f,
            FontStyle.Bold,
            PaperInk,
            monospace: true);
        value.TextAlign = ContentAlignment.TopRight;
        value.Location = new Point(220, 2);
        value.Size = new Size(150, 27);
        var note = CreatePaperLabel(
            nextCredit?.ExpiresAtLocal is null
                ? "Expiry details unavailable"
                : $"Next expiry {FormatResetCreditExpiry(nextCredit.ExpiresAtLocal.Value)}",
            9,
            FontStyle.Regular,
            PaperMuted);
        note.Location = new Point(0, 36);
        note.Size = new Size(PaperContentWidth, 25);
        panel.Controls.AddRange([title, value, note]);
        _content.Controls.Add(panel);
    }

    private void AddPaperTokenActivity(TokenActivity activity)
    {
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = activity.IsAvailable ? 112 : 66,
            BackColor = PaperBackground,
            Margin = new Padding(0),
            AccessibleName = "Token activity"
        };
        var title = CreatePaperLabel("Token Activity", 12, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(220, 29);
        panel.Controls.Add(title);

        if (activity.IsAvailable)
        {
            AddPaperValueRow(panel, 36, $"Latest ({activity.TodayOrLatestDate ?? "unknown"})", FormatCompactNumber(activity.TodayOrLatestTokens));
            AddPaperValueRow(panel, 61, "Lifetime", FormatCompactNumber(activity.LifetimeTokens));
            AddPaperValueRow(panel, 86, "Peak daily", FormatCompactNumber(activity.PeakDailyTokens));
        }
        else
        {
            AddPaperValueRow(panel, 36, "Status", "Unavailable");
        }

        _content.Controls.Add(panel);
    }

    private static void AddPaperValueRow(Control parent, int top, string caption, string value)
    {
        var captionLabel = CreatePaperLabel(caption, 9.5f, FontStyle.Regular, PaperMuted);
        captionLabel.Location = new Point(0, top);
        captionLabel.Size = new Size(245, 23);
        captionLabel.AutoEllipsis = true;
        var valueLabel = CreatePaperLabel(value, 10, FontStyle.Bold, PaperInk, monospace: true);
        valueLabel.TextAlign = ContentAlignment.TopRight;
        valueLabel.Location = new Point(245, top);
        valueLabel.Size = new Size(125, 23);
        valueLabel.AutoEllipsis = true;
        parent.Controls.AddRange([captionLabel, valueLabel]);
    }

    private void AddPaperApiEquivalent(ApiEquivalentEstimate estimate)
    {
        var rowCount = estimate.IsAvailable ? 3 : 1;
        var noteTop = 36 + (rowCount * 25);
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = noteTop + 30,
            BackColor = PaperBackground,
            Margin = new Padding(0),
            AccessibleName = "Estimated API-equivalent cost"
        };
        var title = CreatePaperLabel("API Equivalent", 12, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(220, 29);
        panel.Controls.Add(title);

        if (estimate.IsAvailable)
        {
            AddPaperValueRow(panel, 36, "Estimated cost", FormatEstimatedUsd(estimate.EstimatedUsd, estimate.UnpricedTokens));
            AddPaperValueRow(panel, 61, "Local history", $"{FormatCompactNumber(estimate.TotalTokens)} tok");
            AddPaperValueRow(panel, 86, "Projects", "Usage details");
        }
        else
        {
            AddPaperValueRow(panel, 36, "Status", "Unavailable");
        }

        var note = CreatePaperLabel(
            estimate.IsAvailable ? FormatApiEstimateNote(estimate, 0) : "No local Codex token history found",
            8.5f,
            FontStyle.Regular,
            PaperMuted);
        note.Location = new Point(0, noteTop);
        note.Size = new Size(PaperContentWidth, 25);
        note.AutoEllipsis = true;
        panel.Controls.Add(note);
        _content.Controls.Add(panel);
    }

    private void AddPaperDetailsHint()
    {
        var label = CreatePaperLabel("Token and API details are available in Usage details", 9, FontStyle.Regular, PaperMuted);
        label.Width = PaperContentWidth;
        label.Height = 38;
        label.Margin = new Padding(0, 16, 0, 0);
        label.AutoEllipsis = true;
        _content.Controls.Add(label);
    }

    private void AddPaperActions()
    {
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = 40,
            BackColor = PaperBackground,
            Margin = new Padding(0, 14, 0, 0),
            AccessibleName = "Popup actions"
        };
        var refresh = CreateButton("Refresh", PaperInk, PaperBackground, PaperTrack, Color.FromArgb(210, 207, 202));
        refresh.Location = new Point(218, 4);
        refresh.Size = new Size(78, 31);
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var close = CreateButton("Close", PaperInk, PaperBackground, PaperTrack, Color.FromArgb(210, 207, 202));
        close.Location = new Point(302, 4);
        close.Size = new Size(68, 31);
        close.Click += (_, _) => Hide();
        panel.Controls.AddRange([refresh, close]);
        _content.Controls.Add(panel);
    }

    private void AddPaperError(string error)
    {
        var label = CreatePaperLabel(SecretSanitizer.Sanitize(error), 9, FontStyle.Regular, Color.FromArgb(185, 28, 28));
        label.Width = PaperContentWidth;
        label.Height = 46;
        label.Margin = new Padding(0, 14, 0, 0);
        label.AutoEllipsis = true;
        _content.Controls.Add(label);
    }

    private void RenderMacStyle(UsageSnapshot snapshot)
    {
        AddMacHeader(snapshot.PlanType);
        AddMacStatusRow(snapshot);
        var screen = Visible ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        var compactBuckets = ShouldUseCompactMacBuckets(
            screen.WorkingArea.Height,
            snapshot.Buckets.Count,
            !string.IsNullOrWhiteSpace(snapshot.Error));

        if (snapshot.Buckets.Count == 0)
        {
            AddMacEmptyCard("No rate-limit buckets returned");
        }
        else
        {
            foreach (var bucket in PopupBuckets(snapshot))
            {
                AddMacBucket(bucket, snapshot.TrayBucket?.Key == bucket.Key, compactBuckets);
            }
        }

        if (!compactBuckets || string.IsNullOrWhiteSpace(snapshot.Error))
        {
            AddMacOpenAiStatusCard();
        }
        AddMacAccountCard(snapshot);
        if (CanFitAdditionalContent(420 + (string.IsNullOrWhiteSpace(snapshot.Error) ? 0 : 80)))
        {
            AddMacTokenCard(snapshot.TokenActivity);
            AddMacApiEquivalentCard(snapshot.ApiEquivalent);
        }
        else
        {
            AddMacDetailsHint();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            AddMacErrorCard(snapshot.Error);
        }

        AddMacFooter(snapshot);
    }

    private void AddMacHeader(string? planType)
    {
        var header = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 54,
            Radius = 16,
            BackColor = MacHeader,
            Margin = new Padding(0, 0, 0, 10)
        };
        var title = CreateLabel("Codex Usage", 11, FontStyle.Bold, Color.FromArgb(181, 200, 218));
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Location = new Point(18, 8);
        title.Size = new Size(164, 38);

        var selected = new RoundedPanel
        {
            Location = new Point(190, 6),
            Size = new Size(176, 42),
            Radius = 12,
            BackColor = MacSelected
        };
        var selectedLabel = CreateLabel("Codex", 11, FontStyle.Bold, MacBlue);
        selectedLabel.TextAlign = ContentAlignment.MiddleCenter;
        selectedLabel.Dock = DockStyle.Fill;
        selected.Controls.Add(selectedLabel);
        header.Controls.AddRange([title, selected]);
        header.AccessibleName = string.IsNullOrWhiteSpace(planType) ? "Codex" : $"Codex, {planType} plan";
        _content.Controls.Add(header);
    }

    private void AddMacStatusRow(UsageSnapshot snapshot)
    {
        var row = new Panel
        {
            Width = MacContentWidth,
            Height = 30,
            BackColor = MacBackground,
            Margin = new Padding(0, 0, 0, 6)
        };
        var status = CreateLabel(StatusText(snapshot.Status), 9, FontStyle.Bold, StatusColor(snapshot.Status));
        status.Location = new Point(4, 1);
        status.Size = new Size(190, 24);
        var plan = CreateLabel(
            string.IsNullOrWhiteSpace(snapshot.PlanType) ? "Codex account" : snapshot.PlanType,
            9,
            FontStyle.Regular,
            MacMuted);
        plan.TextAlign = ContentAlignment.TopRight;
        plan.Location = new Point(194, 1);
        plan.Size = new Size(176, 24);
        row.Controls.AddRange([status, plan]);
        _content.Controls.Add(row);
    }

    private void AddMacOpenAiStatusCard()
    {
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 88,
            Radius = 16,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"OpenAI status, Codex {OpenAiStatusClient.DisplayText(_openAiStatus.Codex)}, " +
                $"ChatGPT {OpenAiStatusClient.DisplayText(_openAiStatus.ChatGpt)}"
        };
        var title = CreateLabel(OpenAiStatusHeading(), 9, FontStyle.Bold, MacMuted);
        title.Location = new Point(18, 8);
        title.Size = new Size(336, 20);
        AddOpenAiStatusGraphRow(
            card,
            34,
            "Codex",
            _openAiStatus.Codex,
            Color.FromArgb(70, 94, 115),
            OpenAiStatusColor(_openAiStatus.Codex),
            MacText,
            OpenAiStatusColor(_openAiStatus.Codex));
        AddOpenAiStatusGraphRow(
            card,
            60,
            "ChatGPT",
            _openAiStatus.ChatGpt,
            Color.FromArgb(70, 94, 115),
            OpenAiStatusColor(_openAiStatus.ChatGpt),
            MacText,
            OpenAiStatusColor(_openAiStatus.ChatGpt));
        card.Controls.Add(title);
        _content.Controls.Add(card);
    }

    private void AddMacBucket(RateLimitBucket bucket, bool isTrayBucket, bool compact)
    {
        var displayName = FormatBucketDisplayName(bucket, isTrayBucket);
        var remainingPercent = isTrayBucket ? UsageSnapshot.EffectiveRemainingPercent(bucket) : bucket.RemainingPercent;
        var accent = MacAccentColor(remainingPercent);
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = compact ? 82 : 116,
            Radius = 18,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"{(isTrayBucket ? "Tray indicator, " : string.Empty)}{displayName}, {remainingPercent}% left"
        };
        var title = CreateLabel(displayName, 11, FontStyle.Bold, MacText);
        title.Location = new Point(18, 15);
        title.Size = new Size(245, 26);
        title.AutoEllipsis = true;

        var percent = CreateLabel($"{remainingPercent}%", 12, FontStyle.Bold, accent);
        percent.TextAlign = ContentAlignment.TopRight;
        percent.Location = new Point(265, 13);
        percent.Size = new Size(89, 28);

        var progress = new UsageProgressBar
        {
            Value = remainingPercent,
            Location = new Point(18, compact ? 42 : 49),
            Size = new Size(336, 10),
            TrackColor = Color.FromArgb(70, 94, 115),
            FillColor = accent,
            Rounded = true
        };

        var resetsIn = CreateLabel(
            compact ? $"Reset {FormatResetRemaining(bucket.ResetsAtLocal)}" : $"Resets in: {FormatResetRemaining(bucket.ResetsAtLocal)}",
            9,
            FontStyle.Bold,
            MacMuted);
        resetsIn.Location = new Point(18, compact ? 60 : 73);
        resetsIn.Size = new Size(compact ? 336 : 180, compact ? 18 : 26);
        resetsIn.AutoEllipsis = true;
        var resetAt = CreateLabel(
            bucket.ResetsAtLocal is null ? "Reset unavailable" : bucket.ResetsAtLocal.Value.ToString("MMM d HH:mm"),
            9,
            FontStyle.Bold,
            accent);
        resetAt.TextAlign = ContentAlignment.TopRight;
        resetAt.Location = new Point(198, 73);
        resetAt.Size = new Size(156, 26);

        card.Controls.AddRange(compact
            ? [title, percent, progress, resetsIn]
            : [title, percent, progress, resetsIn, resetAt]);
        _content.Controls.Add(card);
    }

    internal static bool ShouldUseCompactMacBuckets(int workingAreaHeight, int bucketCount, bool hasError)
    {
        const int fixedContentHeight = 100 + 98 + 126 + 54 + 66 + 32;
        const int fullBucketHeight = 126;
        const int errorHeight = 80;
        var maximumHeight = Math.Max(360, workingAreaHeight - 16);
        var desiredHeight = fixedContentHeight + (Math.Max(0, bucketCount) * fullBucketHeight) + (hasError ? errorHeight : 0);
        return desiredHeight > maximumHeight;
    }

    private void AddMacEmptyCard(string message)
    {
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 72,
            Radius = 18,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10)
        };
        var label = CreateLabel(message, 10, FontStyle.Regular, MacMuted);
        label.TextAlign = ContentAlignment.MiddleCenter;
        label.Dock = DockStyle.Fill;
        card.Controls.Add(label);
        _content.Controls.Add(card);
    }

    private void AddMacAccountCard(UsageSnapshot snapshot)
    {
        var nextCredit = NextExpiringResetCredit(snapshot);
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = nextCredit?.ExpiresAtLocal is null ? 90 : 116,
            Radius = 18,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Account summary"
        };
        var header = CreateLabel("Account", 11, FontStyle.Bold, MacText);
        header.Location = new Point(18, 13);
        header.Size = new Size(200, 26);
        AddMacValueRow(card, 43, "Plan", snapshot.PlanType ?? "Unavailable");
        AddMacValueRow(
            card,
            66,
            "Reset credits",
            snapshot.ResetCreditsAvailable?.ToString(CultureInfo.CurrentCulture) ?? "Unavailable");
        if (nextCredit?.ExpiresAtLocal is not null)
        {
            AddMacValueRow(card, 90, "Next expiry", FormatResetCreditExpiryCompact(nextCredit.ExpiresAtLocal.Value));
        }
        card.Controls.Add(header);
        _content.Controls.Add(card);
    }

    private static RateLimitResetCredit? NextExpiringResetCredit(UsageSnapshot snapshot) =>
        snapshot.ResetCredits?
            .Where(credit =>
                string.Equals(credit.Status, "available", StringComparison.OrdinalIgnoreCase) &&
                credit.ExpiresAtLocal is not null)
            .OrderBy(credit => credit.ExpiresAtLocal)
            .ThenBy(credit => credit.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string FormatResetCreditExpiry(DateTimeOffset value) =>
        value.ToString("MMM d, yyyy HH:mm 'local'", CultureInfo.InvariantCulture);

    private static string FormatResetCreditExpiryCompact(DateTimeOffset value) =>
        value.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    private void AddMacTokenCard(TokenActivity activity)
    {
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = activity.IsAvailable ? 148 : 82,
            Radius = 18,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Token activity"
        };
        var header = CreateLabel("Token activity", 11, FontStyle.Bold, MacText);
        header.Location = new Point(18, 13);
        header.Size = new Size(210, 26);
        card.Controls.Add(header);

        if (activity.IsAvailable)
        {
            AddMacValueRow(card, 46, $"Latest ({activity.TodayOrLatestDate ?? "unknown"})", FormatCompactNumber(activity.TodayOrLatestTokens));
            AddMacValueRow(card, 74, "Lifetime", FormatCompactNumber(activity.LifetimeTokens));
            AddMacValueRow(card, 102, "Peak daily", FormatCompactNumber(activity.PeakDailyTokens));
        }
        else
        {
            AddMacValueRow(card, 48, "Status", "Unavailable");
        }

        _content.Controls.Add(card);
    }

    private static void AddMacValueRow(Control parent, int top, string caption, string value)
    {
        var captionLabel = CreateLabel(caption, 9.5f, FontStyle.Regular, MacMuted);
        captionLabel.Location = new Point(18, top);
        captionLabel.Size = new Size(218, 23);
        captionLabel.AutoEllipsis = true;
        var valueLabel = CreateLabel(value, 10, FontStyle.Bold, Color.FromArgb(205, 222, 239));
        valueLabel.TextAlign = ContentAlignment.TopRight;
        valueLabel.Location = new Point(236, top);
        valueLabel.Size = new Size(118, 23);
        valueLabel.AutoEllipsis = true;
        parent.Controls.AddRange([captionLabel, valueLabel]);
    }

    private void AddMacApiEquivalentCard(ApiEquivalentEstimate estimate)
    {
        var rowCount = estimate.IsAvailable ? 3 : 1;
        var noteTop = 46 + (rowCount * 28);
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = noteTop + 38,
            Radius = 18,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Estimated API-equivalent cost"
        };
        var header = CreateLabel("API equivalent", 11, FontStyle.Bold, MacText);
        header.Location = new Point(18, 13);
        header.Size = new Size(210, 26);
        card.Controls.Add(header);

        if (estimate.IsAvailable)
        {
            AddMacValueRow(card, 46, "Estimated cost", FormatEstimatedUsd(estimate.EstimatedUsd, estimate.UnpricedTokens));
            AddMacValueRow(card, 74, "Local history", $"{FormatCompactNumber(estimate.TotalTokens)} tok");
            AddMacValueRow(card, 102, "Projects", "Usage details");
        }
        else
        {
            AddMacValueRow(card, 46, "Status", "Unavailable");
        }

        var note = CreateLabel(
            estimate.IsAvailable ? FormatApiEstimateNote(estimate, 0) : "No local Codex token history found",
            8.5f,
            FontStyle.Regular,
            MacMuted);
        note.Location = new Point(18, noteTop);
        note.Size = new Size(336, 24);
        note.AutoEllipsis = true;
        card.Controls.Add(note);
        _content.Controls.Add(card);
    }

    private void AddMacDetailsHint()
    {
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 44,
            Radius = 14,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Token and API details are available in Usage details"
        };
        var label = CreateLabel("Token and API details: Usage details", 9, FontStyle.Regular, MacMuted);
        label.Location = new Point(16, 10);
        label.Size = new Size(342, 24);
        card.Controls.Add(label);
        _content.Controls.Add(card);
    }

    private void AddMacErrorCard(string error)
    {
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 70,
            Radius = 16,
            BackColor = Color.FromArgb(70, 43, 54),
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Connection error"
        };
        var label = CreateLabel(SecretSanitizer.Sanitize(error), 9, FontStyle.Regular, Color.FromArgb(255, 155, 155));
        label.Location = new Point(16, 10);
        label.Size = new Size(342, 50);
        label.AutoEllipsis = true;
        card.Controls.Add(label);
        _content.Controls.Add(card);
    }

    private void AddMacFooter(UsageSnapshot snapshot)
    {
        var footer = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 66,
            Radius = 16,
            BackColor = Color.FromArgb(12, 48, 82),
            Margin = new Padding(0, 0, 0, 0)
        };
        var updated = CreateLabel($"Quota updated {FormatUpdatedAge(snapshot.UpdatedAt)}", 9, FontStyle.Bold, MacMuted);
        updated.Location = new Point(16, 8);
        updated.Size = new Size(210, 22);

        var refresh = CreateButton("Refresh", MacBlue);
        refresh.Location = new Point(218, 31);
        refresh.Size = new Size(72, 27);
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        var close = CreateButton("Close", Color.FromArgb(176, 198, 220));
        close.Location = new Point(292, 31);
        close.Size = new Size(66, 27);
        close.Click += (_, _) => Hide();

        footer.Controls.AddRange([updated, refresh, close]);
        _content.Controls.Add(footer);
    }

    private static Button CreateButton(
        string text,
        Color color,
        Color? background = null,
        Color? hoverBackground = null,
        Color? pressedBackground = null)
    {
        var backColor = background ?? Color.FromArgb(12, 48, 82);
        var font = new Font("Segoe UI", 9, FontStyle.Regular);
        var button = new Button
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = backColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hoverBackground ?? Color.FromArgb(24, 67, 104);
        button.FlatAppearance.MouseDownBackColor = pressedBackground ?? Color.FromArgb(30, 78, 119);
        button.Disposed += (_, _) => font.Dispose();
        return button;
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color)
        => CreateFontLabel(text, "Segoe UI", size, style, color);

    private static Label CreatePaperLabel(
        string text,
        float size,
        FontStyle style,
        Color color,
        bool monospace = false)
        => CreateFontLabel(text, monospace ? "Consolas" : "Segoe UI", size, style, color);

    private static Label CreateFontLabel(string text, string fontFamily, float size, FontStyle style, Color color)
    {
        var font = new Font(fontFamily, size, style);
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false,
            UseMnemonic = false
        };
        label.Disposed += (_, _) => font.Dispose();
        return label;
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

    private static Color StatusColor(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => Color.FromArgb(64, 221, 116),
        ConnectionStatus.Loading => Color.FromArgb(88, 166, 255),
        ConnectionStatus.DataStale => Color.FromArgb(255, 172, 62),
        _ => Color.FromArgb(255, 102, 102)
    };

    private static Color OpenAiStatusColor(OpenAiServiceHealth status) => status switch
    {
        OpenAiServiceHealth.Operational => Color.FromArgb(64, 221, 116),
        OpenAiServiceHealth.Loading => Color.FromArgb(88, 166, 255),
        OpenAiServiceHealth.UnderMaintenance or OpenAiServiceHealth.DegradedPerformance => Color.FromArgb(255, 172, 62),
        OpenAiServiceHealth.PartialOutage or OpenAiServiceHealth.MajorOutage => Color.FromArgb(255, 102, 102),
        _ => Color.FromArgb(164, 174, 190)
    };

    private static Color PaperOpenAiStatusColor(OpenAiServiceHealth status) => status switch
    {
        OpenAiServiceHealth.Operational => Color.FromArgb(22, 101, 52),
        OpenAiServiceHealth.Loading => Color.FromArgb(30, 64, 175),
        OpenAiServiceHealth.UnderMaintenance or OpenAiServiceHealth.DegradedPerformance => Color.FromArgb(180, 83, 9),
        OpenAiServiceHealth.PartialOutage or OpenAiServiceHealth.MajorOutage => Color.FromArgb(185, 28, 28),
        _ => PaperMuted
    };

    private static void AddOpenAiStatusGraphRow(
        Control parent,
        int top,
        string service,
        OpenAiServiceHealth health,
        Color trackColor,
        Color fillColor,
        Color serviceColor,
        Color valueColor,
        bool monospace = false)
    {
        var left = parent.Width switch
        {
            ClassicContentWidth => 12,
            PaperContentWidth => 0,
            _ => 18
        };
        var right = parent.Width switch
        {
            ClassicContentWidth => 12,
            PaperContentWidth => 0,
            _ => 20
        };
        const int serviceWidth = 70;
        const int graphWidth = 112;
        const int gap = 10;
        var serviceLabel = monospace
            ? CreatePaperLabel(service, 9, FontStyle.Bold, serviceColor, monospace: true)
            : CreateLabel(service, 9, FontStyle.Bold, serviceColor);
        serviceLabel.Location = new Point(left, top);
        serviceLabel.Size = new Size(serviceWidth, 22);

        var graph = new OpenAiStatusGraph(health, trackColor, fillColor)
        {
            Location = new Point(left + serviceWidth + gap, top + 4),
            Size = new Size(graphWidth, 10),
            AccessibleName = $"{service} {OpenAiStatusClient.DisplayText(health)}"
        };

        var valueLeft = graph.Right + gap;
        var valueWidth = Math.Max(60, parent.Width - valueLeft - right);
        var value = monospace
            ? CreatePaperLabel(OpenAiStatusClient.DisplayText(health), 8.5f, FontStyle.Bold, valueColor, monospace: true)
            : CreateLabel(OpenAiStatusClient.DisplayText(health), 8.5f, FontStyle.Bold, valueColor);
        value.TextAlign = ContentAlignment.TopRight;
        value.Location = new Point(valueLeft, top);
        value.Size = new Size(valueWidth, 22);
        value.AutoEllipsis = true;
        parent.Controls.AddRange([serviceLabel, graph, value]);
    }

    private static Color ClassicPercentColor(int remainingPercent) =>
        remainingPercent <= 5 ? Color.FromArgb(244, 129, 129) : Color.FromArgb(119, 224, 177);

    private static Color MacAccentColor(int remainingPercent) => remainingPercent switch
    {
        <= 5 => Color.FromArgb(255, 76, 83),
        <= 20 => Color.FromArgb(255, 151, 42),
        _ => Color.FromArgb(48, 218, 105)
    };

    private static string FormatResetRemaining(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return "unavailable";
        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{Math.Max(1, remaining.Minutes)}m";
    }

    private static string FormatPaperReset(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return "Reset unavailable";
        var culture = CultureInfo.GetCultureInfo("en-US");
        return $"{FormatResetRemaining(resetAt)} · reset {resetAt.Value.ToString("MMM d, HH:mm", culture)}";
    }

    private static string FormatPlanName(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType)) return "Account";
        if (planType.Equals("prolite", StringComparison.OrdinalIgnoreCase)) return "Pro Lite";
        var words = planType.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(words.ToLowerInvariant());
    }

    private static string FormatUpdatedAge(DateTimeOffset updatedAt)
    {
        var age = DateTimeOffset.Now - updatedAt;
        if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return updatedAt.ToLocalTime().ToString("MMM d HH:mm");
    }

    private string OpenAiStatusHeading()
    {
        if (_openAiStatus.Codex == OpenAiServiceHealth.Loading &&
            _openAiStatus.ChatGpt == OpenAiServiceHealth.Loading)
        {
            return "OpenAI status · checking";
        }

        var state = _openAiStatus.IsLive ? "checked" : "unavailable";
        return $"OpenAI status · {state} {FormatUpdatedAge(_openAiStatus.CheckedAt)}";
    }

    private static string FormatBucketDisplayName(RateLimitBucket bucket, bool isTrayBucket)
    {
        var window = RateLimitNormalizer.FormatWindow(bucket.WindowDurationMins);
        if (UsageSnapshot.IsFrontierBucket(bucket))
        {
            return isTrayBucket ? $"Tray · Frontier · {window}" : $"{window} · Frontier";
        }

        var sourceName = bucket.Name.Trim();
        var durationSuffix = $" · {window}";
        if (sourceName.EndsWith(durationSuffix, StringComparison.OrdinalIgnoreCase))
        {
            sourceName = sourceName[..^durationSuffix.Length].TrimEnd();
        }
        if (string.IsNullOrWhiteSpace(sourceName) || sourceName.Equals(window, StringComparison.OrdinalIgnoreCase))
        {
            sourceName = bucket.LimitId ?? "Rate limit";
        }
        return isTrayBucket ? $"Tray · {window} · {sourceName}" : $"{window} · {sourceName}";
    }

    private static IEnumerable<RateLimitBucket> PopupBuckets(UsageSnapshot snapshot)
    {
        var trayKey = snapshot.TrayBucket?.Key;
        return snapshot.Buckets
            .OrderBy(bucket => bucket.Key == trayKey ? 0 : 1)
            .ThenBy(bucket => UsageSnapshot.IsFrontierBucket(bucket) ? 0 : 1);
    }

    private static string FormatCompactNumber(long? value)
    {
        if (value is null) return "Unavailable";
        var number = value.Value;
        return number switch
        {
            >= 1_000_000_000 => $"{number / 1_000_000_000d:0.#}B",
            >= 1_000_000 => $"{number / 1_000_000d:0.#}M",
            >= 1_000 => $"{number / 1_000d:0.#}K",
            _ => number.ToString(CultureInfo.CurrentCulture)
        };
    }

    private static string FormatEstimatedUsd(decimal estimatedUsd, long unpricedTokens)
    {
        if (estimatedUsd == 0 && unpricedTokens > 0) return "Unpriced";
        var culture = CultureInfo.GetCultureInfo("en-US");
        var amount = estimatedUsd is > 0 and < 0.01m
            ? "<$0.01"
            : $"${estimatedUsd.ToString("N2", culture)}";
        return unpricedTokens > 0 ? $"{amount}+" : amount;
    }

    private static string FormatApiEstimateNote(ApiEquivalentEstimate estimate, int displayedProjects)
    {
        var parts = new List<string> { $"API prices {estimate.PricingReferenceDate}" };
        if (displayedProjects > 0 && estimate.ProjectCount > displayedProjects)
        {
            parts.Add($"top {displayedProjects}/{estimate.ProjectCount}");
        }
        if (estimate.UsesProxyPricing)
        {
            parts.Add("Spark=5.3 proxy");
        }
        if (estimate.UnpricedTokens > 0)
        {
            parts.Add($"{FormatCompactNumber(estimate.UnpricedTokens)} unpriced");
        }
        return string.Join(" · ", parts);
    }

    private static string FormatNumber(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "unavailable";
}

internal sealed class OpenAiStatusGraph : Control
{
    private const int SegmentCount = 4;
    private readonly OpenAiServiceHealth _health;
    private readonly Color _trackColor;
    private readonly Color _fillColor;

    public OpenAiStatusGraph(OpenAiServiceHealth health, Color trackColor, Color fillColor)
    {
        _health = health;
        _trackColor = trackColor;
        _fillColor = fillColor;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        const int gap = 3;
        var segmentWidth = Math.Max(1, (ClientSize.Width - (gap * (SegmentCount - 1))) / SegmentCount);
        var filledSegments = _health switch
        {
            OpenAiServiceHealth.Operational => 4,
            OpenAiServiceHealth.UnderMaintenance or OpenAiServiceHealth.DegradedPerformance => 3,
            OpenAiServiceHealth.PartialOutage => 2,
            OpenAiServiceHealth.MajorOutage or OpenAiServiceHealth.Loading => 1,
            _ => 0
        };

        for (var index = 0; index < SegmentCount; index++)
        {
            var x = index * (segmentWidth + gap);
            var width = index == SegmentCount - 1 ? ClientSize.Width - x : segmentWidth;
            var bounds = new Rectangle(x, 0, Math.Max(1, width), Math.Max(1, ClientSize.Height));
            using var path = RoundedPanel.CreateRoundedRectangle(bounds, Math.Min(3, ClientSize.Height / 2));
            using var brush = new SolidBrush(index < filledSegments ? _fillColor : _trackColor);
            eventArgs.Graphics.FillPath(brush, path);
        }
    }
}

public sealed class RoundedPanel : Panel
{
    private int _radius = 16;

    [DefaultValue(16)]
    public int Radius
    {
        get => _radius;
        set
        {
            _radius = Math.Max(1, value);
            UpdateRegion();
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 1 || Height <= 1) return;
        using var path = CreateRoundedRectangle(ClientRectangle, Radius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    internal static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height);
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public sealed class UsageProgressBar : Control
{
    private int _value;

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 100);
            Invalidate();
        }
    }

    [DefaultValue(typeof(Color), "62, 67, 79")]
    public Color TrackColor { get; set; } = Color.FromArgb(62, 67, 79);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? FillColor { get; set; }

    [DefaultValue(false)]
    public bool Rounded { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var color = FillColor ?? (Value <= 5
            ? Color.FromArgb(244, 83, 83)
            : Value <= 20
                ? Color.FromArgb(245, 166, 35)
                : Color.FromArgb(53, 201, 143));

        if (!Rounded)
        {
            e.Graphics.Clear(TrackColor);
            using var brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, 0, 0, Width * Value / 100, Height);
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var trackPath = RoundedPanel.CreateRoundedRectangle(new Rectangle(0, 0, Width, Height), Height / 2);
        using var trackBrush = new SolidBrush(TrackColor);
        e.Graphics.FillPath(trackBrush, trackPath);

        var fillWidth = Width * Value / 100;
        if (fillWidth <= 0) return;
        using var fillPath = RoundedPanel.CreateRoundedRectangle(new Rectangle(0, 0, fillWidth, Height), Height / 2);
        using var fillBrush = new SolidBrush(color);
        e.Graphics.FillPath(fillBrush, fillPath);
    }
}
