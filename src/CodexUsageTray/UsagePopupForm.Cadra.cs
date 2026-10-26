using System.Diagnostics;
using System.Globalization;

namespace CodexUsageTray;

public sealed partial class UsagePopupForm
{
    private ResetForecastSnapshot _resetForecast = new();
    private bool _resetForecastEnabled = true;
    private bool _compactLayout;
    private bool _denseLayout;
    private int _bucketPageIndex;
    private string _forecastFreshnessState = "Unavailable";

    private bool IsCadraStyle => _popupStyle is PopupStyle.CadraLight or PopupStyle.CadraDark;
    private int ScaleUi(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private int DashboardWidth => ScaleUi(IsCadraStyle ? 384 : _popupStyle switch
    {
        PopupStyle.MacOS => MacContentWidth,
        PopupStyle.Paper => PaperContentWidth,
        _ => ClassicContentWidth
    });

    private PopupPalette DashboardPalette => _popupStyle switch
    {
        PopupStyle.CadraLight => new(
            Color.FromArgb(239, 239, 236), Color.FromArgb(248, 248, 246), Color.FromArgb(17, 17, 17),
            Color.FromArgb(92, 92, 90), Color.FromArgb(37, 99, 168), Color.FromArgb(221, 225, 228),
            Color.FromArgb(216, 216, 211), Color.FromArgb(35, 122, 72), Color.FromArgb(143, 96, 0), Color.FromArgb(191, 54, 68)),
        PopupStyle.CadraDark => new(
            Color.FromArgb(11, 11, 11), Color.FromArgb(21, 21, 21), Color.FromArgb(242, 242, 240),
            Color.FromArgb(168, 168, 165), Color.FromArgb(106, 166, 232), Color.FromArgb(42, 43, 45),
            Color.FromArgb(42, 42, 42), Color.FromArgb(92, 197, 139), Color.FromArgb(226, 173, 69), Color.FromArgb(239, 123, 135)),
        PopupStyle.Paper => new(PaperBackground, PaperBackground, PaperInk, PaperMuted, PaperInk, PaperTrack,
            PaperTrack, Color.FromArgb(22, 101, 52), Color.FromArgb(143, 96, 0), Color.FromArgb(185, 28, 28)),
        PopupStyle.MacOS => new(MacBackground, MacCard, MacText, MacMuted, MacBlue,
            Color.FromArgb(70, 94, 115), Color.FromArgb(31, 105, 153), Color.FromArgb(64, 221, 116),
            Color.FromArgb(255, 172, 62), Color.FromArgb(255, 102, 102)),
        _ => new(ClassicBackground, Color.FromArgb(31, 34, 41), Color.FromArgb(238, 241, 246),
            Color.FromArgb(164, 174, 190), Color.FromArgb(88, 166, 255), Color.FromArgb(55, 59, 69),
            Color.FromArgb(55, 59, 69), Color.FromArgb(64, 221, 116), Color.FromArgb(255, 172, 62), Color.FromArgb(255, 102, 102))
    };

    private void ApplyCompactLayout()
    {
        var palette = DashboardPalette;
        BackColor = palette.Background;
        _content.BackColor = palette.Background;
        _content.AutoScroll = false;
        _content.Padding = Padding.Empty;
        _denseLayout = MaximumPopupHeight() * 96 / DeviceDpi < 700;
        Padding = new Padding(ScaleUi(_denseLayout ? 8 : 16));
        ClientSize = new Size(DashboardWidth + Padding.Horizontal, MaximumPopupHeight());
        UpdateWindowRegion();
    }

    private void RenderDashboardStyle(UsageSnapshot snapshot, bool condensed)
    {
        _compactLayout = condensed;
        _denseLayout = condensed && MaximumPopupHeight() * 96 / DeviceDpi < 700;
        AddDashboardHeader(snapshot);
        AddDashboardHealth();
        AddDashboardAccount(snapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            var error = DashboardLabel(SecretSanitizer.Sanitize(snapshot.Error), 8.5f, FontStyle.Regular, DashboardPalette.Bad);
            error.Size = new Size(DashboardWidth, ScaleUi(_denseLayout ? 24 : 34));
            error.Margin = new Padding(0, 0, 0, ScaleUi(_denseLayout ? 4 : 6));
            error.AccessibleName = "Connection error";
            _content.Controls.Add(error);
        }
        AddDashboardBuckets(snapshot, condensed);
        _content.Controls.SetChildIndex(_content.Controls[^1], 1);
    }

    private void AddDashboardHeader(UsageSnapshot snapshot)
    {
        var header = new Panel
        {
            Size = new Size(DashboardWidth, ScaleUi(_denseLayout ? 28 : _compactLayout ? 46 : 60)),
            Margin = new Padding(0, 0, 0, ScaleUi(_denseLayout ? 4 : 8)),
            AccessibleName = "Codex account summary"
        };
        var title = DashboardLabel("Codex Usage", _denseLayout ? 13 : IsCadraStyle ? 21 : 17, FontStyle.Bold, DashboardPalette.Text);
        title.SetBounds(0, 0, _denseLayout ? ScaleUi(142) : DashboardWidth, ScaleUi(_compactLayout ? 26 : 36));
        var subtitle = DashboardLabel($"{FormatPlanName(snapshot.PlanType)}  ·  {StatusText(snapshot.Status)}", 9, FontStyle.Regular,
            snapshot.Status == ConnectionStatus.Connected ? DashboardPalette.Muted : DashboardPalette.Warn);
        subtitle.SetBounds(_denseLayout ? ScaleUi(146) : 0, ScaleUi(_denseLayout ? 5 : _compactLayout ? 25 : 38),
            _denseLayout ? DashboardWidth - ScaleUi(146) : DashboardWidth, ScaleUi(20));
        if (_denseLayout) subtitle.TextAlign = ContentAlignment.TopRight;
        header.Controls.AddRange([title, subtitle]);
        _content.Controls.Add(header);
    }

    private void AddDashboardHealth()
    {
        var card = DashboardCard(
            $"OpenAI status, Codex {OpenAiStatusClient.DisplayText(_openAiStatus.Codex)}, ChatGPT {OpenAiStatusClient.DisplayText(_openAiStatus.ChatGpt)}",
            _denseLayout ? 60 : _compactLayout ? 64 : 82);
        var title = DashboardLabel(OpenAiStatusHeading(), 8.5f, FontStyle.Bold, DashboardPalette.Muted);
        title.SetBounds(ScaleUi(14), ScaleUi(_denseLayout ? 2 : 7), card.Width - ScaleUi(28), ScaleUi(18));
        card.Controls.Add(title);
        AddDashboardHealthRow(card, _denseLayout ? 20 : _compactLayout ? 23 : 29, "Codex", _openAiStatus.Codex);
        AddDashboardHealthRow(card, _denseLayout ? 40 : _compactLayout ? 43 : 54, "ChatGPT", _openAiStatus.ChatGpt);
        _content.Controls.Add(card);
    }

    private void AddDashboardHealthRow(Control card, int top, string service, OpenAiServiceHealth health)
    {
        var color = health switch
        {
            OpenAiServiceHealth.Operational => DashboardPalette.Good,
            OpenAiServiceHealth.Loading => DashboardPalette.Accent,
            OpenAiServiceHealth.DegradedPerformance or OpenAiServiceHealth.UnderMaintenance => DashboardPalette.Warn,
            OpenAiServiceHealth.PartialOutage or OpenAiServiceHealth.MajorOutage => DashboardPalette.Bad,
            _ => DashboardPalette.Muted
        };
        var caption = DashboardLabel(service, 9, FontStyle.Bold, DashboardPalette.Text);
        caption.SetBounds(ScaleUi(14), ScaleUi(top), ScaleUi(64), ScaleUi(20));
        var graph = new OpenAiStatusGraph(health, DashboardPalette.Track, color)
        {
            Bounds = new Rectangle(ScaleUi(84), ScaleUi(top + 4), ScaleUi(94), ScaleUi(9)),
            AccessibleName = $"{service} {OpenAiStatusClient.DisplayText(health)}"
        };
        var value = DashboardLabel(OpenAiStatusClient.DisplayText(health), 8.5f, FontStyle.Bold, color);
        value.TextAlign = ContentAlignment.TopRight;
        value.SetBounds(ScaleUi(186), ScaleUi(top), card.Width - ScaleUi(200), ScaleUi(20));
        card.Controls.AddRange([caption, graph, value]);
    }

    private void AddDashboardBuckets(UsageSnapshot snapshot, bool condensed)
    {
        if (snapshot.Buckets.Count == 0)
        {
            var empty = DashboardCard("No rate-limit buckets returned", 54);
            var label = DashboardLabel("No rate-limit buckets returned", 10, FontStyle.Regular, DashboardPalette.Muted);
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleCenter;
            empty.Controls.Add(label);
            _content.Controls.Add(empty);
            return;
        }

        var gap = _denseLayout ? 4 : 8;
        var normalHeight = condensed ? 76 : 100;
        var minimumHeight = _denseLayout ? 52 : 60;
        var forecastHeight = _resetForecastEnabled ? ScaleUi((_compactLayout ? 152 : 166) + gap) : 0;
        // Measure the rendered fixed sections, including error and credit-expiry rows, before allocating quota space.
        var quotaBudget = (MaximumPopupHeight() - DesiredContentHeight() - forecastHeight - ScaleUi(32 + gap)) * 96 / DeviceDpi;
        var columns = snapshot.Buckets.Count > 2 || snapshot.Buckets.Count == 2 && snapshot.Buckets.Count * (normalHeight + gap) > quotaBudget ? 2 : 1;
        var rows = (snapshot.Buckets.Count + columns - 1) / columns;
        var needsPages = rows * minimumHeight + (rows - 1) * gap > quotaBudget;
        var pagerHeight = needsPages ? 32 : 0;
        if (needsPages) rows = Math.Max(1, (quotaBudget - pagerHeight + gap) / (minimumHeight + gap));
        var pageSize = rows * columns;
        var pageCount = (snapshot.Buckets.Count + pageSize - 1) / pageSize;
        _bucketPageIndex = Math.Clamp(_bucketPageIndex, 0, pageCount - 1);
        var firstIndex = _bucketPageIndex * pageSize;
        var displayedBuckets = snapshot.Buckets.Skip(firstIndex).Take(pageSize).ToList();
        rows = (displayedBuckets.Count + columns - 1) / columns;
        var cardHeight = Math.Min(normalHeight, (quotaBudget - pagerHeight - (rows - 1) * gap) / rows);
        cardHeight = Math.Max(minimumHeight, cardHeight);
        var width = (DashboardWidth - ScaleUi(gap * (columns - 1))) / columns;
        var cardsHeight = rows * ScaleUi(cardHeight + gap) - ScaleUi(gap);
        var grid = new Panel
        {
            Size = new Size(DashboardWidth, cardsHeight + ScaleUi(pagerHeight)),
            Margin = new Padding(0, 0, 0, ScaleUi(gap)),
            AccessibleName = "Account quota buckets"
        };
        for (var index = 0; index < displayedBuckets.Count; index++)
        {
            var bucket = displayedBuckets[index];
            var isTrayBucket = snapshot.TrayBucket?.Key == bucket.Key;
            var displayName = FormatBucketDisplayName(bucket, isTrayBucket);
            var remainingPercent = isTrayBucket ? UsageSnapshot.EffectiveRemainingPercent(bucket) : bucket.RemainingPercent;
            var card = DashboardCard($"{(isTrayBucket ? "Tray indicator, " : string.Empty)}{displayName}, {remainingPercent}% left", cardHeight);
            card.Size = new Size(width, ScaleUi(cardHeight));
            card.Location = new Point(index % columns * (width + ScaleUi(gap)), index / columns * ScaleUi(cardHeight + gap));
            var small = cardHeight < 90;
            var dense = cardHeight < 76;
            var title = DashboardLabel(displayName, small ? 8.5f : 10, FontStyle.Bold, DashboardPalette.Muted);
            title.SetBounds(ScaleUi(12), ScaleUi(small ? 6 : 10), width - ScaleUi(dense ? 108 : 24), ScaleUi(20));
            var accent = remainingPercent <= 5 ? DashboardPalette.Bad
                : remainingPercent <= 20 ? DashboardPalette.Warn : DashboardPalette.Accent;
            var value = DashboardLabel($"{remainingPercent}% left", dense ? 10 : small ? 13 : 21, FontStyle.Bold, accent);
            value.SetBounds(dense ? width - ScaleUi(96) : ScaleUi(12), ScaleUi(dense ? 6 : small ? 24 : 33),
                dense ? ScaleUi(84) : width - ScaleUi(24), ScaleUi(dense ? 20 : small ? 25 : 32));
            var reset = DashboardLabel($"Reset {FormatResetRemaining(bucket.ResetsAtLocal)}", 8, FontStyle.Regular, DashboardPalette.Muted);
            reset.SetBounds(ScaleUi(12), ScaleUi(cardHeight - 26), width - ScaleUi(24), ScaleUi(18));
            var progress = new UsageProgressBar
            {
                Value = remainingPercent,
                Bounds = new Rectangle(ScaleUi(12), ScaleUi(cardHeight - 8), width - ScaleUi(24), ScaleUi(4)),
                Rounded = true,
                TrackColor = DashboardPalette.Track,
                FillColor = accent,
                AccessibleName = $"{bucket.Name} remaining quota"
            };
            card.Controls.AddRange([title, value, reset, progress]);
            grid.Controls.Add(card);
        }
        if (needsPages)
        {
            var caption = DashboardLabel($"Quotas {firstIndex + 1}–{firstIndex + displayedBuckets.Count} of {snapshot.Buckets.Count}", 8.5f,
                FontStyle.Regular, DashboardPalette.Muted);
            caption.SetBounds(0, cardsHeight + ScaleUi(8), DashboardWidth - ScaleUi(156), ScaleUi(22));
            var previous = DashboardButton("Previous");
            previous.AccessibleName = "Previous quota page";
            previous.Enabled = _bucketPageIndex > 0;
            previous.SetBounds(DashboardWidth - ScaleUi(154), cardsHeight + ScaleUi(4), ScaleUi(82), ScaleUi(28));
            previous.Click += (_, _) => ChangeQuotaPage(-1);
            var next = DashboardButton("Next");
            next.AccessibleName = "Next quota page";
            next.Enabled = _bucketPageIndex + 1 < pageCount;
            next.SetBounds(DashboardWidth - ScaleUi(68), cardsHeight + ScaleUi(4), ScaleUi(68), ScaleUi(28));
            next.Click += (_, _) => ChangeQuotaPage(1);
            grid.Controls.AddRange([caption, previous, next]);
        }
        _content.Controls.Add(grid);
    }

    private void ChangeQuotaPage(int direction)
    {
        _bucketPageIndex += direction;
        UpdateSnapshot(_snapshot);
    }

    private void AddDashboardAccount(UsageSnapshot snapshot)
    {
        var expiry = NextExpiringResetCredit(snapshot)?.ExpiresAtLocal;
        if (_denseLayout)
        {
            var compact = DashboardCard("Account and local history", expiry is null ? 24 : 44);
            AddDashboardValue(compact, 2, "Reset credits", snapshot.ResetCreditsAvailable?.ToString(CultureInfo.CurrentCulture) ?? "Unavailable");
            if (expiry is not null) AddDashboardValue(compact, 22, "Next credit expiry", FormatResetCreditExpiryCompact(expiry.Value));
            _content.Controls.Add(compact);
            return;
        }
        var card = DashboardCard("Account and local history", (_compactLayout ? 88 : 146) + (expiry is null ? 0 : 20));
        if (!_compactLayout)
        {
            var title = DashboardLabel("Account & local history", 9, FontStyle.Bold, DashboardPalette.Muted);
            title.SetBounds(ScaleUi(14), ScaleUi(8), card.Width - ScaleUi(28), ScaleUi(20));
            card.Controls.Add(title);
        }
        AddDashboardValue(card, _compactLayout ? 6 : 33, "Reset credits", snapshot.ResetCreditsAvailable?.ToString(CultureInfo.CurrentCulture) ?? "Unavailable");
        var top = _compactLayout ? 26 : 55;
        if (expiry is not null)
        {
            AddDashboardValue(card, top, "Next credit expiry", FormatResetCreditExpiryCompact(expiry.Value));
            top += 20;
        }
        var activity = snapshot.TokenActivity;
        AddDashboardValue(card, top, "Latest tokens", activity.IsAvailable ? FormatCompactNumber(activity.TodayOrLatestTokens) : "Unavailable");
        var rowSpacing = _compactLayout ? 20 : 22;
        AddDashboardValue(card, top + rowSpacing, "Lifetime / daily peak", activity.IsAvailable
            ? $"{FormatCompactNumber(activity.LifetimeTokens)} / {FormatCompactNumber(activity.PeakDailyTokens)}" : "Unavailable");
        AddDashboardValue(card, top + rowSpacing * 2, _compactLayout ? "API price estimate" : "API equivalent", snapshot.ApiEquivalent.IsAvailable
            ? FormatEstimatedUsd(snapshot.ApiEquivalent.EstimatedUsd, snapshot.ApiEquivalent.UnpricedTokens) : "Unavailable");
        if (!_compactLayout)
        {
            var note = DashboardLabel("Local token history · API price estimate", 8, FontStyle.Regular, DashboardPalette.Muted);
            note.SetBounds(ScaleUi(14), ScaleUi(top + 66), card.Width - ScaleUi(28), ScaleUi(18));
            card.Controls.Add(note);
        }
        _content.Controls.Add(card);
    }

    private void AddDashboardValue(Control card, int top, string caption, string value)
    {
        var label = DashboardLabel(caption, 9, FontStyle.Regular, DashboardPalette.Muted);
        label.SetBounds(ScaleUi(14), ScaleUi(top), ScaleUi(180), ScaleUi(20));
        var number = DashboardLabel(value, 9, FontStyle.Bold, DashboardPalette.Text);
        number.TextAlign = ContentAlignment.TopRight;
        number.SetBounds(ScaleUi(190), ScaleUi(top), card.Width - ScaleUi(204), ScaleUi(20));
        card.Controls.AddRange([label, number]);
    }

    private void AddResetForecast()
    {
        if (!_resetForecastEnabled) return;
        var palette = DashboardPalette;
        var card = DashboardCard("Reset forecast", _compactLayout ? 152 : 166);
        // Original themes keep their original control dimensions unless their content needs compact layout.
        if (!IsCadraStyle && !_compactLayout)
            card.Width = _popupStyle switch { PopupStyle.MacOS => MacContentWidth, PopupStyle.Paper => PaperContentWidth, _ => ClassicContentWidth };
        var inset = ScaleUi(14);
        var availableWidth = card.Width - inset * 2;
        var title = DashboardLabel("Reset forecast", 10, FontStyle.Bold, palette.Text);
        title.SetBounds(inset, ScaleUi(8), availableWidth - ScaleUi(94), ScaleUi(22));
        var state = _forecastFreshnessState;
        var activePromise = state == "Live" && _resetForecast.HasActivePromise(DateTimeOffset.UtcNow);
        var freshness = DashboardLabel(state == "Live" ? "API fresh" : $"API {state}", 8.5f, FontStyle.Bold, state == "Stale" ? palette.Warn : palette.Muted);
        freshness.TextAlign = ContentAlignment.TopRight;
        freshness.SetBounds(card.Width - ScaleUi(100), ScaleUi(10), ScaleUi(86), ScaleUi(20));
        var modelOdds = _resetForecast.ModelProbability24Hours is not null || _resetForecast.ModelProbability48Hours is not null
            ? $"Model: 24h {FormatProbability(_resetForecast.ModelProbability24Hours)} · 48h {FormatProbability(_resetForecast.ModelProbability48Hours)}"
            : $"API: 24h {FormatProbability(_resetForecast.Probability24Hours)} · 48h {FormatProbability(_resetForecast.Probability48Hours)}";
        var headline = activePromise
            ? _resetForecast.SignalPercent is { } score ? $"Reset promised · Watch {score}%" : "Reset promised · score unavailable"
            : modelOdds;
        var probabilities = DashboardLabel(headline, 11, FontStyle.Bold, palette.Accent);
        probabilities.SetBounds(inset, ScaleUi(_compactLayout ? 28 : 33), availableWidth, ScaleUi(24));
        var calculated = _resetForecast.UpdatedAt is null ? "Calculation time unavailable" : $"Calculated {FormatUpdatedAge(_resetForecast.UpdatedAt.Value)}";
        var confidence = DashboardLabel(activePromise
            ? $"{modelOdds} · {_resetForecast.Confidence ?? "unavailable"} confidence"
            : $"Confidence: {_resetForecast.Confidence ?? "unavailable"} · {calculated}", 8.5f, FontStyle.Regular, palette.Muted);
        confidence.SetBounds(inset, ScaleUi(_compactLayout ? 51 : 59), availableWidth, ScaleUi(20));
        var lastReset = DashboardLabel(
            $"Last reported reset: {(_resetForecast.LastResetAt is null ? "unavailable" : FormatResetCreditExpiry(_resetForecast.LastResetAt.Value.ToLocalTime()))}",
            8.5f, FontStyle.Regular, palette.Muted);
        lastReset.SetBounds(inset, ScaleUi(_compactLayout ? 71 : 79), availableWidth, ScaleUi(20));
        var signal = DashboardLabel(activePromise
            ? $"Posted {FormatResetCreditExpiry(_resetForecast.SignalAt!.Value.ToLocalTime())}"
            : FormatForecastSignal(), 8.5f, FontStyle.Regular, palette.Muted);
        signal.SetBounds(inset, ScaleUi(_compactLayout ? 91 : 99), availableWidth, ScaleUi(20));
        var qualifier = DashboardLabel(activePromise ? $"{calculated} · account reset not guaranteed"
            : "Community prediction · account reset not guaranteed", 8, FontStyle.Regular, palette.Muted);
        qualifier.SetBounds(inset, ScaleUi(_compactLayout ? 111 : 120), availableWidth, ScaleUi(20));
        var source = CreateProviderLink(palette.Accent, activePromise ? _resetForecast.SignalUrl : null);
        source.SetBounds(inset, ScaleUi(_compactLayout ? 131 : 141), availableWidth, ScaleUi(20));
        card.Controls.AddRange([title, freshness, probabilities, confidence, lastReset, signal, qualifier, source]);
        _content.Controls.Add(card);
    }

    private static string FormatProbability(int? value) => value is >= 0 and <= 100 ? $"{value}%" : "unavailable";

    private string ForecastFreshnessState(DateTimeOffset now) =>
        _resetForecast.Probability24Hours is null && _resetForecast.Probability48Hours is null ? "Unavailable"
        : _resetForecast.IsStale(now) ? "Stale" : _resetForecast.IsLive ? "Live" : "Cached";

    private string FormatForecastSignal() => (_resetForecast.AlertKind, _resetForecast.AlertState, _resetForecast.ConfirmationSource) switch
    {
        ("reset", _, "operator_observed") => "Signal: operator observed a reset",
        ("reset", "confirmed", _) => "Signal: community reset report confirmed",
        ("reset", "active", _) => "Signal: reset report active",
        ("reset", "expired", _) => "Signal: reset report expired",
        ("watch", "active", _) => "Signal: reset watch active",
        ("watch", "expired", _) => "Signal: reset watch expired",
        _ => "Signal: unavailable"
    };

    private static LinkLabel CreateProviderLink(Color accent, string? signalUrl = null)
    {
        var font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        var link = new LinkLabel
        {
            Text = "Data: codex-reset.com",
            Font = font,
            BackColor = Color.Transparent,
            LinkColor = accent,
            ActiveLinkColor = accent,
            VisitedLinkColor = accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            UseMnemonic = false,
            TabStop = true,
            AccessibleDescription = "Open the codex-reset.com community prediction source"
        };
        link.Links.Add(0, link.Text.Length, "https://codex-reset.com/");
        if (signalUrl is not null)
        {
            link.Text += " · View post";
            link.Links.Add(link.Text.Length - "View post".Length, "View post".Length, signalUrl);
        }
        link.LinkClicked += (_, args) =>
        {
            if (args.Link?.LinkData is not string url) return;
            try { using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                link.AccessibleDescription = "Unable to open the community prediction source";
            }
        };
        link.Disposed += (_, _) => font.Dispose();
        return link;
    }

    private void AddDashboardFooter(UsageSnapshot snapshot)
    {
        var footer = new Panel
        {
            Size = new Size(DashboardWidth, ScaleUi(32)),
            Margin = Padding.Empty,
            AccessibleName = "Usage actions"
        };
        var updated = DashboardLabel($"Quota updated {FormatUpdatedAge(snapshot.UpdatedAt)}", 8.5f, FontStyle.Regular, DashboardPalette.Muted);
        updated.SetBounds(0, ScaleUi(7), footer.Width - ScaleUi(142), ScaleUi(22));
        var refresh = DashboardButton("Refresh");
        refresh.SetBounds(footer.Width - ScaleUi(137), 0, ScaleUi(73), ScaleUi(30));
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var close = DashboardButton("Close");
        close.SetBounds(footer.Width - ScaleUi(60), 0, ScaleUi(60), ScaleUi(30));
        close.Click += (_, _) => Hide();
        footer.Controls.AddRange([updated, refresh, close]);
        _content.Controls.Add(footer);
    }

    private Button DashboardButton(string text)
    {
        var button = CreateButton(text, DashboardPalette.Accent);
        button.BackColor = DashboardPalette.Surface;
        button.FlatAppearance.MouseOverBackColor = DashboardPalette.Track;
        button.FlatAppearance.MouseDownBackColor = DashboardPalette.Line;
        return button;
    }

    private RoundedPanel DashboardCard(string accessibleName, int height) => new()
    {
        Width = DashboardWidth,
        Height = ScaleUi(height),
        Radius = ScaleUi(_popupStyle == PopupStyle.Paper ? 1 : 16),
        BackColor = DashboardPalette.Surface,
        Margin = new Padding(0, 0, 0, ScaleUi(_denseLayout ? 4 : 8)),
        AccessibleName = accessibleName
    };

    private Label DashboardLabel(string text, float size, FontStyle style, Color color)
    {
        var label = _popupStyle == PopupStyle.Paper
            ? CreatePaperLabel(text, size, style, color)
            : CreateLabel(text, size, style, color);
        label.AutoEllipsis = true;
        return label;
    }

    private sealed record PopupPalette(Color Background, Color Surface, Color Text, Color Muted, Color Accent,
        Color Track, Color Line, Color Good, Color Warn, Color Bad);
}
