using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace CodexUsageTray;

public sealed class UsagePopupForm : Form
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
    private DateTimeOffset _shownAt;
    private PopupStyle _popupStyle;

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

        _content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            AccessibleName = "Usage details"
        };
        Controls.Add(_content);
        ApplyVisualStyle();

        Deactivate += (_, _) =>
        {
            if (_closeOnDeactivate && DateTimeOffset.Now - _shownAt > TimeSpan.FromMilliseconds(500)) Hide();
        };
        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape) Hide();
        };
    }

    public event EventHandler? RefreshRequested;

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

    public void UpdateSnapshot(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        SuspendLayout();
        _content.SuspendLayout();
        ClearContent();
        ApplyVisualStyle();

        if (_popupStyle == PopupStyle.MacOS)
        {
            RenderMacStyle(snapshot);
        }
        else if (_popupStyle == PopupStyle.Paper)
        {
            RenderPaperStyle(snapshot);
        }
        else
        {
            RenderClassicStyle(snapshot);
        }

        _content.ResumeLayout(true);
        ResumeLayout(true);
        if (_popupStyle == PopupStyle.MacOS)
        {
            FitMacWindowToContent();
        }
        else if (_popupStyle == PopupStyle.Paper)
        {
            FitPaperWindowToContent();
        }
        else
        {
            FitClassicWindowToContent();
        }
    }

    public void ShowNearTray()
    {
        UpdateSnapshot(_snapshot);
        PositionNearTray();
        _shownAt = DateTimeOffset.Now;
        Show();
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_popupStyle != PopupStyle.MacOS) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPanel.CreateRoundedRectangle(
            new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 3), Math.Max(1, ClientSize.Height - 3)),
            22);
        using var pen = new Pen(Color.FromArgb(31, 105, 153), 1.5f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    private void ApplyVisualStyle()
    {
        if (_popupStyle == PopupStyle.MacOS)
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

    private void FitMacWindowToContent()
    {
        var contentHeight = _content.Controls.Cast<Control>()
            .Sum(control => control.Height + control.Margin.Vertical);
        var screen = Visible ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        var maxHeight = Math.Max(360, screen.WorkingArea.Height - 16);
        var desiredHeight = contentHeight + Padding.Vertical + 2;
        var targetHeight = Math.Min(desiredHeight, maxHeight);
        _content.AutoScroll = false;
        ClientSize = new Size(430, targetHeight);
        _content.PerformLayout();
    }

    private void FitPaperWindowToContent()
    {
        var contentHeight = _content.Controls.Cast<Control>()
            .Sum(control => control.Height + control.Margin.Vertical);
        var screen = Visible ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        var maxHeight = Math.Max(360, screen.WorkingArea.Height - 16);
        var desiredHeight = contentHeight + _content.Padding.Vertical + Padding.Vertical + 2;
        var targetHeight = Math.Min(desiredHeight, maxHeight);
        _content.AutoScroll = false;
        ClientSize = new Size(430, targetHeight);
        _content.PerformLayout();
    }

    private void FitClassicWindowToContent()
    {
        var contentHeight = _content.Controls.Cast<Control>()
            .Sum(control => control.Height + control.Margin.Vertical);
        var screen = Visible ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        var maxHeight = Math.Max(360, screen.WorkingArea.Height - 16);
        var desiredHeight = contentHeight + Padding.Vertical + 2;
        var targetHeight = Math.Min(desiredHeight, maxHeight);
        _content.AutoScroll = false;
        ClientSize = new Size(400, targetHeight);
        _content.PerformLayout();
    }

    private void UpdateWindowRegion()
    {
        Region? next = null;
        if (_popupStyle == PopupStyle.MacOS && ClientSize.Width > 1 && ClientSize.Height > 1)
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
            foreach (var bucket in snapshot.Buckets)
            {
                AddClassicBucket(bucket);
            }
        }

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

        if (snapshot.TokenActivity.IsAvailable)
        {
            var activity = snapshot.TokenActivity;
            var latestLabel = activity.TodayOrLatestTokens is null
                ? "Latest tokens: unavailable"
                : $"Tokens ({activity.TodayOrLatestDate}): {FormatNumber(activity.TodayOrLatestTokens)}";
            AddClassicLabel(latestLabel, 10, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
            AddClassicLabel($"Lifetime tokens: {FormatNumber(activity.LifetimeTokens)}", 10, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
            AddClassicLabel($"Peak daily tokens: {FormatNumber(activity.PeakDailyTokens)}", 10, FontStyle.Regular, Color.FromArgb(213, 218, 226), 26);
        }
        else
        {
            AddClassicLabel("Token activity unavailable", 10, FontStyle.Regular, Color.FromArgb(164, 174, 190), 28);
        }

        AddClassicSeparator();
        AddClassicApiEquivalent(snapshot.ApiEquivalent);
        AddClassicSeparator();
        AddClassicLabel($"Last updated: {snapshot.UpdatedAt.ToLocalTime():MMM d, yyyy HH:mm:ss}", 9, FontStyle.Regular, Color.FromArgb(164, 174, 190), 24);
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            AddClassicLabel(SecretSanitizer.Sanitize(snapshot.Error), 9, FontStyle.Regular, Color.FromArgb(244, 129, 129), 48);
        }
    }

    private void AddClassicBucket(RateLimitBucket bucket)
    {
        var panel = new Panel
        {
            Width = ClassicContentWidth,
            Height = 92,
            Margin = new Padding(0, 4, 0, 4),
            BackColor = Color.FromArgb(31, 34, 41),
            AccessibleName = $"{bucket.Name}, {bucket.RemainingPercent}% left"
        };
        var title = CreateLabel(bucket.Name, 10, FontStyle.Bold, Color.White);
        title.Location = new Point(12, 9);
        title.Size = new Size(215, 24);

        var percent = CreateLabel($"{bucket.RemainingPercent}% left", 10, FontStyle.Bold, ClassicPercentColor(bucket.RemainingPercent));
        percent.TextAlign = ContentAlignment.TopRight;
        percent.Location = new Point(225, 9);
        percent.Size = new Size(112, 24);

        var progress = new UsageProgressBar
        {
            Value = bucket.RemainingPercent,
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
        foreach (var project in estimate.Projects.Take(3))
        {
            AddClassicLabel(
                $"{project.Name}: {FormatCompactNumber(project.TotalTokens)} · {FormatEstimatedUsd(project.EstimatedUsd, project.UnpricedTokens)}",
                9,
                FontStyle.Regular,
                Color.FromArgb(190, 197, 209),
                25);
        }
        AddClassicLabel(FormatApiEstimateNote(estimate, Math.Min(3, estimate.Projects.Count)), 8.5f, FontStyle.Regular, Color.FromArgb(164, 174, 190), 25);
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
            foreach (var bucket in snapshot.Buckets)
            {
                AddPaperBucket(bucket);
            }
        }

        AddPaperDivider();
        AddPaperCredits(snapshot);
        AddPaperDivider();
        AddPaperTokenActivity(snapshot.TokenActivity);
        AddPaperDivider();
        AddPaperApiEquivalent(snapshot.ApiEquivalent);

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

        var updated = CreatePaperLabel($"Updated {FormatUpdatedAge(snapshot.UpdatedAt)}", 10, FontStyle.Regular, PaperMuted);
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

    private void AddPaperBucket(RateLimitBucket bucket)
    {
        var panel = new Panel
        {
            Width = PaperContentWidth,
            Height = 88,
            BackColor = PaperBackground,
            Margin = new Padding(0, 0, 0, 18),
            AccessibleName = $"{bucket.Name}, {bucket.RemainingPercent}% left"
        };
        var title = CreatePaperLabel(bucket.Name, 11.5f, FontStyle.Bold, PaperInk, monospace: true);
        title.Location = new Point(0, 0);
        title.Size = new Size(218, 28);
        title.AutoEllipsis = true;

        var percent = CreatePaperLabel($"{bucket.RemainingPercent}% left", 11.5f, FontStyle.Bold, PaperInk, monospace: true);
        percent.TextAlign = ContentAlignment.TopRight;
        percent.Location = new Point(218, 0);
        percent.Size = new Size(152, 28);

        var progress = new UsageProgressBar
        {
            Value = bucket.RemainingPercent,
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
        var displayedProjects = estimate.Projects.Take(3).ToList();
        var rowCount = estimate.IsAvailable ? 2 + displayedProjects.Count : 1;
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
            var top = 86;
            foreach (var project in displayedProjects)
            {
                AddPaperValueRow(
                    panel,
                    top,
                    $"{project.Name} · {FormatCompactNumber(project.TotalTokens)}",
                    FormatEstimatedUsd(project.EstimatedUsd, project.UnpricedTokens));
                top += 25;
            }
        }
        else
        {
            AddPaperValueRow(panel, 36, "Status", "Unavailable");
        }

        var note = CreatePaperLabel(
            estimate.IsAvailable ? FormatApiEstimateNote(estimate, displayedProjects.Count) : "No local Codex token history found",
            8.5f,
            FontStyle.Regular,
            PaperMuted);
        note.Location = new Point(0, noteTop);
        note.Size = new Size(PaperContentWidth, 25);
        note.AutoEllipsis = true;
        panel.Controls.Add(note);
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

        if (snapshot.Buckets.Count == 0)
        {
            AddMacEmptyCard("No rate-limit buckets returned");
        }
        else
        {
            foreach (var bucket in snapshot.Buckets)
            {
                AddMacBucket(bucket);
            }
        }

        AddMacAccountCard(snapshot);
        AddMacTokenCard(snapshot.TokenActivity);
        AddMacApiEquivalentCard(snapshot.ApiEquivalent);

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

    private void AddMacBucket(RateLimitBucket bucket)
    {
        var accent = MacAccentColor(bucket.RemainingPercent);
        var card = new RoundedPanel
        {
            Width = MacContentWidth,
            Height = 116,
            Radius = 18,
            BackColor = MacCard,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"{bucket.Name}, {bucket.RemainingPercent}% left"
        };
        var title = CreateLabel(bucket.Name, 11, FontStyle.Bold, MacText);
        title.Location = new Point(18, 15);
        title.Size = new Size(232, 26);
        title.AutoEllipsis = true;

        var percent = CreateLabel($"{bucket.RemainingPercent}%", 12, FontStyle.Bold, accent);
        percent.TextAlign = ContentAlignment.TopRight;
        percent.Location = new Point(252, 13);
        percent.Size = new Size(102, 28);

        var progress = new UsageProgressBar
        {
            Value = bucket.RemainingPercent,
            Location = new Point(18, 49),
            Size = new Size(336, 10),
            TrackColor = Color.FromArgb(70, 94, 115),
            FillColor = accent,
            Rounded = true
        };

        var resetsIn = CreateLabel($"Resets in: {FormatResetRemaining(bucket.ResetsAtLocal)}", 9, FontStyle.Bold, MacMuted);
        resetsIn.Location = new Point(18, 73);
        resetsIn.Size = new Size(180, 26);
        var resetAt = CreateLabel(
            bucket.ResetsAtLocal is null ? "Reset unavailable" : bucket.ResetsAtLocal.Value.ToString("MMM d HH:mm"),
            9,
            FontStyle.Bold,
            accent);
        resetAt.TextAlign = ContentAlignment.TopRight;
        resetAt.Location = new Point(198, 73);
        resetAt.Size = new Size(156, 26);

        card.Controls.AddRange([title, percent, progress, resetsIn, resetAt]);
        _content.Controls.Add(card);
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
        var displayedProjects = estimate.Projects.Take(3).ToList();
        var rowCount = estimate.IsAvailable ? 2 + displayedProjects.Count : 1;
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
            var top = 102;
            foreach (var project in displayedProjects)
            {
                AddMacValueRow(
                    card,
                    top,
                    $"{project.Name} · {FormatCompactNumber(project.TotalTokens)}",
                    FormatEstimatedUsd(project.EstimatedUsd, project.UnpricedTokens));
                top += 28;
            }
        }
        else
        {
            AddMacValueRow(card, 46, "Status", "Unavailable");
        }

        var note = CreateLabel(
            estimate.IsAvailable ? FormatApiEstimateNote(estimate, displayedProjects.Count) : "No local Codex token history found",
            8.5f,
            FontStyle.Regular,
            MacMuted);
        note.Location = new Point(18, noteTop);
        note.Size = new Size(336, 24);
        note.AutoEllipsis = true;
        card.Controls.Add(note);
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
        var updated = CreateLabel($"Updated {FormatUpdatedAge(snapshot.UpdatedAt)}", 9, FontStyle.Bold, MacMuted);
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

    private static Button CreateButton(string text, Color color)
    {
        var font = new Font("Segoe UI", 9, FontStyle.Regular);
        var button = new Button
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.FromArgb(12, 48, 82),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(24, 67, 104);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 78, 119);
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
        if (estimate.ProjectCount > displayedProjects)
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
