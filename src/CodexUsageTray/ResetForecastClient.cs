using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed record ResetForecastSnapshot
{
    public DateTimeOffset CheckedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? LastResetAt { get; init; }
    public DateTimeOffset? PublishedCheckedAt { get; init; }
    public DateTimeOffset? PublishedExpiresAt { get; init; }
    public DateTimeOffset? AlertSourceAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public int? Probability24Hours { get; init; }
    public int? Probability48Hours { get; init; }
    public int? ModelProbability24Hours { get; init; }
    public int? ModelProbability48Hours { get; init; }
    public string? ForecastMode { get; init; }
    public string? SignalType { get; init; }
    public int? SignalPercent { get; init; }
    public DateTimeOffset? SignalAt { get; init; }
    public string? SignalId { get; init; }
    public string? SignalUrl { get; init; }
    public string? Confidence { get; init; }
    public string? AlertKind { get; init; }
    public string? AlertState { get; init; }
    public string? ConfirmationSource { get; init; }
    public string? Error { get; init; }
    public bool IsLive { get; init; }
    public bool IsWebsiteForecast { get; init; }
    public bool WebsitePromiseActive { get; init; }

    public bool IsStale(DateTimeOffset now) =>
        !IsLive || Error is not null || UpdatedAt is null ||
        CheckedAt > now.AddMinutes(5) || UpdatedAt > now.AddMinutes(5) ||
        now - CheckedAt >= TimeSpan.FromMinutes(15) ||
        now - UpdatedAt.Value >= TimeSpan.FromMinutes(15) ||
        PublishedExpiresAt is { } expiresAt && now >= expiresAt;

    public bool HasActivePromise(DateTimeOffset now)
    {
        if (IsStale(now) || ForecastMode != "announced" || SignalType != "promise" ||
            SignalAt is not { } signalAt || signalAt < DateTimeOffset.UnixEpoch ||
            signalAt > now || signalAt > UpdatedAt!.Value.AddMinutes(5) ||
            ResetForecastClient.ValidateSignalUrl(SignalUrl, SignalId) is null)
        {
            return false;
        }

        var newestReset = LastResetAt;
        if (AlertKind == "reset" && (AlertState == "confirmed" || ConfirmationSource == "operator_observed"))
        {
            foreach (var resetAt in new[] { AlertSourceAt, ObservedAt, ConfirmedAt })
                if (resetAt is not null && (newestReset is null || resetAt > newestReset)) newestReset = resetAt;
        }
        return newestReset is null || signalAt > newestReset;
    }
}

internal sealed class ResetForecastClient : IDisposable
{
    internal const string ForecastUrl = "https://tiboreset.com/wp-content/uploads/tiboreset/v1/notify.json";
    private const int MaximumBodyBytes = 256 * 1024;
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private ResetForecastSnapshot _snapshot;
    private DateTimeOffset _nextAllowedRequestAt;
    private Task<ResetForecastSnapshot>? _inflight;
    private int _consecutiveFailures;
    private bool _disposed;

    public ResetForecastClient(ResetForecastSnapshot? cachedSnapshot = null, TimeProvider? timeProvider = null, HttpMessageHandler? handler = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _shutdownToken = _shutdown.Token;
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var now = _clock.GetUtcNow();
        if (cachedSnapshot is not null &&
            (cachedSnapshot.Probability24Hours is < 0 or > 100 || cachedSnapshot.Probability48Hours is < 0 or > 100 ||
             cachedSnapshot.CheckedAt > now.AddMinutes(5) || cachedSnapshot.UpdatedAt > now.AddMinutes(5) ||
             cachedSnapshot.LastResetAt > now.AddMinutes(5) || cachedSnapshot.LastAttemptAt > now.AddMinutes(5) ||
             cachedSnapshot.PublishedCheckedAt > now.AddMinutes(5) || cachedSnapshot.PublishedExpiresAt < cachedSnapshot.PublishedCheckedAt))
        {
            cachedSnapshot = null;
        }
        if (cachedSnapshot is not null)
        {
            cachedSnapshot = cachedSnapshot with
            {
                ModelProbability24Hours = ValidPercent(cachedSnapshot.ModelProbability24Hours),
                ModelProbability48Hours = ValidPercent(cachedSnapshot.ModelProbability48Hours),
                ForecastMode = cachedSnapshot.ForecastMode is "model" or "announced" ? cachedSnapshot.ForecastMode : null,
                SignalType = cachedSnapshot.SignalType == "promise" ? "promise" : null,
                SignalPercent = ValidPercent(cachedSnapshot.SignalPercent),
                SignalAt = cachedSnapshot.SignalAt is { } signalAt && signalAt >= DateTimeOffset.UnixEpoch && signalAt <= now.AddMinutes(5)
                    ? signalAt : null,
                SignalUrl = ValidateSignalUrl(cachedSnapshot.SignalUrl, cachedSnapshot.SignalId)
            };
        }
        _snapshot = cachedSnapshot is null
            ? new ResetForecastSnapshot { CheckedAt = now, Error = "Forecast not available." }
            : cachedSnapshot with { IsLive = false };
        var attemptedAt = cachedSnapshot?.LastAttemptAt ?? cachedSnapshot?.CheckedAt;
        _nextAllowedRequestAt = attemptedAt is { } attempt && attempt <= now.AddMinutes(5)
            ? attempt.Add(MinimumRequestInterval)
            : DateTimeOffset.MinValue;
        if (cachedSnapshot?.RetryAt is { } retryAt && retryAt > _nextAllowedRequestAt)
        {
            _nextAllowedRequestAt = retryAt;
        }
    }

    public DateTimeOffset NextAllowedRequestAt
    {
        get { lock (_sync) return _nextAllowedRequestAt; }
    }

    public Task<ResetForecastSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<ResetForecastSnapshot>? completion = null;
        Task<ResetForecastSnapshot> pending;
        DateTimeOffset startedAt;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            startedAt = _clock.GetUtcNow();
            if (_inflight is not null)
            {
                pending = _inflight;
            }
            else if (startedAt < _nextAllowedRequestAt)
            {
                return Task.FromResult(_snapshot);
            }
            else
            {
                _nextAllowedRequestAt = startedAt.Add(MinimumRequestInterval);
                completion = new TaskCompletionSource<ResetForecastSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = _inflight = completion.Task;
            }
        }

        if (completion is not null) _ = CompleteReadAsync(completion, startedAt);
        return pending.WaitAsync(cancellationToken);
    }

    private async Task CompleteReadAsync(TaskCompletionSource<ResetForecastSnapshot> completion, DateTimeOffset startedAt)
    {
        DateTimeOffset? retryAt = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, ForecastUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var version = typeof(ResetForecastClient).Assembly.GetName().Version?.ToString() ?? "1.0";
            request.Headers.UserAgent.ParseAdd($"CodexUsageTray/{version} (+https://github.com/159shadows357/CodexUsageTray)");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            retryAt = ReadRetryAfter(response, _clock.GetUtcNow());
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Forecast request returned HTTP {(int)response.StatusCode}.");
            }

            var publishedCheckedAt = ReadPublishedHeader(response, "x-published-checked-at", startedAt);
            var publishedExpiresAt = ReadPublishedHeader(response, "x-published-expires-at", startedAt, allowFuture: true);
            if (response.Content.Headers.ContentLength > MaximumBodyBytes)
            {
                throw new InvalidDataException("Forecast response is too large.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (body.Length + read > MaximumBodyBytes) throw new InvalidDataException("Forecast response is too large.");
                body.Write(buffer, 0, read);
            }

            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)), new JsonDocumentOptions { MaxDepth = 32 });
            var snapshot = ParseForecast(document.RootElement, _clock.GetUtcNow(), publishedCheckedAt, publishedExpiresAt) with
            {
                LastAttemptAt = startedAt
            };
            lock (_sync)
            {
                if (_snapshot.IsWebsiteForecast == snapshot.IsWebsiteForecast &&
                    _snapshot.UpdatedAt is { } previousUpdate && (!snapshot.IsLive || snapshot.UpdatedAt < previousUpdate ||
                    snapshot.IsWebsiteForecast && snapshot.PublishedCheckedAt < _snapshot.PublishedCheckedAt))
                {
                    snapshot = _snapshot with
                    {
                        IsLive = false,
                        Error = snapshot.Error ?? "Forecast provider data is older than the cached data.",
                        LastAttemptAt = startedAt
                    };
                }
                _snapshot = snapshot;
                _consecutiveFailures = 0;
                if (retryAt > _nextAllowedRequestAt) _nextAllowedRequestAt = retryAt.Value;
                _snapshot = _snapshot with { RetryAt = _nextAllowedRequestAt };
                _inflight = null;
                completion.TrySetResult(_snapshot);
            }
        }
        catch (Exception error) when (_shutdownToken.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException)
        {
            completion.TrySetCanceled(_shutdownToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            lock (_sync)
            {
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 5);
                var backoff = TimeSpan.FromMinutes(Math.Min(15, 1 << (_consecutiveFailures - 1)));
                var backoffUntil = _clock.GetUtcNow().Add(backoff);
                if (backoffUntil > _nextAllowedRequestAt) _nextAllowedRequestAt = backoffUntil;
                if (retryAt > _nextAllowedRequestAt) _nextAllowedRequestAt = retryAt.Value;
                _snapshot = _snapshot with
                {
                    IsLive = false,
                    Error = error switch
                    {
                        OperationCanceledException => "Forecast request timed out.",
                        JsonException => "Forecast response is invalid.",
                        InvalidDataException => "Forecast response is invalid or too large.",
                        _ => "Forecast service is unavailable."
                    },
                    LastAttemptAt = startedAt,
                    RetryAt = _nextAllowedRequestAt
                };
                _inflight = null;
                completion.TrySetResult(_snapshot);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            completion.TrySetException(error);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_inflight, completion.Task)) _inflight = null;
            }
        }
    }

    internal static ResetForecastSnapshot ParseForecast(JsonElement root, DateTimeOffset checkedAt,
        DateTimeOffset? publishedCheckedAt = null, DateTimeOffset? publishedExpiresAt = null)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("prob_24", out _) || root.TryGetProperty("prob_48", out _)))
        {
            return ParseWebsiteForecast(root, checkedAt, publishedExpiresAt);
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("probabilities", out var probabilities) ||
            probabilities.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Forecast probabilities are unavailable.");
        }
        var updatedAt = ReadTimestamp(root, "updated_at", checkedAt)
            ?? throw new InvalidDataException("Forecast update time is unavailable.");
        var probability24 = ReadPercent(probabilities, "rounded_24h");
        var probability48 = ReadPercent(probabilities, "rounded_48h");
        if (probability24 is null && probability48 is null) throw new InvalidDataException("Forecast probabilities are unavailable.");
        if (publishedCheckedAt > checkedAt.AddMinutes(5) || publishedExpiresAt < publishedCheckedAt)
        {
            throw new InvalidDataException("Forecast publication times are invalid.");
        }

        var alert = root.TryGetProperty("latest_alert", out var latestAlert) && latestAlert.ValueKind == JsonValueKind.Object
            ? latestAlert
            : default;
        var officialSignal = ObjectProperty(root, "official_signal");
        var signalId = ReadLabel(officialSignal, "tweet_id");
        var directScore = ReadOptionalPercent(probabilities, "signal_percent");
        var nestedScore = ReadOptionalPercent(ObjectProperty(officialSignal, "score"), "value");
        var topLevelScore = ReadOptionalPercent(ObjectProperty(root, "signal_score"), "value");
        var selectedScore = directScore ?? nestedScore ?? topLevelScore;
        var scoresAgree = (directScore is null || directScore == selectedScore) &&
            (nestedScore is null || nestedScore == selectedScore) && (topLevelScore is null || topLevelScore == selectedScore);
        var snapshot = new ResetForecastSnapshot
        {
            CheckedAt = checkedAt,
            UpdatedAt = updatedAt,
            LastResetAt = ReadTimestamp(root, "last_reset_at", checkedAt),
            PublishedCheckedAt = publishedCheckedAt,
            PublishedExpiresAt = publishedExpiresAt,
            Probability24Hours = probability24,
            Probability48Hours = probability48,
            ModelProbability24Hours = ReadOptionalPercent(probabilities, "model_24h"),
            ModelProbability48Hours = ReadOptionalPercent(probabilities, "model_48h"),
            ForecastMode = ReadLabel(root, "mode") is "model" or "announced" ? ReadLabel(root, "mode") : null,
            SignalType = ReadLabel(officialSignal, "signal_type") is "promise" or "dated_commitment" ? "promise" : null,
            SignalPercent = scoresAgree ? selectedScore : null,
            SignalAt = ReadOptionalTimestamp(officialSignal, "at", checkedAt),
            SignalId = signalId,
            SignalUrl = ValidateSignalUrl(ReadSignalUrl(officialSignal), signalId),
            Confidence = ReadLabel(root, "confidence"),
            AlertKind = ReadLabel(alert, "kind") is "reset" or "watch" ? ReadLabel(alert, "kind") : null,
            AlertState = ReadLabel(alert, "state") is "active" or "expired" or "confirmed" ? ReadLabel(alert, "state") : null,
            ConfirmationSource = ReadLabel(alert, "confirmation_source"),
            AlertSourceAt = ReadTimestamp(alert, "source_at", checkedAt),
            ObservedAt = ReadTimestamp(alert, "observed_at", checkedAt),
            ConfirmedAt = ReadTimestamp(alert, "confirmed_at", checkedAt),
            IsLive = true
        };
        return snapshot.IsStale(checkedAt)
            ? snapshot with { IsLive = false, Error = "Forecast provider data is stale." }
            : snapshot;
    }

    private static ResetForecastSnapshot ParseWebsiteForecast(JsonElement root, DateTimeOffset checkedAt,
        DateTimeOffset? publishedExpiresAt)
    {
        var probability24 = ReadPercent(root, "prob_24") ?? throw new InvalidDataException("Website 24-hour odds are unavailable.");
        var probability48 = ReadPercent(root, "prob_48") ?? throw new InvalidDataException("Website 48-hour odds are unavailable.");
        var updatedAt = ReadTimestamp(root, "forecast_updated_at", checkedAt)
            ?? throw new InvalidDataException("Website forecast calculation time is unavailable.");
        var publishedAt = ReadTimestamp(root, "updated_at", checkedAt)
            ?? throw new InvalidDataException("Website publication time is unavailable.");
        if (publishedAt < updatedAt || publishedExpiresAt < publishedAt)
        {
            throw new InvalidDataException("Website publication times are invalid.");
        }
        var signalUrl = ReadSignalUrl(root, "promise_url");
        var signalId = signalUrl?.Split('/').LastOrDefault();
        signalUrl = ValidateSignalUrl(signalUrl, signalId);
        var delayed = !root.TryGetProperty("delayed", out var delayedValue) || delayedValue.ValueKind != JsonValueKind.False;
        var promise = root.TryGetProperty("promise_active", out var promiseValue) && promiseValue.ValueKind == JsonValueKind.True;
        var snapshot = new ResetForecastSnapshot
        {
            IsWebsiteForecast = true,
            WebsitePromiseActive = promise && signalUrl is not null,
            CheckedAt = checkedAt,
            UpdatedAt = updatedAt,
            PublishedCheckedAt = publishedAt,
            PublishedExpiresAt = publishedExpiresAt,
            LastResetAt = ReadTimestamp(root, "last_reset_at", checkedAt),
            Probability24Hours = probability24,
            Probability48Hours = probability48,
            ForecastMode = promise ? "announced" : "model",
            SignalId = signalUrl is null ? null : signalId,
            SignalUrl = signalUrl,
            IsLive = !delayed,
            Error = delayed ? "Tibo website feed is delayed or its freshness is unavailable." : null
        };
        return snapshot.IsStale(checkedAt)
            ? snapshot with { IsLive = false, Error = snapshot.Error ?? "Tibo website forecast is stale." }
            : snapshot;
    }

    private static JsonElement ObjectProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static int? ValidPercent(int? value) => value is >= 0 and <= 100 ? value : null;

    private static int? ReadOptionalPercent(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var percent)
            ? ValidPercent(percent) : null;

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement element, string name, DateTimeOffset now)
    {
        try { return ReadTimestamp(element, name, now); }
        catch (InvalidDataException) { return null; }
    }

    private static string? ReadSignalUrl(JsonElement element, string name = "url") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 256 } url
            ? url : null;

    internal static string? ValidateSignalUrl(string? value, string? signalId)
    {
        if (signalId is not { Length: > 0 and <= 20 } || !signalId.All(char.IsAsciiDigit) ||
            value is not { Length: > 0 and <= 256 } || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("x.com" or "twitter.com"))
        {
            return null;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && segments[0].Length is > 0 and <= 15 &&
            segments[0].All(character => char.IsAsciiLetterOrDigit(character) || character == '_') &&
            segments[1] == "status" && segments[2] == signalId ? uri.AbsoluteUri : null;
    }

    private static int? ReadPercent(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var percent) || percent is < 0 or > 100)
        {
            throw new InvalidDataException("Forecast percentage is invalid.");
        }
        return percent;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name, DateTimeOffset now)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Forecast timestamp is invalid.");
        return ParseTimestamp(value.GetString(), now);
    }

    private static DateTimeOffset ParseTimestamp(string? value, DateTimeOffset now, bool allowFuture = false)
    {
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
        ];
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
            timestamp < DateTimeOffset.UnixEpoch || !allowFuture && timestamp > now.AddMinutes(5))
        {
            throw new InvalidDataException("Forecast timestamp is invalid.");
        }
        return timestamp;
    }

    private static DateTimeOffset? ReadPublishedHeader(HttpResponseMessage response, string name, DateTimeOffset now, bool allowFuture = false)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var entries = values.Take(2).ToArray();
        if (entries.Length != 1) throw new InvalidDataException("Forecast publication header is invalid.");
        return ParseTimestamp(entries[0], now, allowFuture);
    }

    private static string? ReadLabel(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 64 } text && !text.Any(char.IsControl)
            ? text
            : null;

    private static DateTimeOffset? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date) return date;
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delta);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _client.Dispose();
            _shutdown.Dispose();
        }
    }
}
