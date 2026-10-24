using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class CoreUpgradeTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases
    {
        get
        {
            yield return ("current OpenAI components remain operational after Sora and Atlas retirement", CurrentComponentsOperational);
            yield return ("dots and Space incidents affect ChatGPT health", CurrentComponentIncidents);
            yield return ("missing current components remain unavailable", MissingCurrentComponent);
            yield return ("retired components and unrelated API outages do not affect current ChatGPT health", UnrelatedComponentsIgnored);
            yield return ("component identity takes precedence over duplicate Login names", ComponentIdentityWins);

            // Standard USD rates, independently calculated from the official pricing table on 2026-10-24.
            foreach (var (model, shortCost, longCost) in new (string, decimal, decimal)[]
            {
                ("gpt-6.1-sol", 0.32m, 0.99m),
                ("gpt-6-sol", 0.33m, 1.01m),
                ("gpt-6-astra", 1.65m, 5.05m),
                ("gpt-6-luna", 0.0165m, 0.0505m),
                ("gpt-5.6-sol", 0.66m, 2.02m),
                ("gpt-5.6-terra", 0.35m, 1.04m),
                ("gpt-5.6-luna", 0.035m, 0.104m)
            })
            {
                yield return ($"{model} uses current Standard input cache write and output rates", () => ModelCost(model, false, shortCost));
                yield return ($"{model} applies the long-context rates to every token category", () => ModelCost(model, true, longCost));
            }

            yield return ("long-context pricing starts strictly above 272000 input tokens", LongContextBoundary);
            yield return ("unknown model suffixes do not inherit another model price", UnknownModelVariants);
            yield return ("legacy usage index reprices unchanged sessions", LegacyIndexReprices);
            yield return ("obsolete calculation version reprices unchanged sessions", ObsoleteCalculationReprices);
        }
    }

    private static Task CurrentComponentsOperational()
    {
        var snapshot = ParseStatus(CurrentComponents());
        Equal(OpenAiServiceHealth.Operational, snapshot.Codex);
        Equal(OpenAiServiceHealth.Operational, snapshot.ChatGpt);
        return Task.CompletedTask;
    }

    private static Task CurrentComponentIncidents()
    {
        foreach (var id in new[] { "01M3Q6NERHVE2WPPAQRHBAC6XB", "01M3Q6NERHS4BG0HQ85VAR1SRH" })
        {
            var root = CurrentComponents();
            FindComponent(root, id)["status"] = "partial_outage";
            var snapshot = ParseStatus(root);
            Equal(OpenAiServiceHealth.Operational, snapshot.Codex);
            Equal(OpenAiServiceHealth.PartialOutage, snapshot.ChatGpt);
        }
        return Task.CompletedTask;
    }

    private static Task MissingCurrentComponent()
    {
        var root = CurrentComponents();
        root["components"]!.AsArray().Remove(FindComponent(root, "01JMXBNJXGV1T5GT2M9XA83XNG"));
        var snapshot = ParseStatus(root);
        Equal(OpenAiServiceHealth.Unavailable, snapshot.ChatGpt);
        return Task.CompletedTask;
    }

    private static Task UnrelatedComponentsIgnored()
    {
        var root = CurrentComponents();
        root["components"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K9G527YRPY1EFRMHTKB5BKT5",
            ["name"] = "Sora",
            ["status"] = "major_outage"
        });
        root["components"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K8C008QVXHA6JX98PAS42VPD",
            ["name"] = "ChatGPT Atlas",
            ["status"] = "major_outage"
        });
        FindComponent(root, "01JSM5RTJWHRWDTS6Q604VEW3B")["status"] = "major_outage";
        var snapshot = ParseStatus(root);
        Equal(OpenAiServiceHealth.Operational, snapshot.Codex);
        Equal(OpenAiServiceHealth.Operational, snapshot.ChatGpt);
        return Task.CompletedTask;
    }

    private static Task ComponentIdentityWins()
    {
        var root = CurrentComponents();
        FindComponent(root, "01JMXBNJXG1S2D9V65P1ZZTD94")["status"] = "major_outage";
        Equal(OpenAiServiceHealth.MajorOutage, ParseStatus(root).ChatGpt);
        return Task.CompletedTask;
    }

    private static Task ModelCost(string model, bool longContext, decimal expected)
    {
        var usage = new TokenUsageCounters(longContext ? 300_000 : 200_000, 100_000, 20_000, 10_000);
        var result = ApiCostEstimator.Calculate(model, usage);
        Equal(expected, result.EstimatedUsd);
        Equal(usage.TotalTokens, result.PricedTokens);
        Equal(0L, result.UnpricedTokens);
        Equal(false, result.UsesProxyPricing);
        return Task.CompletedTask;
    }

    private static Task LongContextBoundary()
    {
        Equal(0.5172m, ApiCostEstimator.Calculate("gpt-6.1-sol", new(272_000, 72_000, 20_000, 10_000)).EstimatedUsd);
        Equal(0.984404m, ApiCostEstimator.Calculate("gpt-6.1-sol", new(272_001, 72_000, 20_000, 10_000)).EstimatedUsd);
        return Task.CompletedTask;
    }

    private static Task UnknownModelVariants()
    {
        foreach (var model in new[] { "private-model", "gpt-6.1-sol-unknown", "gpt-5.6-sol-unknown", "gpt-5.5-unknown", "gpt-5.3-codex-unknown" })
        {
            var result = ApiCostEstimator.Calculate(model, new(900, 0, 0, 100));
            Equal(1_000L, result.UnpricedTokens);
            Equal(0L, result.PricedTokens);
            Equal(0m, result.EstimatedUsd);
        }
        return Task.CompletedTask;
    }

    private static Task LegacyIndexReprices() => CheckIndexReprices(null);

    private static Task ObsoleteCalculationReprices() => CheckIndexReprices("obsolete-parser-and-prices");

    private static Task CheckIndexReprices(string? calculationVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexUsageTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            var sessionPath = Path.Combine(sessions, "usage.jsonl");
            File.WriteAllLines(sessionPath,
            [
                JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "gpt-6.1-sol" } }),
                """{"timestamp":"2026-10-24T08:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100000,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":10000}}}}"""
            ]);
            var indexPath = Path.Combine(root, "index.json");
            var first = new LocalUsageAnalyzer(indexPath);
            first.Analyze(sessions);
            var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
            if (calculationVersion is null)
                index.AsObject().Remove("calculationVersion");
            else
                index["calculationVersion"] = calculationVersion;
            index["sessions"]![0]!["contribution"]!["estimatedUsd"] = 999m;
            File.WriteAllText(indexPath, index.ToJsonString());

            var restarted = new LocalUsageAnalyzer(indexPath);
            var repriced = restarted.Analyze(sessions);
            Equal(0.3m, repriced.EstimatedUsd);
            Equal(1, restarted.LastAnalysisStats.FilesRead);
            Equal(0, restarted.LastAnalysisStats.FilesFromCache);
            var unchanged = new LocalUsageAnalyzer(indexPath);
            Equal(0.3m, unchanged.Analyze(sessions).EstimatedUsd);
            Equal(0, unchanged.LastAnalysisStats.FilesRead);
            Equal(1, unchanged.LastAnalysisStats.FilesFromCache);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static JsonNode CurrentComponents() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "openai-components-2026-10-24.json")))!;

    private static JsonNode FindComponent(JsonNode root, string id) => root["components"]!.AsArray()
        .Single(component => component!["id"]!.GetValue<string>() == id)!;

    private static OpenAiStatusSnapshot ParseStatus(JsonNode root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return OpenAiStatusClient.ParseComponents(document.RootElement, DateTimeOffset.UtcNow);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
}
