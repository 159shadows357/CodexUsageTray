using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexUsageTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1)
    ];

    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly AppLogger _logger;
    private AppSettings _settings;
    private readonly NotifyIcon _trayIcon;
    private readonly UsagePopupForm _popup;
    private readonly UsageDetailsForm _details;
    private readonly ModernContextMenuStrip _contextMenu;
    private readonly Form? _menuSmokeHost;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NotificationTracker _notificationTracker;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly LocalUsageAnalyzer _localUsageAnalyzer;
    private readonly GitHubReleaseUpdater _updater;
    private readonly AdaptiveRefreshPolicy _adaptiveRefreshPolicy = new();
    private readonly Dictionary<TrayIconStyle, ToolStripMenuItem> _iconStyleItems = new();
    private readonly Dictionary<PopupStyle, ToolStripMenuItem> _popupStyleItems = new();
    private readonly Dictionary<UpdateMode, ToolStripMenuItem> _updateModeItems = new();
    private CodexAppServerSession? _session;
    private UsageSnapshot _snapshot;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.Now;
    private DateTimeOffset _nextUpdateCheckAt = DateTimeOffset.MaxValue;
    private int _retryIndex;
    private volatile bool _externalRefreshRequested;
    private volatile bool _openRequested;
    private volatile bool _detailsOpenRequested;
    private volatile bool _exitRequested;
    private volatile bool _iconRefreshRequested;
    private bool _exiting;
    private Icon? _currentIcon;
    private Task? _activeRefresh;
    private Task? _activeUpdate;
    private TrayIconVisualKey? _iconVisualKey;
    private TimeSpan? _lastRefreshDelay;
    private Version? _lastNotifiedUpdateVersion;
    private bool _updateFeedWarningShown;

    public TrayApplicationContext(
        SingleInstanceCoordinator singleInstance,
        AppPaths paths,
        JsonStore store,
        AppLogger logger,
        bool showOnStart = false,
        bool pinPopupForSmokeTest = false,
        bool showDetailsOnStart = false,
        bool showMenuForSmokeTest = false)
    {
        _singleInstance = singleInstance;
        _paths = paths;
        _store = store;
        _logger = logger;
        _settings = store.ReadSettings(paths.Settings);
        _notificationTracker = new NotificationTracker(paths.NotificationState);
        _localUsageAnalyzer = new LocalUsageAnalyzer(paths.LocalUsageIndex, store);
        _updater = new GitHubReleaseUpdater(paths.Updates);
        if (!File.Exists(paths.Settings))
        {
            store.Write(paths.Settings, _settings);
        }

        _snapshot = LoadInitialSnapshot();
        _popup = new UsagePopupForm(
            closeOnDeactivate: !pinPopupForSmokeTest,
            popupStyle: _settings.PopupStyle,
            showInTaskbarForSmokeTest: pinPopupForSmokeTest);
        _popup.RefreshRequested += (_, _) => RequestRefresh();
        _popup.UpdateSnapshot(_snapshot);
        _details = new UsageDetailsForm();
        _details.UpdateEstimate(_snapshot.ApiEquivalent);

        _contextMenu = new ModernContextMenuStrip();
        var menu = _contextMenu;
        menu.Items.Add("Open Usage", null, (_, _) => OpenUsage());
        menu.Items.Add("Usage details", null, (_, _) => OpenUsageDetails());
        menu.Items.Add("Refresh now", null, (_, _) => RequestRefresh());
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        menu.Items.Add(_startupItem);
        var iconStyleMenu = new ToolStripMenuItem("Tray icon style");
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            var styleItem = new ToolStripMenuItem(IconStyleText(style));
            styleItem.Click += (_, _) => SetTrayIconStyle(style);
            _iconStyleItems.Add(style, styleItem);
            iconStyleMenu.DropDownItems.Add(styleItem);
        }
        UpdateIconStyleChecks();
        menu.Items.Add(iconStyleMenu);
        var popupStyleMenu = new ToolStripMenuItem("Popup style");
        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            var styleItem = new ToolStripMenuItem(PopupStyleText(style));
            styleItem.Click += (_, _) => SetPopupStyle(style);
            _popupStyleItems.Add(style, styleItem);
            popupStyleMenu.DropDownItems.Add(styleItem);
        }
        UpdatePopupStyleChecks();
        menu.Items.Add(popupStyleMenu);
        var updatesMenu = new ToolStripMenuItem("Updates");
        updatesMenu.DropDownItems.Add("Check now", null, async (_, _) => await CheckForUpdatesAsync(interactive: true));
        updatesMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var mode in Enum.GetValues<UpdateMode>())
        {
            var modeItem = new ToolStripMenuItem(UpdateModeText(mode));
            modeItem.Click += (_, _) => SetUpdateMode(mode);
            _updateModeItems.Add(mode, modeItem);
            updatesMenu.DropDownItems.Add(modeItem);
        }
        UpdateModeChecks();
        menu.Items.Add(updatesMenu);
        menu.Items.Add("Open logs folder", null, (_, _) => OpenLogs());
        menu.Items.Add("Copy diagnostics", null, (_, _) => CopyDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Project on GitHub", null, (_, _) => OpenLink(AppLinks.Repository, "Open GitHub"));
        menu.Items.Add("Buy me a coffee...", null, (_, _) => OpenLink(AppLinks.Support, "Open support page"));
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) OpenUsage();
        };
        UpdateTray();
        if (showMenuForSmokeTest)
        {
            _menuSmokeHost = CreateMenuSmokeHost(menu);
            _menuSmokeHost.Show();
        }

        _singleInstance.OpenRequested += (_, _) =>
        {
            _logger.Info("Open request received from another instance.");
            _openRequested = true;
        };
        _singleInstance.ExitRequested += (_, _) => _exitRequested = true;
        _singleInstance.RefreshRequested += (_, _) => _externalRefreshRequested = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += async (_, _) => await TimerTickAsync();
        _timer.Start();
        _openRequested = showOnStart;
        _detailsOpenRequested = showDetailsOnStart;
        if (_settings.UpdateMode != UpdateMode.Off)
        {
            _nextUpdateCheckAt = DateTimeOffset.Now.AddSeconds(30);
        }
        _logger.Info(
            $"Codex Usage Tray started. Tray icon style: {_settings.TrayIconStyle}; " +
            $"popup style: {_settings.PopupStyle}; update mode: {_settings.UpdateMode}.");
    }

    private UsageSnapshot LoadInitialSnapshot()
    {
        var cached = _store.ReadSnapshot(_paths.Cache);
        if (cached is null)
        {
            return new UsageSnapshot { Status = ConnectionStatus.Loading, IsLive = false, UpdatedAt = DateTimeOffset.Now };
        }

        var age = DateTimeOffset.Now - cached.UpdatedAt;
        return cached with
        {
            IsLive = false,
            Status = age > TimeSpan.FromMinutes(_settings.StaleAfterMinutes) ? ConnectionStatus.DataStale : ConnectionStatus.Loading,
            Error = age > TimeSpan.FromMinutes(_settings.StaleAfterMinutes) ? "Cached usage is older than 10 minutes." : null
        };
    }

    private Form CreateMenuSmokeHost(ContextMenuStrip menu)
    {
        var host = new Form
        {
            Text = "Codex Usage Tray Menu Smoke",
            ClientSize = new Size(360, 520),
            BackColor = Color.FromArgb(18, 18, 18),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true,
            MaximizeBox = false,
            MinimizeBox = false
        };
        host.Shown += (_, _) => host.BeginInvoke(() => menu.Show(host, new Point(24, 24)));
        host.FormClosed += (_, _) => ExitThread();
        return host;
    }

    private async Task TimerTickAsync()
    {
        if (_exitRequested)
        {
            ExitThread();
            return;
        }
        if (_openRequested)
        {
            _openRequested = false;
            OpenUsage();
        }
        if (_detailsOpenRequested)
        {
            _detailsOpenRequested = false;
            OpenUsageDetails();
        }

        if (_externalRefreshRequested)
        {
            _externalRefreshRequested = false;
            _nextRefreshAt = DateTimeOffset.Now;
        }

        if (_iconRefreshRequested)
        {
            _iconRefreshRequested = false;
            _iconVisualKey = null;
            UpdateTray();
        }

        if (!_exiting && DateTimeOffset.Now >= _nextRefreshAt && (_activeRefresh is null || _activeRefresh.IsCompleted))
        {
            _activeRefresh = RefreshAsync();
            await _activeRefresh;
        }

        if (!_exiting &&
            _settings.UpdateMode != UpdateMode.Off &&
            DateTimeOffset.Now >= _nextUpdateCheckAt &&
            (_activeUpdate is null || _activeUpdate.IsCompleted))
        {
            _activeUpdate = CheckForUpdatesAsync(interactive: false);
        }
    }

    private void RequestRefresh()
    {
        _nextRefreshAt = DateTimeOffset.Now;
        _externalRefreshRequested = true;
    }

    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_session is null || !_session.IsRunning)
            {
                await DisposeSessionAsync();
                _session = new CodexAppServerSession(_logger);
                _session.AccountDataChanged += OnAccountDataChanged;
                _session.ProcessExited += OnProcessExited;
                await _session.StartAsync(_shutdown.Token);
            }

            var live = await _session.ReadSnapshotAsync(_shutdown.Token);
            var apiEquivalent = _snapshot.ApiEquivalent;
            try
            {
                apiEquivalent = await Task.Run(
                    () => _localUsageAnalyzer.Analyze(_shutdown.Token),
                    _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.Error($"Local token-cost analysis failed: {SecretSanitizer.Sanitize(error.Message)}");
            }
            live = live with { ApiEquivalent = apiEquivalent };
            _retryIndex = 0;
            var nextDelay = _adaptiveRefreshPolicy.RecordSuccess(
                _snapshot,
                live,
                _settings.RefreshIntervalMinutes,
                _popup.Visible || _details.Visible,
                SystemInformation.PowerStatus.PowerLineStatus);
            _nextRefreshAt = DateTimeOffset.Now.Add(nextDelay);
            if (_lastRefreshDelay != nextDelay)
            {
                _logger.Info($"Adaptive refresh interval: {nextDelay.TotalMinutes:0} minute(s).");
                _lastRefreshDelay = nextDelay;
            }
            var analysisStats = _localUsageAnalyzer.LastAnalysisStats;
            _logger.Info(
                $"Local usage analysis: files={analysisStats.SessionFiles}; read={analysisStats.FilesRead}; " +
                $"cached={analysisStats.FilesFromCache}; index={analysisStats.IndexEntries}.");
            try
            {
                _store.Write(_paths.Cache, live);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"Could not update usage cache: {error.Message}");
            }
            SetSnapshot(live);

            var alerts = _notificationTracker.Evaluate(live.Buckets, live.IsLive);
            if (alerts.Count > 0)
            {
                var alertText = string.Join(
                    Environment.NewLine,
                    alerts.GroupBy(item => item.Bucket.Key).Select(group =>
                        $"{group.First().Bucket.Name}: {group.First().Bucket.RemainingPercent}% left " +
                        $"(crossed {string.Join(", ", group.OrderByDescending(item => item.Threshold).Select(item => $"{item.Threshold}%"))})"));
                if (alertText.Length > 255) alertText = alertText[..252] + "...";
                _trayIcon.ShowBalloonTip(
                    5000,
                    "Codex usage",
                    alertText,
                    alerts.Any(item => item.Threshold == 0) ? ToolTipIcon.Error : ToolTipIcon.Warning);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (CodexCliNotFoundException error)
        {
            await DisposeSessionAsync();
            HandleRefreshFailure(ConnectionStatus.CliNotFound, error);
        }
        catch (Exception error)
        {
            await DisposeSessionAsync();
            HandleRefreshFailure(ConnectionStatus.AppServerUnavailable, error);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void HandleRefreshFailure(ConnectionStatus fallbackStatus, Exception error)
    {
        _logger.Error($"Refresh failed: {error.Message}");
        var age = DateTimeOffset.Now - _snapshot.UpdatedAt;
        var hasCachedData = _snapshot.Buckets.Count > 0;
        var status = hasCachedData && age > TimeSpan.FromMinutes(_settings.StaleAfterMinutes)
            ? ConnectionStatus.DataStale
            : fallbackStatus;
        SetSnapshot(_snapshot with
        {
            IsLive = false,
            Status = status,
            Error = SecretSanitizer.Sanitize(error.Message)
        });
        _nextRefreshAt = DateTimeOffset.Now.Add(RetryDelays[Math.Min(_retryIndex, RetryDelays.Length - 1)]);
        _retryIndex = Math.Min(_retryIndex + 1, RetryDelays.Length - 1);
    }

    private void SetSnapshot(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        _popup.UpdateSnapshot(snapshot);
        _details.UpdateEstimate(snapshot.ApiEquivalent);
        UpdateTray();
    }

    private void UpdateTray()
    {
        var constrained = _snapshot.MostConstrained;
        var nextVisualKey = new TrayIconVisualKey(_snapshot.Status, constrained?.Key, constrained?.RemainingPercent, _settings.TrayIconStyle);
        if (_iconVisualKey == nextVisualKey)
        {
            UpdateTooltip();
            return;
        }

        var nextIcon = TrayIconRenderer.Create(_snapshot.Status, constrained?.RemainingPercent, _settings.TrayIconStyle);
        try
        {
            _trayIcon.Icon = nextIcon;
        }
        catch
        {
            nextIcon.Dispose();
            throw;
        }

        var previous = _currentIcon;
        _currentIcon = nextIcon;
        _iconVisualKey = nextVisualKey;
        previous?.Dispose();

        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        _trayIcon.Text = TrayTooltipFormatter.Format(_snapshot);
    }

    private void OpenUsage()
    {
        if (_popup.InvokeRequired)
        {
            _popup.BeginInvoke(OpenUsage);
            return;
        }
        _logger.Info("Usage popup opened.");
        _popup.ShowNearTray();
        AccelerateRefreshForVisibleSurface();
    }

    private void OpenUsageDetails()
    {
        if (_details.InvokeRequired)
        {
            _details.BeginInvoke(OpenUsageDetails);
            return;
        }
        _logger.Info("Usage details opened.");
        _details.UpdateEstimate(_snapshot.ApiEquivalent);
        _details.ShowNearCursor();
        AccelerateRefreshForVisibleSurface();
    }

    private void AccelerateRefreshForVisibleSurface()
    {
        if (DateTimeOffset.Now - _snapshot.UpdatedAt > TimeSpan.FromSeconds(30))
        {
            RequestRefresh();
        }
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            StartupManager.SetEnabled(_startupItem.Checked);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            _startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
            _startupItem.Checked = StartupManager.IsEnabled();
            _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
            MessageBox.Show(SecretSanitizer.Sanitize(error.Message), "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetTrayIconStyle(TrayIconStyle style)
    {
        if (_settings.TrayIconStyle == style) return;
        var nextSettings = _settings with { TrayIconStyle = style };
        try
        {
            _store.Write(_paths.Settings, nextSettings);
            _settings = nextSettings;
            UpdateIconStyleChecks();
            _iconVisualKey = null;
            UpdateTray();
            _logger.Info($"Tray icon style changed to {style}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(SecretSanitizer.Sanitize(error.Message), "Tray icon style", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateIconStyleChecks()
    {
        foreach (var (style, item) in _iconStyleItems)
        {
            item.Checked = style == _settings.TrayIconStyle;
        }
    }

    private static string IconStyleText(TrayIconStyle style) => style switch
    {
        TrayIconStyle.Ring => "Ring",
        TrayIconStyle.SolidBar => "Solid bar",
        TrayIconStyle.SegmentedBar => "Segmented bar",
        TrayIconStyle.CodexPercent => "Number only",
        _ => style.ToString()
    };

    private void SetPopupStyle(PopupStyle style)
    {
        if (_settings.PopupStyle == style) return;
        var nextSettings = _settings with { PopupStyle = style };
        try
        {
            _store.Write(_paths.Settings, nextSettings);
            _settings = nextSettings;
            UpdatePopupStyleChecks();
            _popup.SetPopupStyle(style);
            _logger.Info($"Popup style changed to {style}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(SecretSanitizer.Sanitize(error.Message), "Popup style", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdatePopupStyleChecks()
    {
        foreach (var (style, item) in _popupStyleItems)
        {
            item.Checked = style == _settings.PopupStyle;
        }
    }

    private static string PopupStyleText(PopupStyle style) => style switch
    {
        PopupStyle.Classic => "Classic",
        PopupStyle.MacOS => "macOS style",
        PopupStyle.Paper => "Paper style",
        _ => style.ToString()
    };

    private void SetUpdateMode(UpdateMode mode)
    {
        if (_settings.UpdateMode == mode) return;
        if (mode == UpdateMode.Automatic)
        {
            var confirm = MessageBox.Show(
                "Automatic updates download and run the installer without another prompt.\n\n" +
                "Release SHA-256 is verified, but the executables are not Authenticode-signed. Enable automatic install?",
                "Automatic updates",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        if (!SaveUpdateMode(mode)) return;
        _nextUpdateCheckAt = mode == UpdateMode.Off ? DateTimeOffset.MaxValue : DateTimeOffset.Now;
        _logger.Info($"Update mode changed to {mode}.");
    }

    private bool SaveUpdateMode(UpdateMode mode)
    {
        var nextSettings = _settings with { UpdateMode = mode };
        try
        {
            _store.Write(_paths.Settings, nextSettings);
            _settings = nextSettings;
            UpdateModeChecks();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                SecretSanitizer.Sanitize(error.Message),
                "Updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private void UpdateModeChecks()
    {
        foreach (var (mode, item) in _updateModeItems)
        {
            item.Checked = mode == _settings.UpdateMode;
        }
    }

    private static string UpdateModeText(UpdateMode mode) => mode switch
    {
        UpdateMode.Off => "Off",
        UpdateMode.NotifyOnly => "Notify only",
        UpdateMode.Automatic => "Install automatically",
        _ => mode.ToString()
    };

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (!await _updateGate.WaitAsync(0)) return;
        try
        {
            var result = await _updater.CheckAsync(_shutdown.Token);
            if (result.Status == UpdateCheckStatus.FeedUnavailable)
            {
                if (interactive)
                {
                    MessageBox.Show(
                        result.Message,
                        "Check for updates",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else if (!_updateFeedWarningShown)
                {
                    _updateFeedWarningShown = true;
                    _logger.Info($"Background update check unavailable: {result.Message}");
                    _trayIcon.ShowBalloonTip(
                        8000,
                        "Update check unavailable",
                        result.Message,
                        ToolTipIcon.Warning);
                }
                return;
            }

            _updateFeedWarningShown = false;
            if (result.Status == UpdateCheckStatus.UpToDate)
            {
                _logger.Info($"Update check: current version {GitHubReleaseUpdater.CurrentVersion} is up to date.");
                if (interactive)
                {
                    MessageBox.Show(
                        result.Message,
                        "Check for updates",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return;
            }

            var release = result.Release ?? throw new InvalidDataException("Update result did not include release metadata.");
            _logger.Info($"Update available: {release.TagName}.");
            var install = _settings.UpdateMode == UpdateMode.Automatic;
            if (interactive)
            {
                install = MessageBox.Show(
                    $"{result.Message}\n\nDownload, verify, and install it now?",
                    "Codex Usage Tray update",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes;
            }
            else if (!install && _lastNotifiedUpdateVersion != release.Version)
            {
                _lastNotifiedUpdateVersion = release.Version;
                _trayIcon.ShowBalloonTip(
                    8000,
                    "Codex Usage Tray update",
                    $"{result.Message} Right-click the tray icon and select Updates > Check now.",
                    ToolTipIcon.Info);
            }

            if (install)
            {
                var prepared = await _updater.DownloadAndPrepareAsync(release, _shutdown.Token);
                GitHubReleaseUpdater.StartInstaller(prepared, StartupManager.IsEnabled());
                _logger.Info($"Verified updater started for {release.TagName}.");
                ExitThread();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            var message = SecretSanitizer.Sanitize(error.Message);
            _logger.Error($"Update check failed: {message}");
            if (interactive)
            {
                MessageBox.Show(message, "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (_settings.UpdateMode == UpdateMode.Automatic)
            {
                _trayIcon.ShowBalloonTip(
                    8000,
                    "Codex Usage Tray update failed",
                    "Automatic update failed. Use Updates > Check now for details.",
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            _nextUpdateCheckAt = _settings.UpdateMode == UpdateMode.Off
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.Now.AddHours(24);
            _updateGate.Release();
        }
    }

    private void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo(_paths.Logs) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(SecretSanitizer.Sanitize(error.Message), "Open logs", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void OpenLink(string url, string caption)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(
                SecretSanitizer.Sanitize(error.Message),
                caption,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void CopyDiagnostics()
    {
        var buckets = string.Join(Environment.NewLine, _snapshot.Buckets.Select(bucket =>
            $"- {bucket.Name}: {bucket.RemainingPercent}% left; window={bucket.WindowDurationMins?.ToString() ?? "unknown"}; reset={bucket.ResetsAtLocal?.ToString("O") ?? "unknown"}"));
        var diagnostics = $"""
            Codex Usage Tray {Application.ProductVersion}
            OS: {RuntimeInformation.OSDescription}
            Framework: {RuntimeInformation.FrameworkDescription}
            Status: {_snapshot.Status}
            Tray icon style: {_settings.TrayIconStyle}
            Popup style: {_settings.PopupStyle}
            Update mode: {_settings.UpdateMode}
            Update feed: public GitHub Releases
            Plan: {_snapshot.PlanType ?? "unknown"}
            Last updated: {_snapshot.UpdatedAt:O}
            Codex: {_session?.CodexVersion ?? "unknown"}
            Protocol: {_session?.ProtocolCompatibilityStatus ?? "not checked"}
            Adaptive refresh: {_lastRefreshDelay?.TotalMinutes.ToString("0") ?? "pending"} minute(s)
            Token activity: {(_snapshot.TokenActivity.IsAvailable ? "available" : "unavailable")}
            API-equivalent estimate: {(_snapshot.ApiEquivalent.IsAvailable ? $"${_snapshot.ApiEquivalent.EstimatedUsd:F2} est." : "unavailable")}
            Local usage index: files={_localUsageAnalyzer.LastAnalysisStats.SessionFiles}; read={_localUsageAnalyzer.LastAnalysisStats.FilesRead}; cached={_localUsageAnalyzer.LastAnalysisStats.FilesFromCache}
            Buckets:
            {buckets}
            Logs: {_paths.Logs}
            """;
        try { Clipboard.SetText(SecretSanitizer.Sanitize(diagnostics)); }
        catch (Exception error) { MessageBox.Show(SecretSanitizer.Sanitize(error.Message), "Copy diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void ShowAbout() => MessageBox.Show(
        $"Codex Usage Tray\nVersion {Application.ProductVersion}\n" +
        "by Pongsathon.T\n\n" +
        "Reads quota from the local Codex app-server and estimates API-equivalent cost from local Codex session token counters.\n" +
        $"Updates: public GitHub Releases from {GitHubReleaseUpdater.Repository}",
        "About Codex Usage Tray",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);

    private void OnAccountDataChanged(object? sender, EventArgs eventArgs) => _externalRefreshRequested = true;
    private void OnProcessExited(object? sender, EventArgs eventArgs) => _externalRefreshRequested = true;
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Resume) _externalRefreshRequested = true;
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs) => _iconRefreshRequested = true;

    private async Task DisposeSessionAsync()
    {
        if (_session is null) return;
        _session.AccountDataChanged -= OnAccountDataChanged;
        _session.ProcessExited -= OnProcessExited;
        await _session.DisposeAsync();
        _session = null;
    }

    protected override void ExitThreadCore() => _ = ExitAsync();

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _shutdown.Cancel();
        if (_activeRefresh is not null)
        {
            try { await _activeRefresh; } catch { }
        }
        if (_activeUpdate is not null)
        {
            try { await _activeUpdate; } catch { }
        }
        try { await DisposeSessionAsync(); } catch { }
        _trayIcon.Icon = null;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _contextMenu.Dispose();
        _currentIcon?.Dispose();
        _popup.Dispose();
        _details.Dispose();
        _menuSmokeHost?.Dispose();
        _refreshGate.Dispose();
        _updateGate.Dispose();
        _updater.Dispose();
        _shutdown.Dispose();
        _logger.Info("Codex Usage Tray stopped.");
        base.ExitThreadCore();
    }

    private readonly record struct TrayIconVisualKey(
        ConnectionStatus Status,
        string? BucketKey,
        int? RemainingPercent,
        TrayIconStyle Style);
}
