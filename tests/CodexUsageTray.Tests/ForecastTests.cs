using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class ForecastTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T07:02:00Z");

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("forecast percentages remain percentages and operator observation stays distinct", PublicForecast),
        ("forecast parser tolerates null optional fields and ignores undocumented signal", OptionalFields),
        ("forecast parser rejects malformed percentages and timestamps", MalformedForecast),
        ("forecast freshness uses publication expiry and rejects future data", Freshness),
        ("forecast requests identify the project without authentication", PublicRequest),
        ("forecast manual and concurrent refresh share one minute throttle", Throttle),
        ("forecast Retry-After delta delays every refresh and preserves last good data", RetryAfterDelta),
        ("forecast Retry-After date and increasing failure backoff are honored", RetryAfterDateAndBackoff),
        ("forecast malformed and oversized replies preserve stale last good data", FaultPreservesData),
        ("forecast streamed size limits and publication headers are validated", StreamAndHeaders),
        ("forecast unexpected handler failures complete the waiting request", UnexpectedFailureCompletes),
        ("forecast cancellation and disposal stop waiting and owned HTTP work", CancellationAndDisposal),
        ("forecast cache is stale and respects the previous request minute", CachedSnapshot),
        ("forecast bad cached values are discarded and retry deadlines survive restart", InvalidCacheAndRetry)
    ];

    private static Task PublicForecast()
    {
        var snapshot = Parse(Fixture());
        Equal<int?>(39, snapshot.Probability24Hours);
        Equal<int?>(63, snapshot.Probability48Hours);
        Equal("low", snapshot.Confidence);
        Equal("reset", snapshot.AlertKind);
        Equal("confirmed", snapshot.AlertState);
        Equal("operator_observed", snapshot.ConfirmationSource);
        Equal<DateTimeOffset?>(null, snapshot.ConfirmedAt);
        Equal<DateTimeOffset?>(DateTimeOffset.Parse("2026-10-23T17:40:51Z"), snapshot.LastResetAt);
        True(snapshot.IsLive && !snapshot.IsStale(Now));
        return Task.CompletedTask;
    }

    private static Task OptionalFields()
    {
        var snapshot = Parse("""
            {"updated_at":"2026-10-24T07:01:50Z","probabilities":{"rounded_24h":null,"rounded_48h":100},
             "last_reset_at":null,"confidence":null,"latest_alert":null,"official_signal":{"score":99,"unexpected":"ignored"}}
            """);
        Equal<int?>(null, snapshot.Probability24Hours);
        Equal<int?>(100, snapshot.Probability48Hours);
        Equal<string?>(null, snapshot.ConfirmationSource);
        Equal<DateTimeOffset?>(null, snapshot.LastResetAt);
        Equal<int?>(0, Parse(Fixture().Replace("\"rounded_24h\": 39", "\"rounded_24h\": 0")).Probability24Hours);
        return Task.CompletedTask;
    }

    private static Task MalformedForecast()
    {
        foreach (var value in new[] { "-1", "101", "39.5", "\"39\"", "true" })
        {
            Throws<InvalidDataException>(() => Parse(Fixture().Replace("\"rounded_24h\": 39", $"\"rounded_24h\": {value}")));
        }
        foreach (var value in new[] { "not-a-date", "2026-10-24", "2026-10-24T07:01:50", "9999-10-24T07:01:50Z", "2026-10-24 07:01:50Z" })
        {
            Throws<InvalidDataException>(() => Parse(Fixture().Replace("2026-10-24T07:01:50.445Z", value)));
        }
        Throws<InvalidDataException>(() => Parse("[]"));
        Throws<InvalidDataException>(() => Parse("{}"));
        Throws<InvalidDataException>(() => Parse(Fixture().Replace("2026-10-23T17:40:51.000Z", "invalid")));
        Equal<DateTimeOffset?>(DateTimeOffset.Parse("2026-10-24T07:01:50.445Z"),
            Parse(Fixture().Replace("2026-10-24T07:01:50.445Z", "2026-10-24T14:01:50.445+07:00")).UpdatedAt);
        return Task.CompletedTask;
    }

    private static Task Freshness()
    {
        var expired = Parse(Fixture(), Now.AddMinutes(-2), Now.AddSeconds(-1));
        True(expired.IsStale(Now));
        True(!expired.IsLive);
        var fresh = Parse(Fixture(), Now.AddMinutes(-1), Now.AddMinutes(2));
        True(!fresh.IsStale(Now));
        True(fresh.IsStale(Now.AddMinutes(2)));
        var old = Parse(Fixture().Replace("2026-10-24T07:01:50.445Z", "2026-10-24T06:01:50Z"));
        True(old.IsStale(Now));
        True(!old.IsLive);
        Throws<InvalidDataException>(() => Parse(Fixture(), Now.AddMinutes(6), Now.AddMinutes(9)));
        Throws<InvalidDataException>(() => Parse(Fixture(), Now, Now.AddMinutes(-1)));
        return Task.CompletedTask;
    }

    private static async Task PublicRequest()
    {
        using var handler = new ResponseHandler((request, _) =>
        {
            Equal(HttpMethod.Get, request.Method);
            Equal("https://codex-reset.com/api/forecast", request.RequestUri?.AbsoluteUri);
            True(request.Headers.Authorization is null);
            True(!request.Headers.Contains("Cookie"));
            True(request.Headers.UserAgent.ToString().Contains("CodexUsageTray/", StringComparison.Ordinal));
            True(request.Headers.UserAgent.ToString().Contains("github.com/159shadows357/CodexUsageTray", StringComparison.Ordinal));
            return Task.FromResult(Reply());
        });
        using var client = new ResetForecastClient(timeProvider: new ForecastClock(Now), handler: handler);
        var result = await client.ReadAsync(CancellationToken.None);
        True(result.IsLive, $"Forecast unexpectedly unavailable: {result.Error}; checked={result.CheckedAt:O}; updated={result.UpdatedAt:O}; handlerCalls={handler.Calls}.");
        Equal<int?>(39, result.Probability24Hours);
    }

    private static async Task Throttle()
    {
        var clock = new ForecastClock(Now);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ResponseHandler(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Reply();
        });
        using var client = new ResetForecastClient(timeProvider: clock, handler: handler);
        var first = client.ReadAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = Enumerable.Range(0, 12).Select(_ => client.ReadAsync(CancellationToken.None)).ToArray();
        release.SetResult();
        await first;
        foreach (var result in await Task.WhenAll(concurrent)) Equal<int?>(39, result.Probability24Hours);
        await client.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(59));
        await client.ReadAsync(CancellationToken.None);
        Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.ReadAsync(CancellationToken.None);
        Equal(2, handler.Calls);
    }

    private static async Task RetryAfterDelta()
    {
        var clock = new ForecastClock(Now);
        var responses = new Queue<HttpResponseMessage>([Reply(), Reply(HttpStatusCode.TooManyRequests), Reply()]);
        responses.ElementAt(1).Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        using var handler = new ResponseHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var client = new ResetForecastClient(timeProvider: clock, handler: handler);
        await client.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var stale = await client.ReadAsync(CancellationToken.None);
        Equal<int?>(39, stale.Probability24Hours);
        True(!stale.IsLive && stale.IsStale(clock.GetUtcNow()));
        True(!string.IsNullOrWhiteSpace(stale.Error));
        clock.Advance(TimeSpan.FromMinutes(4));
        await client.ReadAsync(CancellationToken.None);
        Equal(2, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        True((await client.ReadAsync(CancellationToken.None)).IsLive);
        Equal(3, handler.Calls);
    }

    private static async Task RetryAfterDateAndBackoff()
    {
        var clock = new ForecastClock(Now);
        using var handler = new ResponseHandler((_, _) =>
        {
            var response = Reply(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(3));
            return Task.FromResult(response);
        });
        using var client = new ResetForecastClient(timeProvider: clock, handler: handler);
        var missing = await client.ReadAsync(CancellationToken.None);
        Equal<int?>(null, missing.Probability24Hours);
        clock.Advance(TimeSpan.FromMinutes(2));
        await client.ReadAsync(CancellationToken.None);
        Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await client.ReadAsync(CancellationToken.None);
        Equal(2, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await client.ReadAsync(CancellationToken.None);
        Equal(2, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await client.ReadAsync(CancellationToken.None);
        Equal(3, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(3));
        await client.ReadAsync(CancellationToken.None);
        Equal(3, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await client.ReadAsync(CancellationToken.None);
        Equal(4, handler.Calls);
    }

    private static async Task FaultPreservesData()
    {
        foreach (var body in new[]
        {
            "{bad", "{}", new string('x', 262145),
            Fixture().Replace("2026-10-24T07:01:50.445Z", "2026-10-24T06:01:50Z").Replace("\"rounded_24h\": 39", "\"rounded_24h\": 67")
        })
        {
            var clock = new ForecastClock(Now);
            using var handler = new ResponseHandler((_, _) => Task.FromResult(Reply(body: body)));
            using var client = new ResetForecastClient(Parse(Fixture()), clock, handler);
            clock.Advance(TimeSpan.FromMinutes(1));
            var stale = await client.ReadAsync(CancellationToken.None);
            Equal<int?>(39, stale.Probability24Hours);
            Equal(Now, stale.CheckedAt);
            True(stale.IsStale(clock.GetUtcNow()));
            True(!string.IsNullOrWhiteSpace(stale.Error));
        }
        var timeoutClock = new ForecastClock(Now);
        using var timeoutHandler = new ResponseHandler((_, _) => throw new TaskCanceledException("timeout"));
        using var timeoutClient = new ResetForecastClient(timeProvider: timeoutClock, handler: timeoutHandler);
        True(!(await timeoutClient.ReadAsync(CancellationToken.None)).IsLive);
    }

    private static async Task CancellationAndDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new ResponseHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply();
        });
        var client = new ResetForecastClient(timeProvider: new ForecastClock(Now), handler: handler);
        await ThrowsAsync<OperationCanceledException>(() => client.ReadAsync(cancellation.Token));
        Equal(0, handler.Calls);
        var pending = client.ReadAsync(CancellationToken.None);
        client.Dispose();
        await ThrowsAsync<OperationCanceledException>(() => pending);
        True(handler.Disposed);
        await ThrowsAsync<ObjectDisposedException>(() => client.ReadAsync(CancellationToken.None));

        var clock = new ForecastClock(Now);
        using var raceHandler = new ResponseHandler((_, _) => Task.FromResult(Reply()));
        using var raceClient = new ResetForecastClient(timeProvider: clock, handler: raceHandler);
        clock.OnNextRead = raceClient.Dispose;
        await ThrowsAsync<OperationCanceledException>(() => raceClient.ReadAsync(CancellationToken.None));
    }

    private static async Task UnexpectedFailureCompletes()
    {
        using var handler = new ResponseHandler((_, _) => throw new InvalidOperationException("Unexpected handler failure."));
        using var client = new ResetForecastClient(timeProvider: new ForecastClock(Now), handler: handler);
        await ThrowsAsync<InvalidOperationException>(() => client.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Equal(1, handler.Calls);
    }

    private static async Task StreamAndHeaders()
    {
        var clock = new ForecastClock(Now);
        using var handler = new ResponseHandler((_, _) =>
        {
            var response = Reply();
            response.Content = new UnknownLengthContent(new string('x', 262145));
            return Task.FromResult(response);
        });
        using var client = new ResetForecastClient(Parse(Fixture()), clock, handler);
        clock.Advance(TimeSpan.FromMinutes(1));
        True((await client.ReadAsync(CancellationToken.None)).IsStale(clock.GetUtcNow()));
        Equal(1, handler.Calls);

        foreach (var header in new[] { "invalid", "2026-10-24T07:08:00Z", "2026-10-24T07:00:00Z" })
        {
            using var headerHandler = new ResponseHandler((_, _) =>
            {
                var response = Reply();
                response.Headers.Add("x-published-checked-at", header);
                response.Headers.Add("x-published-expires-at", "2026-10-24T07:01:00Z");
                response.Headers.Add("x-published-cache-status", "HIT");
                return Task.FromResult(response);
            });
            using var headerClient = new ResetForecastClient(timeProvider: new ForecastClock(Now), handler: headerHandler);
            var stale = await headerClient.ReadAsync(CancellationToken.None);
            True(stale.IsStale(Now));
            True(!stale.IsLive);
        }
    }

    private static async Task CachedSnapshot()
    {
        var clock = new ForecastClock(Now);
        using var handler = new ResponseHandler((_, _) => Task.FromResult(Reply()));
        using var client = new ResetForecastClient(Parse(Fixture()), clock, handler);
        var cached = await client.ReadAsync(CancellationToken.None);
        True(!cached.IsLive && cached.IsStale(Now));
        Equal<int?>(39, cached.Probability24Hours);
        Equal(0, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        True((await client.ReadAsync(CancellationToken.None)).IsLive);
        Equal(1, handler.Calls);
    }

    private static async Task InvalidCacheAndRetry()
    {
        using var handler = new ResponseHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.ServiceUnavailable)));
        using var client = new ResetForecastClient(Parse(Fixture()) with { Probability24Hours = 500 }, new ForecastClock(Now), handler);
        var snapshot = await client.ReadAsync(CancellationToken.None);
        Equal<int?>(null, snapshot.Probability24Hours);
        Equal(1, handler.Calls);

        var cached = Parse(Fixture()) with { RetryAt = Now.AddMinutes(5), LastAttemptAt = Now };
        var clock = new ForecastClock(Now);
        using var retryHandler = new ResponseHandler((_, _) => Task.FromResult(Reply()));
        using var retryClient = new ResetForecastClient(cached, clock, retryHandler);
        clock.Advance(TimeSpan.FromMinutes(4));
        await retryClient.ReadAsync(CancellationToken.None);
        Equal(0, retryHandler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        True((await retryClient.ReadAsync(CancellationToken.None)).IsLive);
        Equal(1, retryHandler.Calls);
    }

    private static ResetForecastSnapshot Parse(string body, DateTimeOffset? publishedCheckedAt = null, DateTimeOffset? publishedExpiresAt = null)
    {
        using var document = JsonDocument.Parse(body);
        return ResetForecastClient.ParseForecast(document.RootElement, Now, publishedCheckedAt, publishedExpiresAt);
    }

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reset-forecast.json"));

    private static HttpResponseMessage Reply(HttpStatusCode status = HttpStatusCode.OK, string? body = null) => new(status)
    {
        Content = new StringContent(body ?? Fixture(), Encoding.UTF8, "application/json")
    };

    private static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class ForecastClock(DateTimeOffset now) : TimeProvider
    {
        internal Action? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var callback = OnNextRead;
            OnNextRead = null;
            callback?.Invoke();
            return now;
        }
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
    }
}
