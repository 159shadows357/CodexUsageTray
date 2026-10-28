using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class TiboWebsiteForecastTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T03:32:00Z");
    private const string WebsiteUrl = "https://tiboreset.com/wp-content/uploads/tiboreset/v1/notify.json";

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("website feed preserves the exact visible Tibo percentages", WebsiteValues),
        ("forecast requests use the same JSON feed as the Tibo UI", WebsiteRequest),
        ("website feed rejects malformed or missing odds and publication times", InvalidWebsiteValues),
        ("website promises require safe source links and fresh publications", WebsiteFreshness),
        ("switching from the API cache accepts a fresh website publication", WebsiteReplacesApiCache),
        ("website publications cannot roll back odds or reactivate an old promise", WebsitePublicationRollback)
    ];

    private static Task WebsiteValues()
    {
        var snapshot = Parse(Body());
        Require(snapshot.IsWebsiteForecast && snapshot.IsLive, "Website forecast was not recognized.");
        Require(snapshot.Probability24Hours == 99 && snapshot.Probability48Hours == 99, "UI odds were replaced with API odds.");
        Require(snapshot.ModelProbability24Hours is null && snapshot.SignalPercent is null, "Website odds invented an API model or Watch score.");
        Require(snapshot.WebsitePromiseActive && snapshot.SignalUrl?.EndsWith("/2115273253711409208", StringComparison.Ordinal) == true,
            "Website promise lost its safe public source.");
        Require(snapshot.UpdatedAt == DateTimeOffset.Parse("2026-10-28T03:29:48.682Z"), "Calculation time was replaced by fetch time.");
        var split = Body();
        split["prob_24"] = 41;
        split["prob_48"] = 66;
        var changed = Parse(split);
        Require(changed.Probability24Hours == 41 && changed.Probability48Hours == 66, "Website odds were hardcoded to 99.");
        return Task.CompletedTask;
    }

    private static async Task WebsiteRequest()
    {
        using var handler = new WebsiteHandler(request =>
        {
            Require(request.RequestUri?.AbsoluteUri == WebsiteUrl, "The client still reads the different API forecast.");
            Require(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Website request sent account credentials.");
            Require(request.Headers.UserAgent.ToString().Contains("CodexUsageTray/", StringComparison.Ordinal), "Website request has no project User-Agent.");
        });
        using var client = new ResetForecastClient(timeProvider: new WebsiteClock(), handler: handler);
        var snapshot = await client.ReadAsync(CancellationToken.None);
        Require(snapshot.IsLive && snapshot.IsWebsiteForecast && snapshot.Probability24Hours == 99, "Website feed did not reach the live client.");
    }

    private static Task InvalidWebsiteValues()
    {
        foreach (var property in new[] { "prob_24", "prob_48" })
        {
            foreach (var invalid in new[] { "null", "-1", "101", "99.5", "\"99\"", "true" })
            {
                var body = Body();
                body[property] = JsonNode.Parse(invalid);
                Reject(body);
            }
        }
        foreach (var property in new[] { "forecast_updated_at", "updated_at" })
        {
            var body = Body();
            body[property] = "invalid";
            Reject(body);
        }
        return Task.CompletedTask;
    }

    private static Task WebsiteFreshness()
    {
        var delayed = Body();
        delayed["delayed"] = true;
        Require(Parse(delayed).IsStale(Now), "A delayed website feed was marked fresh.");
        var stale = Body();
        stale["forecast_updated_at"] = Now.AddMinutes(-16).ToString("O");
        Require(Parse(stale).IsStale(Now), "An old website calculation was marked fresh.");
        var unsafePost = Body();
        unsafePost["promise_url"] = "https://x.com.evil.example/thsottiaux/status/2115273253711409208";
        var snapshot = Parse(unsafePost);
        Require(!snapshot.WebsitePromiseActive && snapshot.SignalUrl is null && snapshot.Probability24Hours == 99,
            "An unsafe post asserted a promise or destroyed otherwise valid website odds.");
        return Task.CompletedTask;
    }

    private static async Task WebsiteReplacesApiCache()
    {
        using var handler = new WebsiteHandler(_ => { });
        using var client = new ResetForecastClient(new ResetForecastSnapshot
        {
            CheckedAt = Now.AddMinutes(-2),
            LastAttemptAt = Now.AddMinutes(-2),
            UpdatedAt = Now,
            Probability24Hours = 46,
            Probability48Hours = 71
        }, new WebsiteClock(), handler);
        var snapshot = await client.ReadAsync(CancellationToken.None);
        Require(snapshot.IsWebsiteForecast && snapshot.IsLive && snapshot.Probability24Hours == 99, "A newer API calculation blocked the selected website source.");
    }

    private static async Task WebsitePublicationRollback()
    {
        var current = Body();
        current["prob_24"] = 55;
        current["prob_48"] = 75;
        current["promise_active"] = false;
        var older = Body();
        older["updated_at"] = "2026-10-28T03:30:58Z";
        var clock = new WebsiteClock();
        using var handler = new WebsiteHandler(_ => { }, new Queue<JsonObject>([current, older]));
        using var client = new ResetForecastClient(timeProvider: clock, handler: handler);
        var first = await client.ReadAsync(CancellationToken.None);
        Require(first.IsLive && first.Probability24Hours == 55, "The current website fixture was not live.");
        clock.Advance(TimeSpan.FromMinutes(1));
        var rejected = await client.ReadAsync(CancellationToken.None);
        Require(rejected.Probability24Hours == 55 && !rejected.WebsitePromiseActive && !rejected.IsLive,
            "An older website publication rolled back odds or reactivated its promise.");
    }

    private static JsonObject Body() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tibo-website-forecast.json")))!.AsObject();

    private static ResetForecastSnapshot Parse(JsonNode body)
    {
        using var document = JsonDocument.Parse(body.ToJsonString());
        return ResetForecastClient.ParseForecast(document.RootElement, Now);
    }

    private static void Reject(JsonNode body)
    {
        try { Parse(body); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid website forecast was accepted.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class WebsiteClock : TimeProvider
    {
        private DateTimeOffset _now = Now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class WebsiteHandler(Action<HttpRequestMessage> inspect, Queue<JsonObject>? bodies = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent((bodies?.Dequeue() ?? Body()).ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }
}
