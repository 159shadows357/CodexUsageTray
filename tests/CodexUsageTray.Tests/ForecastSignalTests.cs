using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class ForecastSignalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T05:57:00Z");
    private const string PostUrl = "https://x.com/thsottiaux/status/2114588174249071102";

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("forecast promise preserves source score separately from model odds", PromiseAndModel),
        ("live dated commitments retain their sourced watch score", DatedCommitment),
        ("forecast promise is retired by a newer reset or non-announced mode", SupersededPromise),
        ("stale failed and publication-expired data cannot assert an active promise", StalePromise),
        ("unknown and malformed optional signal fields preserve usable model odds", OptionalSignals),
        ("forecast promise links require a safe matching public source post", SignalProvenance),
        ("forecast signal fields survive cache serialization but remain inactive after restart", SignalCache),
        ("legacy forecast caches remain usable without a promise", LegacyCache)
    ];

    private static Task DatedCommitment()
    {
        var body = Body();
        body["official_signal"]!["signal_type"] = "dated_commitment";
        body["probabilities"]!["signal_percent"] = 93;
        body["official_signal"]!["score"]!["value"] = 93;
        body["signal_score"]!["value"] = 93;
        var snapshot = Parse(body);
        Require(Active(snapshot), "The live API's dated commitment was discarded.");
        Equal<int?>(93, snapshot.SignalPercent);
        Equal(PostUrl, snapshot.SignalUrl);
        ModelUnchanged(snapshot);
        Require(!Active(snapshot with { IsLive = false }), "A cached dated commitment became current.");
        Require(!Active(snapshot with { LastResetAt = Now }), "A superseded dated commitment became current.");
        return Task.CompletedTask;
    }

    private static Task PromiseAndModel()
    {
        foreach (var score in new[] { 83, 93 })
        {
            var body = Body();
            body["probabilities"]!["signal_percent"] = score;
            body["official_signal"]!["score"]!["value"] = score;
            body["signal_score"]!["value"] = score;
            var snapshot = Parse(body);
            Equal("announced", Read<string?>(snapshot, "ForecastMode"));
            Equal("promise", Read<string?>(snapshot, "SignalType"));
            Equal<int?>(score, Read<int?>(snapshot, "SignalPercent"));
            Equal<int?>(44, Read<int?>(snapshot, "ModelProbability24Hours"));
            Equal<int?>(69, Read<int?>(snapshot, "ModelProbability48Hours"));
            Equal<DateTimeOffset?>(DateTimeOffset.Parse("2026-10-26T05:21:22Z"), Read<DateTimeOffset?>(snapshot, "SignalAt"));
            Equal(PostUrl, Read<string?>(snapshot, "SignalUrl"));
            Require(Active(snapshot), "A current sourced promise was discarded.");
            ModelUnchanged(snapshot);
        }
        var fallback = Body();
        fallback["probabilities"]!["signal_percent"] = null;
        Equal<int?>(83, Read<int?>(Parse(fallback), "SignalPercent"));
        var divergent = Body();
        divergent["probabilities"]!["rounded_24h"] = 89;
        divergent["probabilities"]!["rounded_48h"] = 89;
        var separate = Parse(divergent);
        Equal<int?>(89, separate.Probability24Hours);
        Equal<int?>(89, separate.Probability48Hours);
        Equal<int?>(44, Read<int?>(separate, "ModelProbability24Hours"));
        Equal<int?>(69, Read<int?>(separate, "ModelProbability48Hours"));
        return Task.CompletedTask;
    }

    private static Task SupersededPromise()
    {
        foreach (var resetAt in new[] { "2026-10-26T05:21:22Z", "2026-10-26T05:30:00Z" })
        {
            var body = Body();
            body["last_reset_at"] = resetAt;
            Require(!Active(Parse(body)), "An already superseded promise remained active.");
        }
        foreach (var mode in new[] { "model", "future-unknown", "" })
        {
            var body = Body();
            body["mode"] = mode;
            Require(!Active(Parse(body)), "A non-announced mode asserted a live promise.");
        }
        foreach (var timestamp in new[] { "observed_at", "confirmed_at", "source_at" })
        {
            var body = Body();
            body["latest_alert"] = new JsonObject
            {
                ["kind"] = "reset",
                ["state"] = "confirmed",
                [timestamp] = "2026-10-26T05:30:00Z"
            };
            Require(!Active(Parse(body)), "A newer reset report failed to retire the previous promise.");
        }
        return Task.CompletedTask;
    }

    private static Task StalePromise()
    {
        var snapshot = Parse(Body());
        Require(!Active(snapshot with { IsLive = false }), "A cached promise was presented as current.");
        Require(!Active(snapshot with { Error = "Provider unavailable." }), "A failed refresh asserted a current promise.");
        Require(!Active(snapshot with { PublishedExpiresAt = Now }), "An expired publication asserted a current promise.");
        Require(!Active(snapshot with { CheckedAt = Now.AddMinutes(-16) }), "An old check asserted a current promise.");
        Require(!Active(snapshot with { UpdatedAt = Now.AddMinutes(-16) }), "An old model publication asserted a current promise.");
        Require(!Active(snapshot, Now.AddMinutes(16)), "An aged promise stayed current without fresh data.");
        var future = Body();
        future["official_signal"]!["at"] = "2026-10-26T05:58:00Z";
        Require(!Active(Parse(future)), "A future-dated source post was asserted as current.");
        return Task.CompletedTask;
    }

    private static Task OptionalSignals()
    {
        foreach (var malformed in new[] { "-1", "101", "83.5", "\"83\"", "true", "{}" })
        {
            var body = Body();
            body["probabilities"]!["signal_percent"] = JsonNode.Parse(malformed);
            body["official_signal"]!["score"] = JsonNode.Parse(malformed);
            body["signal_score"] = JsonNode.Parse(malformed);
            var snapshot = Parse(body);
            Equal<int?>(null, Read<int?>(snapshot, "SignalPercent"));
            ModelUnchanged(snapshot);
        }
        foreach (var type in new[] { "future-unknown", "<untrusted>", "" })
        {
            var body = Body();
            body["official_signal"]!["signal_type"] = type;
            var snapshot = Parse(body);
            Require(!Active(snapshot), "Unknown signal wording became an active promise.");
            ModelUnchanged(snapshot);
        }
        foreach (var timestamp in new[] { "not-a-date", "2026-10-26", "2026-10-26T06:03:00Z" })
        {
            var body = Body();
            body["official_signal"]!["at"] = timestamp;
            var snapshot = Parse(body);
            Require(!Active(snapshot), "A malformed or future promise date became active.");
            ModelUnchanged(snapshot);
        }
        var unknown = Body();
        unknown["official_signal"] = JsonNode.Parse("""{"score":99,"unexpected":"ignored"}""");
        Require(!Active(Parse(unknown)), "An undocumented signal shape became a promise.");
        var conflicting = Body();
        conflicting["official_signal"]!["score"]!["value"] = 93;
        Equal<int?>(null, Read<int?>(Parse(conflicting), "SignalPercent"));
        var topLevelConflict = Body();
        topLevelConflict["signal_score"]!["value"] = 93;
        Equal<int?>(null, Read<int?>(Parse(topLevelConflict), "SignalPercent"));
        var missingModel = Body();
        missingModel["probabilities"]!.AsObject().Remove("model_24h");
        missingModel["probabilities"]!.AsObject().Remove("model_48h");
        Equal<int?>(null, Read<int?>(Parse(missingModel), "ModelProbability24Hours"));
        Equal<int?>(null, Read<int?>(Parse(missingModel), "ModelProbability48Hours"));
        foreach (var malformed in new[] { "-1", "101", "44.5", "\"44\"", "true", "{}" })
        {
            var body = Body();
            body["probabilities"]!["model_24h"] = JsonNode.Parse(malformed);
            var snapshot = Parse(body);
            Equal<int?>(null, Read<int?>(snapshot, "ModelProbability24Hours"));
            ModelUnchanged(snapshot);
        }
        return Task.CompletedTask;
    }

    private static Task SignalProvenance()
    {
        foreach (var url in new[]
        {
            "http://x.com/thsottiaux/status/2114588174249071102",
            "https://x.com.evil.example/thsottiaux/status/2114588174249071102",
            "https://example.com/thsottiaux/status/2114588174249071102",
            "https://x.com@evil.example/thsottiaux/status/2114588174249071102",
            "https://user@x.com/thsottiaux/status/2114588174249071102",
            "https://x.com:444/thsottiaux/status/2114588174249071102",
            "https://x.com/thsottiaux/status/2114588174249071102?redirect=evil",
            "https://x.com/thsottiaux/status/2114588174249071102#untrusted",
            "https://x.com/thsottiaux/status/1111111111111111111",
            "https://x.com/thsottiaux",
            "javascript:alert(1)"
        })
        {
            var body = Body();
            body["official_signal"]!["url"] = url;
            var snapshot = Parse(body);
            Require(!Active(snapshot), "An unsafe or mismatched source asserted a promise.");
            Equal<string?>(null, Read<string?>(snapshot, "SignalUrl"));
            ModelUnchanged(snapshot);
        }
        var missing = Body();
        missing["official_signal"]!["tweet_id"] = null;
        Require(!Active(Parse(missing)), "A source without an event identity asserted a promise.");
        return Task.CompletedTask;
    }

    private static async Task SignalCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonStore();
            var path = Path.Combine(directory, "forecast.json");
            store.Write(path, Parse(Body()));
            var cached = store.ReadValue<ResetForecastSnapshot>(path) ?? throw new InvalidOperationException("Forecast cache was lost.");
            Equal("promise", Read<string?>(cached, "SignalType"));
            Equal<int?>(83, Read<int?>(cached, "SignalPercent"));
            Equal(PostUrl, Read<string?>(cached, "SignalUrl"));
            using var client = new ResetForecastClient(cached, new FrozenClock());
            var restored = await client.ReadAsync(CancellationToken.None);
            Require(!restored.IsLive && !Active(restored), "A restarted cache asserted a live promise before revalidation.");
            ModelUnchanged(restored);

            var malformed = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            malformed["modelProbability24Hours"] = 200;
            malformed["signalPercent"] = 200;
            malformed["signalType"] = "untrusted";
            malformed["signalAt"] = Now.AddMinutes(10).ToString("O");
            malformed["signalUrl"] = "https://evil.example/thsottiaux/status/2114588174249071102";
            store.Write(path, malformed);
            var badCache = store.ReadValue<ResetForecastSnapshot>(path) ?? throw new InvalidOperationException("Malformed optional fields destroyed the cache.");
            using var invalidClient = new ResetForecastClient(badCache, new FrozenClock());
            var sanitized = await invalidClient.ReadAsync(CancellationToken.None);
            ModelUnchanged(sanitized);
            Equal<int?>(null, Read<int?>(sanitized, "ModelProbability24Hours"));
            Equal<int?>(null, Read<int?>(sanitized, "SignalPercent"));
            Equal<string?>(null, Read<string?>(sanitized, "SignalType"));
            Equal<DateTimeOffset?>(null, Read<DateTimeOffset?>(sanitized, "SignalAt"));
            Equal<string?>(null, Read<string?>(sanitized, "SignalUrl"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Task LegacyCache()
    {
        using var client = new ResetForecastClient(new ResetForecastSnapshot
        {
            CheckedAt = Now,
            UpdatedAt = Now,
            Probability24Hours = 44,
            Probability48Hours = 69,
            IsLive = true
        }, new FrozenClock());
        var body = Body();
        body.Remove("mode");
        body.Remove("official_signal");
        body["probabilities"]!.AsObject().Remove("signal_percent");
        var legacy = Parse(body);
        Equal<string?>(null, Read<string?>(legacy, "SignalType"));
        Require(!Active(legacy), "Missing optional fields invented a promise.");
        ModelUnchanged(legacy);
        return Task.CompletedTask;
    }

    private static JsonObject Body() => JsonNode.Parse("""
        {"mode":"announced","updated_at":"2026-10-26T05:56:00Z",
         "probabilities":{"rounded_24h":44,"rounded_48h":69,"model_24h":44,"model_48h":69,"signal_percent":83},
         "last_reset_at":"2026-10-25T06:10:01Z","confidence":"low","signal_score":{"band":"promise","value":83},
         "official_signal":{"tweet_id":"2114588174249071102","signal_type":"promise",
         "at":"2026-10-26T05:21:22Z","url":"https://x.com/thsottiaux/status/2114588174249071102",
         "score":{"band":"promise","value":83}}}
        """)!.AsObject();

    private static ResetForecastSnapshot Parse(JsonNode body)
    {
        using var document = JsonDocument.Parse(body.ToJsonString());
        return ResetForecastClient.ParseForecast(document.RootElement, Now);
    }

    private static T Read<T>(ResetForecastSnapshot snapshot, string name)
    {
        var property = typeof(ResetForecastSnapshot).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing forecast signal property: {name}.");
        var value = property.GetValue(snapshot);
        return value is null ? default! : (T)value;
    }

    private static bool Active(ResetForecastSnapshot snapshot, DateTimeOffset? now = null)
    {
        var method = typeof(ResetForecastSnapshot).GetMethod("HasActivePromise", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing forecast promise activation policy.");
        return (bool)method.Invoke(snapshot, [now ?? Now])!;
    }

    private static void ModelUnchanged(ResetForecastSnapshot snapshot)
    {
        Equal<int?>(44, snapshot.Probability24Hours);
        Equal<int?>(69, snapshot.Probability48Hours);
        Require(snapshot.IsLive || snapshot.IsStale(Now), "Forecast freshness became indeterminate.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
