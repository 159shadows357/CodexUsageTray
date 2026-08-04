using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageTray;

public sealed class CodexAppServerSession : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private readonly AppLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private JsonRpcClient? _client;
    private Task? _stderrTask;
    private string? _lastCompatibilityWarningSignature;
    private string? _lastCompatibilityStatus;

    public CodexAppServerSession(AppLogger logger) => _logger = logger;

    public event EventHandler? AccountDataChanged;
    public event EventHandler? ProcessExited;

    public bool IsRunning => _process is { HasExited: false } && _client is not null;
    public string? CodexVersion { get; private set; }
    public string ProtocolCompatibilityStatus { get; private set; } = "not checked";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var locator = new CodexExecutableLocator(_logger);
        var executable = await locator.FindAsync(cancellationToken);
        CodexVersion = await ReadVersionAsync(executable, cancellationToken);

        var process = new Process
        {
            StartInfo = CodexExecutableLocator.CreateStartInfo(executable, ["app-server", "--listen", "stdio://"]),
            EnableRaisingEvents = true
        };
        process.Exited += (_, _) => ProcessExited?.Invoke(this, EventArgs.Empty);
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex app-server process could not be started.");
        }

        _process = process;
        _stderrTask = ReadStderrAsync(process, _shutdown.Token);
        _client = new JsonRpcClient(process.StandardOutput, process.StandardInput, _logger);
        _client.NotificationReceived += OnNotificationReceived;

        var initialize = await _client.RequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "codex_usage_tray", title = "Codex Usage Tray", version = "0.6.0" },
                capabilities = new { experimentalApi = true }
            },
            RequestTimeout,
            cancellationToken);
        await _client.NotifyAsync("initialized", new { }, cancellationToken);
        _logger.Info($"Connected to {ReadString(initialize, "userAgent") ?? "Codex app-server"}.");
    }

    public async Task<UsageSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("Codex app-server is not connected.");
        var account = await client.RequestAsync("account/read", new { refreshToken = false }, RequestTimeout, cancellationToken);
        if (!account.TryGetProperty("account", out var accountValue) || accountValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new UsageSnapshot
            {
                UpdatedAt = DateTimeOffset.Now,
                IsLive = true,
                Status = ConnectionStatus.NotSignedIn,
                Error = "Codex is not signed in."
            };
        }

        var planType = accountValue.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String ? plan.GetString() : null;
        var limits = await client.RequestAsync("account/rateLimits/read", new { }, RequestTimeout, cancellationToken);
        JsonElement? usage = null;
        try
        {
            usage = await client.RequestAsync("account/usage/read", new { }, RequestTimeout, cancellationToken);
        }
        catch (JsonRpcException error) when (error.IsMethodUnavailable)
        {
            _logger.Info("Token activity endpoint is unavailable in this Codex version/account.");
        }

        var buckets = RateLimitNormalizer.Normalize(limits).ToList();
        var compatibility = ProtocolCompatibility.Inspect(limits, usage);
        ProtocolCompatibilityStatus = compatibility.Status;
        if (!string.Equals(_lastCompatibilityStatus, compatibility.Status, StringComparison.Ordinal))
        {
            _logger.Info($"Protocol compatibility: {compatibility.Status}.");
            _lastCompatibilityStatus = compatibility.Status;
        }
        var warningSignature = string.Join("|", compatibility.Warnings);
        if (compatibility.Warnings.Count > 0 &&
            !string.Equals(_lastCompatibilityWarningSignature, warningSignature, StringComparison.Ordinal))
        {
            foreach (var warning in compatibility.Warnings)
            {
                _logger.Error($"Protocol compatibility: {warning}");
            }
        }
        _lastCompatibilityWarningSignature = warningSignature;
        if (string.IsNullOrWhiteSpace(planType) && limits.TryGetProperty("rateLimits", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
        {
            planType = ReadString(fallback, "planType");
        }
        if (string.IsNullOrWhiteSpace(planType) && limits.TryGetProperty("rateLimitsByLimitId", out var rateLimitMap) && rateLimitMap.ValueKind == JsonValueKind.Object)
        {
            planType = rateLimitMap.EnumerateObject()
                .Select(item => item.Value.ValueKind == JsonValueKind.Object ? ReadString(item.Value, "planType") : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        var resetCredits = RateLimitNormalizer.NormalizeResetCredits(limits);
        var reached = buckets.Any(bucket => bucket.RemainingPercent == 0 || !string.IsNullOrWhiteSpace(bucket.RateLimitReachedType));
        return new UsageSnapshot
        {
            UpdatedAt = DateTimeOffset.Now,
            IsLive = true,
            PlanType = planType,
            Buckets = buckets,
            ResetCreditsAvailable = resetCredits.AvailableCount,
            ResetCredits = resetCredits.Credits?.ToList(),
            TokenActivity = RateLimitNormalizer.NormalizeUsage(usage, DateOnly.FromDateTime(DateTime.Now)),
            Status = reached ? ConnectionStatus.RateLimitReached : ConnectionStatus.Connected
        };
    }

    public async Task<ResetCreditConsumeOutcome> ConsumeResetCreditAsync(
        string creditId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var client = _client ?? throw new InvalidOperationException("Codex app-server is not connected.");
        var response = await client.RequestAsync(
            "account/rateLimitResetCredit/consume",
            new { creditId, idempotencyKey },
            RequestTimeout,
            cancellationToken);
        return ParseResetCreditOutcome(response);
    }

    internal static ResetCreditConsumeOutcome ParseResetCreditOutcome(JsonElement response)
    {
        var outcome = ReadString(response, "outcome");
        return outcome switch
        {
            "reset" => ResetCreditConsumeOutcome.Reset,
            "nothingToReset" => ResetCreditConsumeOutcome.NothingToReset,
            "noCredit" => ResetCreditConsumeOutcome.NoCredit,
            "alreadyRedeemed" => ResetCreditConsumeOutcome.AlreadyRedeemed,
            _ => ResetCreditConsumeOutcome.Unknown
        };
    }

    private void OnNotificationReceived(object? sender, string method)
    {
        if (method is "account/rateLimits/updated" or "account/updated" or "account/login/completed")
        {
            AccountDataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                _logger.Error($"app-server: {line}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        Process? process = null;
        var started = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            process = new Process { StartInfo = CodexExecutableLocator.CreateStartInfo(executable, ["--version"]) };
            started = process.Start();
            if (!started) return null;
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
        finally
        {
            if (process is not null)
            {
                if (started && !process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                process.Dispose();
            }
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_client is not null)
        {
            _client.NotificationReceived -= OnNotificationReceived;
            await _client.DisposeAsync();
        }

        if (_process is not null)
        {
            try { _process.StandardInput.Close(); } catch { }
            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }
            _process.Dispose();
        }

        if (_stderrTask is not null)
        {
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        _shutdown.Dispose();
    }
}
