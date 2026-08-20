using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using CodexUsageTray;

namespace CodexUsageTray.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Tests =
    [
        ("weekly-only response does not invent 5h", WeeklyOnly),
        ("rate-limit map takes precedence over fallback", MapPrecedence),
        ("5h and weekly response", FiveHourAndWeekly),
        ("unknown duration remains readable", UnknownDuration),
        ("primary and secondary are distinct", PrimarySecondary),
        ("limitName is used before duration", LimitNameFirst),
        ("null optional fields do not throw", NullOptionalFields),
        ("usedPercent below zero is clamped", UsedPercentBelowZero),
        ("usedPercent above 100 is clamped", UsedPercentAboveHundred),
        ("reset timestamp converts without changing instant", ResetTimestampConversion),
        ("invalid reset timestamp becomes unavailable", InvalidResetTimestamp),
        ("most constrained bucket is selected", MostConstrained),
        ("tray selects Frontier instead of a lower Spark bucket", FrontierTrayBucket),
        ("tray does not substitute Spark when Frontier is unavailable", FrontierTrayUnavailable),
        ("tray selects the most constrained Frontier slot deterministically", FrontierTraySlots),
        ("reset credits use availableCount", ResetCredits),
        ("reset-credit details preserve null and empty semantics", ResetCreditDetails),
        ("reset-credit consume outcomes are parsed exactly", ResetCreditOutcomes),
        ("reset-credit settings persist with safe legacy defaults and bounds", ResetCreditModeSettings),
        ("reset-credit automation stages attempts and idempotency keys", ResetCreditAutomationStages),
        ("reset-credit automation honors a custom lead time and eligible fallbacks", ResetCreditAutomationCustomLeadTime),
        ("reset-credit timing changes preserve new attempts and pending retries", ResetCreditAutomationTimingChanges),
        ("reset-credit automation reuses a pending key after transport failure", ResetCreditAutomationTransportRetry),
        ("reset-credit automation filters ineligible credits and completes redemption", ResetCreditAutomationEligibility),
        ("usage summary and latest bucket", UsageSummaryLatest),
        ("usage summary prefers local today", UsageSummaryToday),
        ("unsupported usage becomes unavailable", UsageUnavailable),
        ("cache round trip", CacheRoundTrip),
        ("malformed settings recover to defaults", MalformedSettingsRecovery),
        ("malformed cache returns null", MalformedCacheRecovery),
        ("secret sanitization redacts credentials", SecretSanitization),
        ("secret sanitization redacts refresh and Basic credentials", ExtendedSecretSanitization),
        ("secret sanitization preserves token metrics", SanitizerPreservesMetrics),
        ("notification thresholds fire once per reset window", NotificationThresholds),
        ("notification reports every threshold crossed in one jump", NotificationThresholdJump),
        ("cached data never triggers thresholds", CachedNotificationIgnored),
        ("new reset window creates a baseline", NotificationResetWindow),
        ("notification state survives restart", NotificationStatePersists),
        ("startup command quotes executable path", StartupCommandQuotesPath),
        ("command-file launcher supports spaces", CommandFileLauncherSupportsSpaces),
        ("JSON-RPC responses correlate out of order", JsonRpcCorrelation),
        ("JSON-RPC server requests preserve ids and cannot resolve client requests", JsonRpcServerRequest),
        ("tray ICO contains 16 20 24 and 32 pixel frames", TrayIcoSizes),
        ("tray icon styles render every supported size", TrayStylesRenderEverySize),
        ("solid bar fill matches remaining percentage", SolidBarFill),
        ("segmented bar shows quarter steps", SegmentedBarFill),
        ("bar styles do not render a battery terminal", BarStylesHaveNoBatteryTerminal),
        ("number-only style renders exact value without percent sign or background", PercentOnlyRendersValue),
        ("tray icon special states have distinct marks", TrayIconSpecialStates),
        ("tray icon style persists and legacy settings default to ring", TrayIconStyleSettings),
        ("popup style persists and legacy settings default to classic", PopupStyleSettings),
        ("paper popup style persists", PaperPopupStyleSettings),
        ("update mode persists and legacy settings default to off", UpdateModeSettings),
        ("public GitHub requests do not send authorization", PublicGitHubRequestHasNoAuthorization),
        ("OpenAI status parser aggregates Codex and ChatGPT components", OpenAiStatusAggregation),
        ("OpenAI status parser surfaces outages and missing components", OpenAiStatusOutageAndMissingComponents),
        ("OpenAI status request is public and exact", OpenAiStatusRequestIsPublic),
        ("OpenAI status notifications require a live incident transition", OpenAiStatusTransitions),
        ("updater removes staging data from older versions", UpdaterRemovesOldStagingDirectories),
        ("GitHub release parser compares versions and selects exact asset", GitHubReleaseSelection),
        ("GitHub release parser rejects unsafe metadata", GitHubReleaseUnsafeMetadata),
        ("update package verifies outer and internal SHA-256", UpdatePackageVerification),
        ("update package rejects path traversal", UpdatePackageTraversal),
        ("API-equivalent cost separates cached input and cache writes", ApiCostCategories),
        ("API-equivalent cost applies long-context pricing", ApiCostLongContext),
        ("Spark uses a documented proxy and unknown models remain unpriced", ApiCostProxyAndUnpriced),
        ("local usage groups project tokens without double counting", LocalUsageGroupsProjects),
        ("local usage index persists hashed session contributions", LocalUsageIndexPersists),
        ("malformed local usage index rebuilds without crashing", MalformedLocalUsageIndexRecovery),
        ("usage history filters 7 days 30 days and all time", UsageHistoryRanges),
        ("adaptive refresh reacts to changes visibility idle and battery", AdaptiveRefresh),
        ("protocol compatibility recognizes current and legacy schemas", ProtocolCompatibilityKnown),
        ("protocol compatibility reports unknown schema without throwing", ProtocolCompatibilityDrift),
        ("tray context menu uses dark modern renderer", ModernContextMenuStyle),
        ("usage popup styles keep scrollbars disabled", UsagePopupHasNoScrollbar),
        ("mac popup compacts bucket cards on short screens", UsagePopupMacCompactBuckets),
        ("usage popup closes only for an intentional outside pointer action", UsagePopupDeactivatePolicy),
        ("usage popup styles show Codex and ChatGPT service status", UsagePopupShowsOpenAiStatus),
        ("usage popup identifies the Frontier tray bucket before service status", UsagePopupIdentifiesFrontier),
        ("tray icon replacement does not leak GDI handles", TrayIconDoesNotLeak),
        ("tray tooltip preserves exact percentage", TrayTooltipPreservesPercentage)
    ];

    private static async Task<int> Main()
    {
        var passed = 0;
        var failed = 0;
        foreach (var test in Tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
                passed++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
                failed++;
            }
        }

        Console.WriteLine($"Test summary: Total={Tests.Count}, Passed={passed}, Failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static Task WeeklyOnly()
    {
        var buckets = Normalize("weekly-only.json");
        Equal(1, buckets.Count);
        Equal("Weekly", buckets[0].Name);
        Equal(100, buckets[0].RemainingPercent);
        False(buckets.Any(bucket => bucket.Name.Contains("5h", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    private static Task MapPrecedence()
    {
        var buckets = Normalize("weekly-only.json");
        False(buckets.Any(bucket => bucket.LimitId == "legacy"));
        return Task.CompletedTask;
    }

    private static Task FiveHourAndWeekly()
    {
        var buckets = Normalize("five-hour-weekly.json");
        Equal(2, buckets.Count);
        True(buckets.Any(bucket => bucket.Name == "5h"));
        True(buckets.Any(bucket => bucket.Name == "Weekly"));
        return Task.CompletedTask;
    }

    private static Task UnknownDuration()
    {
        Equal("90 min", Normalize("unknown-duration.json").Single().Name);
        return Task.CompletedTask;
    }

    private static Task PrimarySecondary()
    {
        var buckets = Normalize("named-primary-secondary.json");
        Equal(2, buckets.Count);
        NotEqual(buckets[0].Key, buckets[1].Key);
        Equal("primary", buckets[0].Slot);
        Equal("secondary", buckets[1].Slot);
        return Task.CompletedTask;
    }

    private static Task LimitNameFirst()
    {
        var buckets = Normalize("named-primary-secondary.json");
        True(buckets.All(bucket => bucket.Name.StartsWith("GPT-5.3-Codex-Spark", StringComparison.Ordinal)));
        return Task.CompletedTask;
    }

    private static Task NullOptionalFields()
    {
        var buckets = Normalize("null-optionals.json");
        Equal(2, buckets.Count);
        True(buckets[0].ResetsAtLocal is null);
        return Task.CompletedTask;
    }

    private static Task UsedPercentBelowZero()
    {
        var bucket = Normalize("null-optionals.json")[0];
        Equal(0, bucket.UsedPercent);
        Equal(100, bucket.RemainingPercent);
        return Task.CompletedTask;
    }

    private static Task UsedPercentAboveHundred()
    {
        var bucket = Normalize("null-optionals.json")[1];
        Equal(100, bucket.UsedPercent);
        Equal(0, bucket.RemainingPercent);
        return Task.CompletedTask;
    }

    private static Task ResetTimestampConversion()
    {
        var bucket = Normalize("weekly-only.json").Single();
        Equal(1784703842L, bucket.ResetsAtLocal!.Value.ToUnixTimeSeconds());
        return Task.CompletedTask;
    }

    private static Task InvalidResetTimestamp()
    {
        True(Normalize("null-optionals.json")[1].ResetsAtLocal is null);
        return Task.CompletedTask;
    }

    private static Task MostConstrained()
    {
        var buckets = Normalize("five-hour-weekly.json").ToList();
        var snapshot = new UsageSnapshot { Buckets = buckets };
        Equal(45, snapshot.MostConstrained!.RemainingPercent);
        Equal("Weekly", snapshot.MostConstrained.Name);
        return Task.CompletedTask;
    }

    private static Task FrontierTrayBucket()
    {
        var reset = DateTimeOffset.Now.AddDays(7);
        var frontier = new RateLimitBucket("codex:primary", "Weekly", "codex", "primary", 9, 91, 10080, reset.ToUnixTimeSeconds(), reset, null);
        var sparkFiveHour = new RateLimitBucket("codex_bengalfox:primary", "GPT-5.3-Codex-Spark · 5h", "codex_bengalfox", "primary", 46, 54, 300, reset.ToUnixTimeSeconds(), reset, null);
        var sparkWeekly = new RateLimitBucket("codex_bengalfox:secondary", "GPT-5.3-Codex-Spark · Weekly", "codex_bengalfox", "secondary", 20, 80, 10080, reset.ToUnixTimeSeconds(), reset, null);
        var snapshot = new UsageSnapshot
        {
            Status = ConnectionStatus.RateLimitReached,
            Buckets = [sparkFiveHour, sparkWeekly, frontier]
        };

        Equal("codex:primary", snapshot.TrayBucket!.Key);
        Equal(91, snapshot.TrayBucket.RemainingPercent);
        Equal(ConnectionStatus.Connected, TrayApplicationContext.TrayConnectionStatus(snapshot.Status, snapshot.TrayBucket));
        Equal("Codex Frontier · Weekly 91% left · Reset " + reset.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture), TrayTooltipFormatter.Format(snapshot));
        return Task.CompletedTask;
    }

    private static Task FrontierTrayUnavailable()
    {
        var spark = new RateLimitBucket("codex_bengalfox:primary", "GPT-5.3-Codex-Spark · 5h", "codex_bengalfox", "primary", 46, 54, 300, null, null, null);
        var snapshot = new UsageSnapshot { Status = ConnectionStatus.Connected, Buckets = [spark] };
        True(snapshot.TrayBucket is null);
        Equal("Codex Frontier · unavailable", TrayTooltipFormatter.Format(snapshot));

        var missingLimitId = spark with { Key = "default:primary", Name = "Weekly", LimitId = "default", RemainingPercent = 72 };
        True((snapshot with { Buckets = [missingLimitId] }).TrayBucket is null);
        return Task.CompletedTask;
    }

    private static Task FrontierTraySlots()
    {
        var primary = new RateLimitBucket("codex:primary", "Frontier · 5h", "CODEX", "primary", 20, 80, 300, null, null, null);
        var secondary = new RateLimitBucket("codex:secondary", "Frontier · Weekly", "codex", "secondary", 60, 40, 10080, null, null, null);
        var reached = primary with { RateLimitReachedType = "hard_limit" };

        Equal("codex:secondary", UsageSnapshot.SelectTrayBucket([primary, secondary])!.Key);
        Equal("codex:primary", UsageSnapshot.SelectTrayBucket([primary, primary with { Key = "codex:secondary", Slot = "secondary" }])!.Key);
        Equal("codex:primary", UsageSnapshot.SelectTrayBucket([secondary, reached])!.Key);
        Equal(0, UsageSnapshot.EffectiveRemainingPercent(reached));
        Equal(ConnectionStatus.RateLimitReached, TrayApplicationContext.TrayConnectionStatus(ConnectionStatus.RateLimitReached, reached));
        return Task.CompletedTask;
    }

    private static Task ResetCredits()
    {
        using var document = ReadFixture("weekly-only.json");
        Equal(4L, RateLimitNormalizer.ReadResetCredits(document.RootElement));
        True(RateLimitNormalizer.NormalizeResetCredits(document.RootElement).Credits is null);
        return Task.CompletedTask;
    }

    private static Task ResetCreditDetails()
    {
        using var document = ReadFixture("reset-credit-details.json");
        var summary = RateLimitNormalizer.NormalizeResetCredits(document.RootElement);
        Equal(2L, summary.AvailableCount);
        Equal(2, summary.Credits!.Count);
        Equal("credit-first", summary.Credits[0].Id);
        Equal("codexRateLimits", summary.Credits[0].ResetType);
        Equal("available", summary.Credits[0].Status);
        Equal(1785528616L, summary.Credits[0].ExpiresAtLocal!.Value.ToUnixTimeSeconds());

        using var empty = JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":3000000000,"credits":[]}}""");
        var emptySummary = RateLimitNormalizer.NormalizeResetCredits(empty.RootElement);
        Equal(3_000_000_000L, emptySummary.AvailableCount);
        Equal(0, emptySummary.Credits!.Count);

        using var malformed = JsonDocument.Parse(
            """{"rateLimitResetCredits":{"availableCount":1,"credits":[null,5,{},{"id":"safe","resetType":"codexRateLimits","status":"available","grantedAt":1785000000,"expiresAt":9223372036854775807}]}}""");
        var malformedSummary = RateLimitNormalizer.NormalizeResetCredits(malformed.RootElement);
        Equal(1, malformedSummary.Credits!.Count);
        True(malformedSummary.Credits[0].ExpiresAtLocal is null);
        return Task.CompletedTask;
    }

    private static Task ResetCreditOutcomes()
    {
        foreach (var (value, expected) in new[]
        {
            ("reset", ResetCreditConsumeOutcome.Reset),
            ("nothingToReset", ResetCreditConsumeOutcome.NothingToReset),
            ("noCredit", ResetCreditConsumeOutcome.NoCredit),
            ("alreadyRedeemed", ResetCreditConsumeOutcome.AlreadyRedeemed),
            ("unexpected", ResetCreditConsumeOutcome.Unknown)
        })
        {
            using var response = JsonDocument.Parse($$"""{"outcome":"{{value}}"}""");
            Equal(expected, CodexAppServerSession.ParseResetCreditOutcome(response.RootElement));
        }
        return Task.CompletedTask;
    }

    private static Task UsageSummaryLatest()
    {
        using var document = ReadFixture("usage-summary.json");
        var usage = RateLimitNormalizer.NormalizeUsage(document.RootElement, new DateOnly(2026, 7, 15));
        True(usage.IsAvailable);
        Equal("2026-07-14", usage.TodayOrLatestDate);
        Equal(188945203L, usage.TodayOrLatestTokens);
        Equal(5965926592L, usage.LifetimeTokens);
        Equal(283350812L, usage.PeakDailyTokens);
        return Task.CompletedTask;
    }

    private static Task UsageSummaryToday()
    {
        using var document = ReadFixture("usage-summary.json");
        var usage = RateLimitNormalizer.NormalizeUsage(document.RootElement, new DateOnly(2026, 7, 13));
        Equal("2026-07-13", usage.TodayOrLatestDate);
        Equal(1000L, usage.TodayOrLatestTokens);
        return Task.CompletedTask;
    }

    private static Task UsageUnavailable()
    {
        False(RateLimitNormalizer.NormalizeUsage(null, DateOnly.FromDateTime(DateTime.Today)).IsAvailable);
        return Task.CompletedTask;
    }

    private static Task CacheRoundTrip()
    {
        WithTempDirectory(root =>
        {
            var paths = new AppPaths(root);
            var store = new JsonStore();
            var expected = new UsageSnapshot
            {
                UpdatedAt = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(7)),
                IsLive = true,
                PlanType = "prolite",
                Buckets = Normalize("weekly-only.json").ToList(),
                Status = ConnectionStatus.Connected
            };
            store.Write(paths.Cache, expected);
            var actual = store.ReadSnapshot(paths.Cache)!;
            Equal(expected.PlanType, actual.PlanType);
            Equal(expected.Buckets.Count, actual.Buckets.Count);
            Equal(expected.UpdatedAt, actual.UpdatedAt);
        });
        return Task.CompletedTask;
    }

    private static Task MalformedSettingsRecovery()
    {
        WithTempDirectory(root =>
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{not-json");
            var settings = new JsonStore().ReadSettings(path);
            Equal(2, settings.RefreshIntervalMinutes);
            Equal(10, settings.StaleAfterMinutes);
        });
        return Task.CompletedTask;
    }

    private static Task MalformedCacheRecovery()
    {
        WithTempDirectory(root =>
        {
            var path = Path.Combine(root, "cache.json");
            File.WriteAllText(path, "[] broken");
            True(new JsonStore().ReadSnapshot(path) is null);
        });
        return Task.CompletedTask;
    }

    private static Task SecretSanitization()
    {
        var fakeApiKey = "sk-" + new string('a', 26);
        var input = $"Authorization: Bearer abc.def.ghi access_token=secret-value apiKey={fakeApiKey}";
        var sanitized = SecretSanitizer.Sanitize(input);
        False(sanitized.Contains("abc.def.ghi", StringComparison.Ordinal));
        False(sanitized.Contains("secret-value", StringComparison.Ordinal));
        False(sanitized.Contains(fakeApiKey, StringComparison.Ordinal));
        True(sanitized.Contains("[REDACTED]", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task SanitizerPreservesMetrics()
    {
        var input = "lifetimeTokens=123 peakDailyTokens=456 Token activity available";
        Equal(input, SecretSanitizer.Sanitize(input));
        return Task.CompletedTask;
    }

    private static Task ExtendedSecretSanitization()
    {
        var fakeGitHubToken = "github_" + "pat_" + new string('A', 36);
        var input = "refresh_token=rt-secret\nrefreshToken=\"refresh secret words\"\n" +
                    "Authorization: Basic dXNlcjpwYXNz\ncredential=\"credential secret words\"\n" +
                    fakeGitHubToken;
        var sanitized = SecretSanitizer.Sanitize(input);
        False(sanitized.Contains("rt-secret", StringComparison.Ordinal));
        False(sanitized.Contains("refresh secret words", StringComparison.Ordinal));
        False(sanitized.Contains("dXNlcjpwYXNz", StringComparison.Ordinal));
        False(sanitized.Contains("credential secret words", StringComparison.Ordinal));
        False(sanitized.Contains(fakeGitHubToken, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task NotificationThresholds()
    {
        var tracker = new NotificationTracker();
        Empty(tracker.Evaluate([Bucket(25, 100)], true));
        Equal(20, tracker.Evaluate([Bucket(20, 100)], true).Single().Threshold);
        Empty(tracker.Evaluate([Bucket(20, 100)], true));
        Equal(5, tracker.Evaluate([Bucket(4, 100)], true).Single().Threshold);
        Equal(0, tracker.Evaluate([Bucket(0, 100)], true).Single().Threshold);
        Empty(tracker.Evaluate([Bucket(0, 100)], true));
        return Task.CompletedTask;
    }

    private static Task CachedNotificationIgnored()
    {
        var tracker = new NotificationTracker();
        Empty(tracker.Evaluate([Bucket(25, 100)], true));
        Empty(tracker.Evaluate([Bucket(0, 100)], false));
        var alerts = tracker.Evaluate([Bucket(19, 100)], true);
        Equal(20, alerts.Single().Threshold);
        return Task.CompletedTask;
    }

    private static Task NotificationThresholdJump()
    {
        var tracker = new NotificationTracker();
        Empty(tracker.Evaluate([Bucket(25, 100)], true));
        var alerts = tracker.Evaluate([Bucket(0, 100)], true);
        Equal(3, alerts.Count);
        True(alerts.Select(item => item.Threshold).SequenceEqual([20, 5, 0]));
        return Task.CompletedTask;
    }

    private static Task NotificationResetWindow()
    {
        var tracker = new NotificationTracker();
        Empty(tracker.Evaluate([Bucket(25, 100)], true));
        Equal(20, tracker.Evaluate([Bucket(19, 100)], true).Single().Threshold);
        Empty(tracker.Evaluate([Bucket(10, 200)], true));
        Equal(5, tracker.Evaluate([Bucket(5, 200)], true).Single().Threshold);
        return Task.CompletedTask;
    }

    private static Task NotificationStatePersists()
    {
        WithTempDirectory(root =>
        {
            var path = Path.Combine(root, "notification-state.json");
            var first = new NotificationTracker(path);
            Empty(first.Evaluate([Bucket(25, 100)], true));
            Equal(20, first.Evaluate([Bucket(19, 100)], true).Single().Threshold);

            var restarted = new NotificationTracker(path);
            Empty(restarted.Evaluate([Bucket(10, 100)], true));
            Equal(5, restarted.Evaluate([Bucket(5, 100)], true).Single().Threshold);
        });
        return Task.CompletedTask;
    }

    private static Task StartupCommandQuotesPath()
    {
        Equal("\"C:\\Program Files\\Codex Usage Tray\\CodexUsageTray.exe\" --autostart",
            StartupManager.BuildCommand("C:\\Program Files\\Codex Usage Tray\\CodexUsageTray.exe"));
        return Task.CompletedTask;
    }

    private static async Task CommandFileLauncherSupportsSpaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codex Usage Tray Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var commandFile = Path.Combine(root, "fake codex.cmd");
            await File.WriteAllTextAsync(commandFile, "@echo off\r\necho codex-cli fake\r\n");
            using var process = new System.Diagnostics.Process
            {
                StartInfo = CodexExecutableLocator.CreateStartInfo(commandFile, ["--version"])
            };
            True(process.Start());
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            True(process.ExitCode == 0, $"Command file exited {process.ExitCode}: {error}");
            True(output.Contains("codex-cli fake", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task JsonRpcCorrelation()
    {
        var registry = new PendingRequestRegistry();
        var first = registry.Add(10);
        var second = registry.Add(11);
        using var secondDocument = JsonDocument.Parse("{\"id\":11,\"result\":{\"value\":\"second\"}}");
        using var firstDocument = JsonDocument.Parse("{\"id\":10,\"result\":{\"value\":\"first\"}}");
        True(registry.Resolve(11, secondDocument.RootElement));
        True(registry.Resolve(10, firstDocument.RootElement));
        Equal("first", (await first).GetProperty("result").GetProperty("value").GetString());
        Equal("second", (await second).GetProperty("result").GetProperty("value").GetString());
    }

    private static async Task JsonRpcServerRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexUsageTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var pipeServer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out, HandleInheritability.None);
            using var pipeClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipeServer.GetClientHandleAsString());
            pipeServer.DisposeLocalCopyOfClientHandle();
            using var input = new StreamReader(pipeClient);
            using var outputStream = new MemoryStream();
            using var output = new StreamWriter(outputStream, leaveOpen: true) { AutoFlush = true };
            await using var client = new JsonRpcClient(input, output, new AppLogger(new AppPaths(root)), startReader: false);

            var first = client.RequestAsync("client/read", new { }, TimeSpan.FromSeconds(5), CancellationToken.None);
            True(client.ProcessLine("{\"id\":1,\"method\":\"server/test\",\"params\":{\"refreshToken\":\"rt-secret\"}}"));
            False(first.IsCompleted);
            True(client.ProcessLine("{\"id\":1,\"result\":{\"value\":\"client-result\"}}"));
            Equal("client-result", (await first).GetProperty("value").GetString());

            True(client.ProcessLine("{\"id\":\"server-1\",\"method\":\"server/test\",\"params\":{}}"));
            await WaitUntilAsync(() => Encoding.UTF8.GetString(outputStream.ToArray()).Split("-32601").Length - 1 == 2);
            var written = Encoding.UTF8.GetString(outputStream.ToArray());
            True(written.Contains("\"id\":1,\"error\"", StringComparison.Ordinal));
            True(written.Contains("\"id\":\"server-1\",\"error\"", StringComparison.Ordinal));
            False(written.Contains("rt-secret", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        True(condition(), "Condition was not met before timeout.");
    }

    private static Task TrayIcoSizes()
    {
        var bytes = TrayIconRenderer.CreateIcoBytes(ConnectionStatus.Connected, 74, TrayIconStyle.Ring);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        Equal((ushort)0, reader.ReadUInt16());
        Equal((ushort)1, reader.ReadUInt16());
        Equal((ushort)4, reader.ReadUInt16());
        var sizes = new List<int>();
        for (var index = 0; index < 4; index++)
        {
            sizes.Add(reader.ReadByte());
            Equal(sizes[^1], (int)reader.ReadByte());
            reader.ReadBytes(14);
        }
        True(sizes.SequenceEqual([16, 20, 24, 32]));
        return Task.CompletedTask;
    }

    private static Task TrayStylesRenderEverySize()
    {
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            foreach (var size in new[] { 16, 20, 24, 32 })
            {
                using var bitmap = TrayIconRenderer.RenderFrame(ConnectionStatus.Connected, 74, style, size);
                Equal(size, bitmap.Width);
                Equal(size, bitmap.Height);
                True(CountPixels(bitmap, color => color.A > 0) > 0);
            }
        }
        return Task.CompletedTask;
    }

    private static Task SolidBarFill()
    {
        var fillColor = Color.FromArgb(255, 53, 201, 143);
        foreach (var remaining in new[] { 0, 25, 50, 75, 100 })
        {
            using var bitmap = TrayIconRenderer.RenderFrame(ConnectionStatus.Connected, remaining, TrayIconStyle.SolidBar, 32);
            Equal(TrayIconRenderer.FillWidth(24, remaining) * 8, CountColor(bitmap, fillColor));
        }
        Equal(0, TrayIconRenderer.FillWidth(20, -1));
        Equal(20, TrayIconRenderer.FillWidth(20, 101));
        return Task.CompletedTask;
    }

    private static Task SegmentedBarFill()
    {
        var fillColor = Color.FromArgb(255, 53, 201, 143);
        foreach (var (remaining, expectedRuns) in new[] { (25, 1), (50, 2), (75, 3), (100, 4) })
        {
            using var bitmap = TrayIconRenderer.RenderFrame(ConnectionStatus.Connected, remaining, TrayIconStyle.SegmentedBar, 16);
            Equal(expectedRuns, CountColorRuns(bitmap, 7, fillColor));
        }
        return Task.CompletedTask;
    }

    private static Task BarStylesHaveNoBatteryTerminal()
    {
        foreach (var style in new[] { TrayIconStyle.SolidBar, TrayIconStyle.SegmentedBar })
        {
            using var bitmap = TrayIconRenderer.RenderFrame(ConnectionStatus.Connected, 100, style, 16);
            Equal(0, bitmap.GetPixel(15, 7).A);
        }
        return Task.CompletedTask;
    }

    private static Task PercentOnlyRendersValue()
    {
        Equal("100", TrayIconRenderer.FormatPercentOnlyValue(ConnectionStatus.Connected, true, 100));
        Equal("74", TrayIconRenderer.FormatPercentOnlyValue(ConnectionStatus.Connected, true, 74));
        Equal("--", TrayIconRenderer.FormatPercentOnlyValue(ConnectionStatus.Connected, false, 0));
        Equal("0", TrayIconRenderer.FormatPercentOnlyValue(ConnectionStatus.RateLimitReached, true, 10));

        using var bitmap = TrayIconRenderer.RenderFrame(ConnectionStatus.Connected, 100, TrayIconStyle.CodexPercent, 16);
        var labelColor = Color.FromArgb(255, 245, 245, 245);
        var valueColor = Color.FromArgb(255, 53, 201, 143);
        var formerBackingColor = Color.FromArgb(255, 20, 23, 29);
        Equal(0, CountColor(bitmap, labelColor));
        True(CountPixelsIn(bitmap, new Rectangle(0, 0, 16, 16), color => color.ToArgb() == valueColor.ToArgb()) > 0);
        True(CountPixels(bitmap, color => color.A > 0 && color.R < 30 && color.G < 30 && color.B < 30) > 0);
        Equal(0, CountColor(bitmap, formerBackingColor));
        Equal(0, bitmap.GetPixel(0, 0).A);

        using var stale = TrayIconRenderer.RenderFrame(ConnectionStatus.DataStale, 50, TrayIconStyle.CodexPercent, 16);
        True(CountPixelsIn(stale, new Rectangle(13, 0, 3, 3), color => color.ToArgb() == formerBackingColor.ToArgb()) > 0);
        return Task.CompletedTask;
    }

    private static Task TrayIconSpecialStates()
    {
        var cases = new[]
        {
            (ConnectionStatus.Loading, (int?)null, Color.FromArgb(255, 88, 166, 255)),
            (ConnectionStatus.DataStale, (int?)50, Color.FromArgb(255, 245, 166, 35)),
            (ConnectionStatus.CliNotFound, (int?)null, Color.FromArgb(255, 244, 83, 83)),
            (ConnectionStatus.AppServerUnavailable, (int?)null, Color.FromArgb(255, 244, 83, 83)),
            (ConnectionStatus.NotSignedIn, (int?)null, Color.FromArgb(255, 174, 181, 194)),
            (ConnectionStatus.RateLimitReached, (int?)0, Color.FromArgb(255, 244, 83, 83))
        };
        foreach (var (status, remaining, markerColor) in cases)
        {
            using var bitmap = TrayIconRenderer.RenderFrame(status, remaining, TrayIconStyle.SolidBar, 16);
            True(CountColor(bitmap, markerColor) > 0, $"Expected visible marker for {status}.");
        }
        return Task.CompletedTask;
    }

    private static Task TrayIconStyleSettings()
    {
        WithTempDirectory(root =>
        {
            var store = new JsonStore();
            var path = Path.Combine(root, "settings.json");
            store.Write(path, new AppSettings { TrayIconStyle = TrayIconStyle.SegmentedBar });
            Equal(TrayIconStyle.SegmentedBar, store.ReadSettings(path).TrayIconStyle);

            File.WriteAllText(path, "{\"refreshIntervalMinutes\":2,\"staleAfterMinutes\":10}");
            Equal(TrayIconStyle.Ring, store.ReadSettings(path).TrayIconStyle);
        });
        return Task.CompletedTask;
    }

    private static Task PopupStyleSettings()
    {
        WithTempDirectory(root =>
        {
            var store = new JsonStore();
            var path = Path.Combine(root, "settings.json");
            store.Write(path, new AppSettings { PopupStyle = PopupStyle.MacOS });
            Equal(PopupStyle.MacOS, store.ReadSettings(path).PopupStyle);

            File.WriteAllText(path, "{\"refreshIntervalMinutes\":2,\"staleAfterMinutes\":10}");
            Equal(PopupStyle.Classic, store.ReadSettings(path).PopupStyle);
        });
        return Task.CompletedTask;
    }

    private static Task PaperPopupStyleSettings()
    {
        WithTempDirectory(root =>
        {
            var store = new JsonStore();
            var path = Path.Combine(root, "settings.json");
            store.Write(path, new AppSettings { PopupStyle = PopupStyle.Paper });
            Equal(PopupStyle.Paper, store.ReadSettings(path).PopupStyle);
        });
        return Task.CompletedTask;
    }

    private static Task UpdateModeSettings()
    {
        WithTempDirectory(root =>
        {
            var store = new JsonStore();
            var path = Path.Combine(root, "settings.json");
            store.Write(path, new AppSettings { UpdateMode = UpdateMode.Automatic });
            Equal(UpdateMode.Automatic, store.ReadSettings(path).UpdateMode);

            File.WriteAllText(path, "{\"refreshIntervalMinutes\":2,\"staleAfterMinutes\":10}");
            Equal(UpdateMode.Off, store.ReadSettings(path).UpdateMode);
        });
        return Task.CompletedTask;
    }

    private static Task ResetCreditModeSettings()
    {
        WithTempDirectory(root =>
        {
            var store = new JsonStore();
            var path = Path.Combine(root, "settings.json");
            store.Write(path, new AppSettings
            {
                ResetCreditMode = ResetCreditMode.UseBeforeExpiry,
                ResetCreditLeadTimeMinutes = 90
            });
            Equal(ResetCreditMode.UseBeforeExpiry, store.ReadSettings(path).ResetCreditMode);
            Equal(90, store.ReadSettings(path).ResetCreditLeadTimeMinutes);

            File.WriteAllText(path, "{\"refreshIntervalMinutes\":2,\"staleAfterMinutes\":10}");
            Equal(ResetCreditMode.Off, store.ReadSettings(path).ResetCreditMode);
            Equal(ResetCreditAutomation.DefaultLeadTimeMinutes, store.ReadSettings(path).ResetCreditLeadTimeMinutes);

            File.WriteAllText(path, "{\"resetCreditLeadTimeMinutes\":1}");
            Equal(ResetCreditAutomation.MinimumLeadTimeMinutes, store.ReadSettings(path).ResetCreditLeadTimeMinutes);
            File.WriteAllText(path, "{\"resetCreditLeadTimeMinutes\":20000}");
            Equal(ResetCreditAutomation.MaximumLeadTimeMinutes, store.ReadSettings(path).ResetCreditLeadTimeMinutes);
        });
        return Task.CompletedTask;
    }

    private static Task ResetCreditAutomationStages()
    {
        WithTempDirectory(root =>
        {
            var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.FromHours(7));
            var expires = now.AddHours(8);
            var credit = ResetCredit("credit-stages", expires);
            var statePath = Path.Combine(root, "reset-credit-state.json");
            var automation = new ResetCreditAutomation(statePath, new JsonStore());

            Equal(ResetCreditActionKind.None, automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, now).Action);
            Equal(expires.AddHours(-6), automation.NextEvaluationAt([credit], ResetCreditMode.UseBeforeExpiry, now));

            var first = automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, expires.AddHours(-5));
            Equal(ResetCreditActionKind.Consume, first.Action);
            Equal(0, first.Stage);
            True(!string.IsNullOrWhiteSpace(first.IdempotencyKey));
            False(File.ReadAllText(statePath).Contains("credit-stages", StringComparison.Ordinal));
            automation.RecordOutcome(first, ResetCreditConsumeOutcome.NothingToReset, expires.AddHours(-5));

            var second = automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-30));
            Equal(1, second.Stage);
            NotEqual(first.IdempotencyKey, second.IdempotencyKey);
            automation.RecordOutcome(second, ResetCreditConsumeOutcome.NothingToReset, expires.AddMinutes(-30));

            var third = automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-10));
            Equal(2, third.Stage);
            automation.RecordOutcome(third, ResetCreditConsumeOutcome.NothingToReset, expires.AddMinutes(-10));

            var fourth = automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-3));
            Equal(3, fourth.Stage);
            automation.RecordOutcome(fourth, ResetCreditConsumeOutcome.NothingToReset, expires.AddMinutes(-3));
            Equal(ResetCreditActionKind.None, automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-2)).Action);
        });
        return Task.CompletedTask;
    }

    private static Task ResetCreditAutomationCustomLeadTime()
    {
        WithTempDirectory(root =>
        {
            var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(7));
            var expires = now.AddHours(2);
            var credit = ResetCredit("credit-custom-window", expires);
            var automation = new ResetCreditAutomation(Path.Combine(root, "custom.json"), new JsonStore());

            Equal(expires.AddMinutes(-30), automation.NextEvaluationAt(
                [credit], ResetCreditMode.UseBeforeExpiry, now, 30));
            Equal(ResetCreditActionKind.None, automation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-31), 30).Action);

            var first = automation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-29), 30);
            Equal(ResetCreditActionKind.Consume, first.Action);
            Equal(0, first.Stage);
            False(first.IsFinalStage);
            automation.RecordOutcome(first, ResetCreditConsumeOutcome.NothingToReset, expires.AddMinutes(-29));

            Equal(ResetCreditActionKind.None, automation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-20), 30).Action);
            var fifteenMinuteFallback = automation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-10), 30);
            Equal(2, fifteenMinuteFallback.Stage);
            automation.RecordOutcome(
                fifteenMinuteFallback,
                ResetCreditConsumeOutcome.NothingToReset,
                expires.AddMinutes(-10));

            var finalFallback = automation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-3), 30);
            Equal(3, finalFallback.Stage);
            True(finalFallback.IsFinalStage);
            automation.RecordOutcome(finalFallback, ResetCreditConsumeOutcome.NothingToReset, expires.AddMinutes(-3));
            Equal(ResetCreditActionKind.None, automation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-2), 30).Action);
            True(automation.NextEvaluationAt(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-2), 30) is null);

            var fiveMinuteAutomation = new ResetCreditAutomation(
                Path.Combine(root, "five-minute.json"),
                new JsonStore());
            var fiveMinuteDecision = fiveMinuteAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-4), 5);
            Equal(0, fiveMinuteDecision.Stage);
            True(fiveMinuteDecision.IsFinalStage);

            var exactHourAutomation = new ResetCreditAutomation(
                Path.Combine(root, "one-hour.json"),
                new JsonStore());
            var exactHourDecision = exactHourAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-59), 60);
            Equal(0, exactHourDecision.Stage);
            exactHourAutomation.RecordOutcome(
                exactHourDecision,
                ResetCreditConsumeOutcome.NothingToReset,
                expires.AddMinutes(-59));
            var afterExactHour = exactHourAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-10), 60);
            Equal(2, afterExactHour.Stage);

            var notification = new ResetCreditAutomation(
                Path.Combine(root, "notification.json"),
                new JsonStore());
            Equal(ResetCreditActionKind.None, notification.Evaluate(
                [credit], ResetCreditMode.NotifyBeforeExpiry, expires.AddMinutes(-31), 30).Action);
            Equal(ResetCreditActionKind.Notify, notification.Evaluate(
                [credit], ResetCreditMode.NotifyBeforeExpiry, expires.AddMinutes(-29), 30).Action);
            Equal(ResetCreditActionKind.None, notification.Evaluate(
                [credit], ResetCreditMode.NotifyBeforeExpiry, expires.AddMinutes(-10), 30).Action);

            Equal("30 minutes", ResetCreditTimingDialog.FormatLeadTime(30));
            Equal("1 day 1 hour 30 minutes", ResetCreditTimingDialog.FormatLeadTime(1530));
        });
        return Task.CompletedTask;
    }

    private static Task ResetCreditAutomationTimingChanges()
    {
        WithTempDirectory(root =>
        {
            var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(7));
            var expires = now.AddHours(6);
            var credit = ResetCredit("credit-timing-change", expires);
            var store = new JsonStore();
            var completedPath = Path.Combine(root, "completed-change.json");
            var completedAutomation = new ResetCreditAutomation(completedPath, store);

            var sixHourAttempt = completedAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, now, 360);
            Equal(0, sixHourAttempt.Stage);
            completedAutomation.RecordOutcome(
                sixHourAttempt,
                ResetCreditConsumeOutcome.NothingToReset,
                now);

            var restartedWithFiveMinutes = new ResetCreditAutomation(completedPath, store);
            var fiveMinuteAttempt = restartedWithFiveMinutes.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-4), 5);
            Equal(ResetCreditActionKind.Consume, fiveMinuteAttempt.Action);
            Equal(0, fiveMinuteAttempt.Stage);
            Equal(5, fiveMinuteAttempt.LeadTimeMinutes);
            True(fiveMinuteAttempt.IsFinalStage);

            var pendingPath = Path.Combine(root, "pending-change.json");
            var pendingAutomation = new ResetCreditAutomation(pendingPath, store);
            var pending = pendingAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, now, 360);
            pendingAutomation.RecordTransportFailure(pending, now);
            Equal(now.AddMinutes(1), pendingAutomation.NextEvaluationAt(
                [credit], ResetCreditMode.UseBeforeExpiry, now.AddSeconds(30), 5));

            var restartedPending = new ResetCreditAutomation(pendingPath, store);
            var retry = restartedPending.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, now.AddMinutes(1), 5);
            Equal(ResetCreditActionKind.Consume, retry.Action);
            Equal(pending.IdempotencyKey, retry.IdempotencyKey);
            Equal(360, retry.LeadTimeMinutes);

            var nearExpiryCredit = ResetCredit("credit-no-safe-retry", now.AddSeconds(30));
            var noRetryAutomation = new ResetCreditAutomation(
                Path.Combine(root, "no-safe-retry.json"),
                store);
            var nearExpiryAttempt = noRetryAutomation.Evaluate(
                [nearExpiryCredit], ResetCreditMode.UseBeforeExpiry, now, 5);
            noRetryAutomation.RecordTransportFailure(nearExpiryAttempt, now);
            Equal(ResetCreditActionKind.None, noRetryAutomation.Evaluate(
                [nearExpiryCredit], ResetCreditMode.UseBeforeExpiry, now.AddSeconds(10), 5).Action);

            var legacyPath = Path.Combine(root, "legacy-state.json");
            File.WriteAllText(
                legacyPath,
                $$"""
                {
                  "credits": [
                    {
                      "creditKey": "{{ResetCreditAutomation.CreditKey(credit.Id)}}",
                      "expiresAt": "{{expires:O}}",
                      "notificationSent": false,
                      "completed": false,
                      "lastCompletedStage": 0,
                      "pendingStage": null,
                      "idempotencyKey": null,
                      "retryAt": null
                    }
                  ]
                }
                """);
            var migratedLegacy = new ResetCreditAutomation(legacyPath, store);
            Equal(ResetCreditActionKind.None, migratedLegacy.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, now, 360).Action);
            Equal(ResetCreditActionKind.Consume, migratedLegacy.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-4), 5).Action);

            File.WriteAllText(
                legacyPath,
                $$"""
                {
                  "credits": [
                    {
                      "creditKey": "{{ResetCreditAutomation.CreditKey(credit.Id)}}",
                      "expiresAt": "{{expires:O}}",
                      "notificationSent": false,
                      "completed": false,
                      "lastCompletedStage": 2,
                      "pendingStage": null,
                      "idempotencyKey": null,
                      "retryAt": null
                    }
                  ]
                }
                """);
            var legacyFifteen = new ResetCreditAutomation(legacyPath, store);
            Equal(ResetCreditActionKind.None, legacyFifteen.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-14), 15).Action);

            var canonicalPath = Path.Combine(root, "canonical-window.json");
            var canonicalAutomation = new ResetCreditAutomation(canonicalPath, store);
            var customFifteen = canonicalAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-14), 15);
            Equal(0, customFifteen.Stage);
            canonicalAutomation.RecordOutcome(
                customFifteen,
                ResetCreditConsumeOutcome.NothingToReset,
                expires.AddMinutes(-14));
            Equal(ResetCreditActionKind.None, canonicalAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-13), 360).Action);

            var fallbackPath = Path.Combine(root, "fallback-to-custom.json");
            var fallbackAutomation = new ResetCreditAutomation(fallbackPath, store);
            var sixHour = fallbackAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, now, 360);
            fallbackAutomation.RecordOutcome(sixHour, ResetCreditConsumeOutcome.NothingToReset, now);
            var fallbackFifteen = fallbackAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-14), 360);
            Equal(2, fallbackFifteen.Stage);
            fallbackAutomation.RecordOutcome(
                fallbackFifteen,
                ResetCreditConsumeOutcome.NothingToReset,
                expires.AddMinutes(-14));
            Equal(ResetCreditActionKind.None, fallbackAutomation.Evaluate(
                [credit], ResetCreditMode.UseBeforeExpiry, expires.AddMinutes(-13), 15).Action);
        });
        return Task.CompletedTask;
    }

    private static Task ResetCreditAutomationTransportRetry()
    {
        WithTempDirectory(root =>
        {
            var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.FromHours(7));
            var credit = ResetCredit("credit-retry", now.AddHours(2));
            var statePath = Path.Combine(root, "reset-credit-state.json");
            var store = new JsonStore();
            var automation = new ResetCreditAutomation(statePath, store);
            var first = automation.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, now);
            automation.RecordTransportFailure(first, now);

            var restarted = new ResetCreditAutomation(statePath, store);
            Equal(ResetCreditActionKind.None, restarted.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, now.AddSeconds(30)).Action);
            var retry = restarted.Evaluate([credit], ResetCreditMode.UseBeforeExpiry, now.AddMinutes(1));
            Equal(ResetCreditActionKind.Consume, retry.Action);
            Equal(first.IdempotencyKey, retry.IdempotencyKey);
            Equal(first.Stage, retry.Stage);
        });
        return Task.CompletedTask;
    }

    private static Task ResetCreditAutomationEligibility()
    {
        WithTempDirectory(root =>
        {
            var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.FromHours(7));
            var automation = new ResetCreditAutomation(Path.Combine(root, "state.json"), new JsonStore());
            var ineligible = new[]
            {
                ResetCredit("redeemed", now.AddHours(1)) with { Status = "redeemed" },
                ResetCredit("wrong-type", now.AddHours(1)) with { ResetType = "unknown" },
                ResetCredit("expired", now.AddMinutes(-1)),
                ResetCredit("no-expiry", now.AddHours(1)) with { ExpiresAtUnix = null, ExpiresAtLocal = null }
            };
            Equal(ResetCreditActionKind.None, automation.Evaluate(ineligible, ResetCreditMode.UseBeforeExpiry, now).Action);

            var later = ResetCredit("later", now.AddHours(3));
            var earlier = ResetCredit("earlier", now.AddHours(2));
            var decision = automation.Evaluate([later, earlier], ResetCreditMode.UseBeforeExpiry, now);
            Equal("earlier", decision.Credit!.Id);
            automation.RecordOutcome(decision, ResetCreditConsumeOutcome.Reset, now);
            Equal(ResetCreditActionKind.None, automation.Evaluate([earlier], ResetCreditMode.UseBeforeExpiry, now.AddMinutes(1)).Action);

            var notification = new ResetCreditAutomation(Path.Combine(root, "notify.json"), new JsonStore());
            Equal(ResetCreditActionKind.Notify, notification.Evaluate([earlier], ResetCreditMode.NotifyBeforeExpiry, now).Action);
            Equal(ResetCreditActionKind.None, notification.Evaluate([earlier], ResetCreditMode.NotifyBeforeExpiry, now.AddMinutes(1)).Action);
        });
        return Task.CompletedTask;
    }

    private static Task GitHubReleaseSelection()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "tag_name": "v0.3.1",
              "html_url": "https://github.com/159shadows357/CodexUsageTray/releases/tag/v0.3.1",
              "assets": [
                {
                  "name": "CodexUsageTray-0.3.1-win-arm64.zip",
                  "url": "https://api.github.com/repos/159shadows357/CodexUsageTray/releases/assets/1",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 100
                },
                {
                  "name": "CodexUsageTray-0.3.1-win-x64.zip",
                  "url": "https://api.github.com/repos/159shadows357/CodexUsageTray/releases/assets/2",
                  "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "size": 200
                }
              ]
            }
            """);
        var available = GitHubReleaseUpdater.ParseLatestRelease(document.RootElement, new Version(0, 3, 0));
        Equal(UpdateCheckStatus.UpdateAvailable, available.Status);
        Equal("CodexUsageTray-0.3.1-win-x64.zip", available.Release!.AssetName);
        Equal(200L, available.Release.AssetSize);

        var current = GitHubReleaseUpdater.ParseLatestRelease(document.RootElement, new Version(0, 3, 1));
        Equal(UpdateCheckStatus.UpToDate, current.Status);
        return Task.CompletedTask;
    }

    private static Task PublicGitHubRequestHasNoAuthorization()
    {
        using var request = GitHubReleaseUpdater.CreateGitHubRequest(
            HttpMethod.Get,
            new Uri("https://api.github.com/repos/159shadows357/CodexUsageTray/releases/latest"),
            "application/vnd.github+json");
        True(request.Headers.Authorization is null);
        True(request.Headers.UserAgent.Count == 0);
        True(request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json"));
        True(request.Headers.Contains("X-GitHub-Api-Version"));
        return Task.CompletedTask;
    }

    private static Task OpenAiStatusAggregation()
    {
        using var document = CreateOpenAiStatusSummary();
        var checkedAt = new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.FromHours(7));
        var result = OpenAiStatusClient.ParseComponents(document.RootElement, checkedAt);

        True(result.IsLive);
        Equal(checkedAt, result.CheckedAt);
        Equal(OpenAiServiceHealth.Operational, result.Codex);
        Equal(OpenAiServiceHealth.Operational, result.ChatGpt);
        return Task.CompletedTask;
    }

    private static Task OpenAiStatusOutageAndMissingComponents()
    {
        using (var outage = CreateOpenAiStatusSummary(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Codex API"] = "partial_outage",
            ["Conversations"] = "degraded_performance"
        }))
        {
            var result = OpenAiStatusClient.ParseComponents(outage.RootElement, DateTimeOffset.Now);
            Equal(OpenAiServiceHealth.PartialOutage, result.Codex);
            Equal(OpenAiServiceHealth.DegradedPerformance, result.ChatGpt);
        }

        using (var missing = CreateOpenAiStatusSummary(omit: "CLI"))
        {
            var result = OpenAiStatusClient.ParseComponents(missing.RootElement, DateTimeOffset.Now);
            Equal(OpenAiServiceHealth.Unavailable, result.Codex);
            Equal(OpenAiServiceHealth.Operational, result.ChatGpt);
        }

        using (var unknown = CreateOpenAiStatusSummary(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLI"] = "new_status_value"
        }))
        {
            var result = OpenAiStatusClient.ParseComponents(unknown.RootElement, DateTimeOffset.Now);
            Equal(OpenAiServiceHealth.Unavailable, result.Codex);
        }
        return Task.CompletedTask;
    }

    private static Task OpenAiStatusRequestIsPublic()
    {
        using var request = OpenAiStatusClient.CreateComponentsRequest();
        Equal(HttpMethod.Get, request.Method);
        Equal(OpenAiStatusClient.ComponentsUrl, request.RequestUri?.AbsoluteUri);
        True(request.Headers.Authorization is null);
        True(request.Headers.Accept.Any(value => value.MediaType == "application/json"));
        return Task.CompletedTask;
    }

    private static Task OpenAiStatusTransitions()
    {
        var loading = new OpenAiStatusSnapshot
        {
            IsLive = false,
            Codex = OpenAiServiceHealth.Loading,
            ChatGpt = OpenAiServiceHealth.Loading
        };
        var operational = new OpenAiStatusSnapshot
        {
            IsLive = true,
            Codex = OpenAiServiceHealth.Operational,
            ChatGpt = OpenAiServiceHealth.Operational
        };
        var incident = operational with { Codex = OpenAiServiceHealth.DegradedPerformance };

        Empty(TrayApplicationContext.OpenAiStatusTransitionMessages(loading, incident));
        var outageMessages = TrayApplicationContext.OpenAiStatusTransitionMessages(operational, incident);
        Equal(1, outageMessages.Count);
        Equal("Codex: Degraded performance", outageMessages[0]);
        var recoveryMessages = TrayApplicationContext.OpenAiStatusTransitionMessages(incident, operational);
        Equal(1, recoveryMessages.Count);
        Equal("Codex: Operational", recoveryMessages[0]);
        return Task.CompletedTask;
    }

    private static Task UpdaterRemovesOldStagingDirectories()
    {
        WithTempDirectory(root =>
        {
            var currentVersion = GitHubReleaseUpdater.CurrentVersion;
            var current = Path.Combine(root, $"{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}");
            var old = Path.Combine(root, "0.0.1");
            Directory.CreateDirectory(current);
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(current, "keep.txt"), "current");
            File.WriteAllText(Path.Combine(old, "delete.txt"), "old");

            using var updater = new GitHubReleaseUpdater(root);
            True(Directory.Exists(current));
            False(Directory.Exists(old));
        });
        return Task.CompletedTask;
    }

    private static Task GitHubReleaseUnsafeMetadata()
    {
        using var wrongHost = JsonDocument.Parse(
            """
            {
              "tag_name": "v0.3.1",
              "html_url": "https://github.com/159shadows357/CodexUsageTray/releases/tag/v0.3.1",
              "assets": [{
                "name": "CodexUsageTray-0.3.1-win-x64.zip",
                "url": "https://example.com/release.zip",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 100
              }]
            }
            """);
        Throws<InvalidDataException>(() =>
            GitHubReleaseUpdater.ParseLatestRelease(wrongHost.RootElement, new Version(0, 3, 0)));

        using var malformedTag = JsonDocument.Parse("""{"tag_name":"latest","assets":[]}""");
        Throws<InvalidDataException>(() =>
            GitHubReleaseUpdater.ParseLatestRelease(malformedTag.RootElement, new Version(0, 3, 0)));
        return Task.CompletedTask;
    }

    private static Task UpdatePackageVerification()
    {
        WithTempDirectory(root =>
        {
            var packageSource = Path.Combine(root, "source");
            Directory.CreateDirectory(packageSource);
            var setup = Path.Combine(packageSource, "CodexUsageTray-Setup.exe");
            var app = Path.Combine(packageSource, "CodexUsageTray.exe");
            File.WriteAllBytes(setup, Encoding.UTF8.GetBytes("setup-payload"));
            File.WriteAllBytes(app, Encoding.UTF8.GetBytes("app-payload"));
            var sums = string.Join(
                Environment.NewLine,
                $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup)))}  CodexUsageTray-Setup.exe",
                $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(app)))}  CodexUsageTray.exe");
            File.WriteAllText(Path.Combine(packageSource, "SHA256SUMS.txt"), sums);
            var zip = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(packageSource, zip);
            var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)))}";

            GitHubReleaseUpdater.VerifyFileDigest(zip, digest);
            var extractedSetup = GitHubReleaseUpdater.VerifyAndExtractPackage(
                zip,
                Path.Combine(root, "extracted"));
            True(File.Exists(extractedSetup));

            Throws<InvalidDataException>(() =>
                GitHubReleaseUpdater.VerifyFileDigest(
                    zip,
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        });
        return Task.CompletedTask;
    }

    private static Task UpdatePackageTraversal()
    {
        WithTempDirectory(root =>
        {
            var zip = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.exe");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("unsafe");
            }

            Throws<InvalidDataException>(() =>
                GitHubReleaseUpdater.VerifyAndExtractPackage(zip, Path.Combine(root, "extracted")));
            False(File.Exists(Path.Combine(root, "outside.exe")));
        });
        return Task.CompletedTask;
    }

    private static Task ApiCostCategories()
    {
        var result = ApiCostEstimator.Calculate(
            "gpt-5.6-sol",
            new TokenUsageCounters(200_000, 100_000, 20_000, 10_000));
        Equal(210_000L, result.TotalTokens);
        Equal(210_000L, result.PricedTokens);
        Equal(0L, result.UnpricedTokens);
        Equal(0.875m, result.EstimatedUsd);
        False(result.UsesProxyPricing);
        return Task.CompletedTask;
    }

    private static Task ApiCostLongContext()
    {
        var result = ApiCostEstimator.Calculate(
            "gpt-5.5",
            new TokenUsageCounters(300_000, 0, 0, 100_000));
        Equal(7.5m, result.EstimatedUsd);
        return Task.CompletedTask;
    }

    private static Task ApiCostProxyAndUnpriced()
    {
        var spark = ApiCostEstimator.Calculate(
            "gpt-5.3-codex-spark",
            new TokenUsageCounters(1_000_000, 0, 0, 0));
        Equal(1.75m, spark.EstimatedUsd);
        True(spark.UsesProxyPricing);

        var unknown = ApiCostEstimator.Calculate(
            "private-model",
            new TokenUsageCounters(900, 0, 0, 100));
        Equal(1_000L, unknown.UnpricedTokens);
        Equal(0m, unknown.EstimatedUsd);
        return Task.CompletedTask;
    }

    private static Task LocalUsageGroupsProjects()
    {
        WithTempDirectory(root =>
        {
            var project = Path.Combine(root, "sample-project");
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            var sessions = Path.Combine(root, "sessions", "2026", "07", "21");
            Directory.CreateDirectory(sessions);
            var session = Path.Combine(sessions, "rollout.jsonl");
            var first = UsageRecord(1_000, 100, 100);
            var second = UsageRecord(2_500, 1_100, 300);
            File.WriteAllLines(session,
            [
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = project } }),
                JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = Path.Combine(project, "src"), model = "gpt-5.6-sol" } }),
                JsonSerializer.Serialize(TokenCountRecord(first, first, "2026-07-21T08:00:00+07:00")),
                JsonSerializer.Serialize(TokenCountRecord(second, new { input_tokens = 1_500, cached_input_tokens = 1_000, cache_write_input_tokens = 0, output_tokens = 200 }, "2026-07-21T08:05:00+07:00")),
                JsonSerializer.Serialize(TokenCountRecord(second, new { input_tokens = 0, cached_input_tokens = 0, cache_write_input_tokens = 0, output_tokens = 0 }, "2026-07-21T08:06:00+07:00"))
            ]);

            var analyzer = new LocalUsageAnalyzer();
            var sessionsRoot = Path.Combine(root, "sessions");
            var estimate = analyzer.Analyze(sessionsRoot);
            True(estimate.IsAvailable);
            Equal(2_800L, estimate.TotalTokens);
            Equal(0.01655m, estimate.EstimatedUsd);
            Equal(1, estimate.ProjectCount);
            Equal("sample-project", estimate.Projects.Single().Name);
            Equal(2_800L, estimate.Projects.Single().TotalTokens);
            Equal(2_800L, estimate.DailyUsage.Single().TotalTokens);

            var third = UsageRecord(3_500, 1_500, 400);
            File.AppendAllLines(session,
            [
                JsonSerializer.Serialize(TokenCountRecord(third, new { input_tokens = 1_000, cached_input_tokens = 400, cache_write_input_tokens = 0, output_tokens = 100 }, "2026-07-22T08:00:00+07:00"))
            ]);
            var updated = analyzer.Analyze(sessionsRoot);
            Equal(3_900L, updated.TotalTokens);
            Equal(0.02275m, updated.EstimatedUsd);
            Equal(2, updated.DailyUsage.Count);
            var cached = analyzer.Analyze(sessionsRoot);
            Equal(updated.TotalTokens, cached.TotalTokens);
            Equal(updated.EstimatedUsd, cached.EstimatedUsd);
            Equal(updated.Projects.Single().Name, cached.Projects.Single().Name);
            Equal(updated.Projects.Single().TotalTokens, cached.Projects.Single().TotalTokens);
            True(updated.Projects.Single().DailyUsage.SequenceEqual(cached.Projects.Single().DailyUsage));
        });
        return Task.CompletedTask;
    }

    private static Task LocalUsageIndexPersists()
    {
        WithTempDirectory(root =>
        {
            var project = Path.Combine(root, "indexed-project");
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            var session = Path.Combine(sessions, "rollout-private-name.jsonl");
            var usage = UsageRecord(1_000, 200, 100);
            File.WriteAllLines(session,
            [
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = project } }),
                JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = project, model = "gpt-5.6-sol" } }),
                JsonSerializer.Serialize(TokenCountRecord(usage, usage, "2026-07-23T09:00:00+07:00"))
            ]);

            var indexPath = Path.Combine(root, "local-usage-index.json");
            var firstAnalyzer = new LocalUsageAnalyzer(indexPath);
            var first = firstAnalyzer.Analyze(sessions);
            Equal(1, firstAnalyzer.LastAnalysisStats.FilesRead);
            Equal(0, firstAnalyzer.LastAnalysisStats.FilesFromCache);
            True(File.Exists(indexPath));

            var serializedIndex = File.ReadAllText(indexPath);
            False(serializedIndex.Contains(session, StringComparison.OrdinalIgnoreCase));
            False(serializedIndex.Contains(project, StringComparison.OrdinalIgnoreCase));
            False(serializedIndex.Contains("rollout-private-name.jsonl", StringComparison.OrdinalIgnoreCase));

            var restartedAnalyzer = new LocalUsageAnalyzer(indexPath);
            var restarted = restartedAnalyzer.Analyze(sessions);
            Equal(0, restartedAnalyzer.LastAnalysisStats.FilesRead);
            Equal(1, restartedAnalyzer.LastAnalysisStats.FilesFromCache);
            Equal(first.TotalTokens, restarted.TotalTokens);
            Equal(first.EstimatedUsd, restarted.EstimatedUsd);
            Equal(first.DailyUsage.Single(), restarted.DailyUsage.Single());
        });
        return Task.CompletedTask;
    }

    private static Task UsageHistoryRanges()
    {
        var estimate = new ApiEquivalentEstimate
        {
            IsAvailable = true,
            TotalTokens = 650,
            PricedTokens = 600,
            UnpricedTokens = 50,
            EstimatedUsd = 6m,
            UndatedTokens = 50,
            DailyUsage =
            [
                new(new DateOnly(2026, 7, 1), 100, 100, 0, 1m),
                new(new DateOnly(2026, 7, 20), 200, 200, 0, 2m),
                new(new DateOnly(2026, 7, 23), 300, 300, 0, 3m)
            ],
            Projects =
            [
                new ProjectUsageEstimate("sample", 650, 6m, 50)
                {
                    DailyUsage =
                    [
                        new(new DateOnly(2026, 7, 1), 100, 100, 0, 1m),
                        new(new DateOnly(2026, 7, 20), 200, 200, 0, 2m),
                        new(new DateOnly(2026, 7, 23), 300, 300, 0, 3m)
                    ]
                }
            ]
        };
        var today = new DateOnly(2026, 7, 23);

        var seven = UsageHistoryCalculator.Summarize(estimate, UsageHistoryRange.SevenDays, today);
        Equal(7, seven.DailyUsage.Count);
        Equal(500L, seven.TotalTokens);
        Equal(5m, seven.EstimatedUsd);
        Equal(50L, seven.ExcludedUndatedTokens);
        Equal(500L, seven.Projects.Single().TotalTokens);

        var thirty = UsageHistoryCalculator.Summarize(estimate, UsageHistoryRange.ThirtyDays, today);
        Equal(600L, thirty.TotalTokens);
        Equal(6m, thirty.EstimatedUsd);

        var all = UsageHistoryCalculator.Summarize(estimate, UsageHistoryRange.AllTime, today);
        Equal(650L, all.TotalTokens);
        Equal(6m, all.EstimatedUsd);
        Equal(0L, all.ExcludedUndatedTokens);
        return Task.CompletedTask;
    }

    private static Task MalformedLocalUsageIndexRecovery()
    {
        WithTempDirectory(root =>
        {
            var project = Path.Combine(root, "recovery-project");
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            var usage = UsageRecord(100, 0, 10);
            File.WriteAllLines(Path.Combine(sessions, "rollout.jsonl"),
            [
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = project } }),
                JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = project, model = "gpt-5.3-codex" } }),
                JsonSerializer.Serialize(TokenCountRecord(usage, usage, "2026-07-23T10:00:00+07:00"))
            ]);

            var indexPath = Path.Combine(root, "local-usage-index.json");
            File.WriteAllText(indexPath, "{broken-json");
            var estimate = new LocalUsageAnalyzer(indexPath).Analyze(sessions);
            True(estimate.IsAvailable);
            Equal(110L, estimate.TotalTokens);
            using var rebuilt = JsonDocument.Parse(File.ReadAllText(indexPath));
            Equal(1, rebuilt.RootElement.GetProperty("version").GetInt32());
        });
        return Task.CompletedTask;
    }

    private static Task AdaptiveRefresh()
    {
        var reset = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.FromHours(7));
        var first = new UsageSnapshot
        {
            IsLive = true,
            Status = ConnectionStatus.Connected,
            Buckets = [new("codex:primary", "Weekly", "codex", "primary", 20, 80, 10080, reset.ToUnixTimeSeconds(), reset, null)]
        };
        var policy = new AdaptiveRefreshPolicy();
        Equal(TimeSpan.FromMinutes(1), policy.RecordSuccess(null, first, 2, false, PowerLineStatus.Online));
        Equal(TimeSpan.FromMinutes(2), policy.RecordSuccess(first, first, 2, false, PowerLineStatus.Online));
        Equal(TimeSpan.FromMinutes(2), policy.RecordSuccess(first, first, 2, false, PowerLineStatus.Online));
        Equal(TimeSpan.FromMinutes(5), policy.RecordSuccess(first, first, 2, false, PowerLineStatus.Online));

        var changed = first with { Buckets = [first.Buckets[0] with { UsedPercent = 21, RemainingPercent = 79 }] };
        Equal(TimeSpan.FromMinutes(1), policy.RecordSuccess(first, changed, 2, false, PowerLineStatus.Online));
        Equal(TimeSpan.FromMinutes(1), policy.RecordSuccess(changed, changed, 2, true, PowerLineStatus.Online));

        var batteryPolicy = new AdaptiveRefreshPolicy();
        Equal(TimeSpan.FromMinutes(5), batteryPolicy.RecordSuccess(first, first, 2, false, PowerLineStatus.Offline));

        var creditExpiry = reset.AddDays(2);
        var withCredit = first with
        {
            ResetCreditsAvailable = 1,
            ResetCredits = [ResetCredit("first-credit", creditExpiry)]
        };
        var replacement = withCredit with
        {
            ResetCredits = [ResetCredit("replacement-credit", creditExpiry.AddHours(-1))]
        };
        True(AdaptiveRefreshPolicy.HasUsageChanged(withCredit, replacement));
        return Task.CompletedTask;
    }

    private static Task ProtocolCompatibilityKnown()
    {
        using var current = ReadFixture("weekly-only.json");
        using var usage = ReadFixture("usage-summary.json");
        var currentReport = ProtocolCompatibility.Inspect(current.RootElement, usage.RootElement);
        Equal("rateLimitsByLimitId", currentReport.RateLimitShape);
        Equal("summary+daily", currentReport.UsageShape);
        Empty(currentReport.Warnings);

        using var legacy = ReadFixture("five-hour-weekly.json");
        var legacyReport = ProtocolCompatibility.Inspect(legacy.RootElement, null);
        Equal("rateLimits", legacyReport.RateLimitShape);
        Equal("unavailable", legacyReport.UsageShape);
        Empty(legacyReport.Warnings);
        return Task.CompletedTask;
    }

    private static Task ProtocolCompatibilityDrift()
    {
        using var limits = ReadFixture("schema-drift-rate-limits.json");
        using var usage = ReadFixture("schema-drift-usage.json");
        var report = ProtocolCompatibility.Inspect(limits.RootElement, usage.RootElement);
        Equal("unknown", report.RateLimitShape);
        Equal("unknown", report.UsageShape);
        Equal(2, report.Warnings.Count);
        Empty(RateLimitNormalizer.Normalize(limits.RootElement).ToList());
        False(RateLimitNormalizer.NormalizeUsage(usage.RootElement, new DateOnly(2026, 7, 23)).IsAvailable);
        return Task.CompletedTask;
    }

    private static object UsageRecord(long input, long cached, long output) => new
    {
        input_tokens = input,
        cached_input_tokens = cached,
        cache_write_input_tokens = 0,
        output_tokens = output
    };

    private static object TokenCountRecord(object total, object last, string? timestamp = null) => new
    {
        type = "event_msg",
        timestamp,
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = total,
                last_token_usage = last
            }
        }
    };

    private static Task TrayIconDoesNotLeak()
    {
        using (TrayIconRenderer.Create(ConnectionStatus.Connected, 74)) { }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gdiBefore = GetGuiResources(process.Handle, 0);
        var handlesBefore = process.HandleCount;

        for (var index = 0; index < 1_000; index++)
        {
            var style = (TrayIconStyle)(index % Enum.GetValues<TrayIconStyle>().Length);
            using var icon = TrayIconRenderer.Create(ConnectionStatus.Connected, index % 101, style);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var gdiAfter = GetGuiResources(process.Handle, 0);
        var handlesAfter = process.HandleCount;
        True(gdiAfter <= gdiBefore + 8, $"GDI objects grew from {gdiBefore} to {gdiAfter}.");
        True(handlesAfter <= handlesBefore + 16, $"Process handles grew from {handlesBefore} to {handlesAfter}.");
        return Task.CompletedTask;
    }

    private static Task TrayTooltipPreservesPercentage()
    {
        var reset = new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.FromHours(7));
        var bucket = new RateLimitBucket("codex:primary", "Weekly", "codex", "primary", 26, 74, 10080, reset.ToUnixTimeSeconds(), reset, null);
        var connected = new UsageSnapshot { Status = ConnectionStatus.Connected, Buckets = [bucket] };
        Equal("Codex Frontier · Weekly 74% left · Reset Jul 22 08:00", TrayTooltipFormatter.Format(connected));

        var longName = bucket with { Name = new string('W', 100) };
        var compact = TrayTooltipFormatter.Format(connected with { Buckets = [longName] });
        True(compact.Length <= 63);
        True(compact.Contains("74% left", StringComparison.Ordinal));

        var stale = TrayTooltipFormatter.Format(connected with { Status = ConnectionStatus.DataStale });
        True(stale.Contains("74% left", StringComparison.Ordinal));
        True(stale.EndsWith(" · Stale", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static int CountColor(Bitmap bitmap, Color target) => CountPixels(bitmap, color => color.ToArgb() == target.ToArgb());

    private static int CountPixels(Bitmap bitmap, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (predicate(bitmap.GetPixel(x, y))) count++;
            }
        }
        return count;
    }

    private static int CountPixelsIn(Bitmap bitmap, Rectangle bounds, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (predicate(bitmap.GetPixel(x, y))) count++;
            }
        }
        return count;
    }

    private static int CountColorRuns(Bitmap bitmap, int y, Color target)
    {
        var runs = 0;
        var inRun = false;
        for (var x = 0; x < bitmap.Width; x++)
        {
            var matches = bitmap.GetPixel(x, y).ToArgb() == target.ToArgb();
            if (matches && !inRun) runs++;
            inRun = matches;
        }
        return runs;
    }

    private static IReadOnlyList<RateLimitBucket> Normalize(string file)
    {
        using var document = ReadFixture(file);
        return RateLimitNormalizer.Normalize(document.RootElement);
    }

    private static JsonDocument ReadFixture(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));

    private static RateLimitBucket Bucket(int remaining, long reset) =>
        new("codex:primary", "Weekly", "codex", "primary", 100 - remaining, remaining, 10080, reset, DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime(), null);

    private static RateLimitResetCredit ResetCredit(string id, DateTimeOffset expiresAt) =>
        new(
            id,
            "codexRateLimits",
            "available",
            expiresAt.AddDays(-1).ToUnixTimeSeconds(),
            expiresAt.AddDays(-1),
            expiresAt.ToUnixTimeSeconds(),
            expiresAt,
            "Full reset",
            null);

    private static void WithTempDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexUsageTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static Task ModernContextMenuStyle()
    {
        using var menu = new ModernContextMenuStrip();
        var child = new ToolStripMenuItem("Child");
        child.DropDownItems.Add("Nested");
        menu.Items.Add(child);

        False(menu.ShowImageMargin, "Image margin should be hidden.");
        Equal(Color.FromArgb(31, 31, 31), menu.BackColor);
        Equal(Color.FromArgb(246, 246, 246), menu.ForeColor);
        True(menu.Renderer is ModernToolStripRenderer, "Root menu renderer should be modern.");
        True(child.DropDown.Renderer is ModernToolStripRenderer, "Submenu renderer should be modern.");
        True(child.Padding.Left >= 10 && child.Padding.Top >= 6, $"Expected padded menu item, actual {child.Padding}.");
        return Task.CompletedTask;
    }

    private static Task UsagePopupHasNoScrollbar()
    {
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.FromHours(7));
        var snapshot = new UsageSnapshot
        {
            IsLive = true,
            Status = ConnectionStatus.Connected,
            PlanType = "prolite",
            Buckets =
            [
                new("codex:primary", "Weekly", "codex", "primary", 20, 80, 10080, now.AddDays(7).ToUnixTimeSeconds(), now.AddDays(7), null),
                new("spark:primary", "GPT-5.3-Codex-Spark", "spark", "primary", 5, 95, 10080, now.AddDays(7).ToUnixTimeSeconds(), now.AddDays(7), null)
            ],
            ResetCreditsAvailable = 1,
            ResetCredits = [ResetCredit("popup-credit", now.AddDays(2))]
        };

        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateSnapshot(snapshot);
            var content = popup.Controls.OfType<FlowLayoutPanel>().Single();
            False(content.AutoScroll, $"{style} enabled AutoScroll.");
        }

        return Task.CompletedTask;
    }

    private static Task UsagePopupDeactivatePolicy()
    {
        var bounds = new Rectangle(100, 100, 430, 700);
        var inside = new Point(200, 200);
        var outside = new Point(20, 20);
        var ready = TimeSpan.FromSeconds(1);

        False(UsagePopupForm.ShouldHideOnDeactivate(false, true, false, ready, MouseButtons.Left, bounds, outside));
        False(UsagePopupForm.ShouldHideOnDeactivate(true, false, false, ready, MouseButtons.Left, bounds, outside));
        False(UsagePopupForm.ShouldHideOnDeactivate(true, true, true, ready, MouseButtons.Left, bounds, outside));
        False(UsagePopupForm.ShouldHideOnDeactivate(true, true, false, TimeSpan.FromMilliseconds(200), MouseButtons.Left, bounds, outside));
        False(UsagePopupForm.ShouldHideOnDeactivate(true, true, false, ready, MouseButtons.None, bounds, outside));
        False(UsagePopupForm.ShouldHideOnDeactivate(true, true, false, ready, MouseButtons.Left, bounds, inside));
        True(UsagePopupForm.ShouldHideOnDeactivate(true, true, false, ready, MouseButtons.Left, bounds, outside));
        return Task.CompletedTask;
    }

    private static Task UsagePopupMacCompactBuckets()
    {
        True(UsagePopupForm.ShouldUseCompactMacBuckets(768, 3, false));
        True(UsagePopupForm.ShouldUseCompactMacBuckets(768, 2, true));
        False(UsagePopupForm.ShouldUseCompactMacBuckets(1080, 3, false));
        return Task.CompletedTask;
    }

    private static Task UsagePopupShowsOpenAiStatus()
    {
        var status = new OpenAiStatusSnapshot
        {
            IsLive = true,
            Codex = OpenAiServiceHealth.Operational,
            ChatGpt = OpenAiServiceHealth.DegradedPerformance
        };

        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateOpenAiStatus(status);
            var text = Descendants(popup)
                .Select(control => control.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            True(text.Contains("Codex"), $"{style} did not show the Codex label.");
            True(text.Contains("ChatGPT"), $"{style} did not show the ChatGPT label.");
            True(text.Contains("Operational"), $"{style} did not show the Codex status value.");
            True(text.Contains("Degraded performance"), $"{style} did not show the ChatGPT status value.");
            Equal(2, Descendants(popup).OfType<OpenAiStatusGraph>().Count());
        }

        return Task.CompletedTask;
    }

    private static Task UsagePopupIdentifiesFrontier()
    {
        var now = DateTimeOffset.Now;
        var status = new OpenAiStatusSnapshot
        {
            CheckedAt = now,
            IsLive = true,
            Codex = OpenAiServiceHealth.Operational,
            ChatGpt = OpenAiServiceHealth.Operational
        };
        var snapshot = new UsageSnapshot
        {
            UpdatedAt = now.AddMinutes(-2),
            IsLive = true,
            Status = ConnectionStatus.Connected,
            Buckets =
            [
                new("codex_bengalfox:primary", "GPT-5.3-Codex-Spark · 5h", "codex_bengalfox", "primary", 46, 54, 300, now.AddHours(5).ToUnixTimeSeconds(), now.AddHours(5), null),
                new("codex_bengalfox:secondary", "GPT-5.3-Codex-Spark · Weekly", "codex_bengalfox", "secondary", 20, 80, 10080, now.AddDays(7).ToUnixTimeSeconds(), now.AddDays(7), null),
                new("codex:primary", "Weekly", "codex", "primary", 9, 91, 10080, now.AddDays(7).ToUnixTimeSeconds(), now.AddDays(7), null)
            ]
        };

        foreach (var style in Enum.GetValues<PopupStyle>())
        {
            using var popup = new UsagePopupForm(popupStyle: style);
            popup.UpdateOpenAiStatus(status);
            popup.UpdateSnapshot(snapshot);

            var text = Descendants(popup)
                .Select(control => control.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            True(text.Contains("Tray · Frontier · Weekly"), $"{style} did not identify the Frontier tray bucket.");
            True(text.Contains("5h · GPT-5.3-Codex-Spark"), $"{style} did not preserve the Spark 5h prefix.");
            True(text.Contains("Weekly · GPT-5.3-Codex-Spark"), $"{style} did not preserve the Spark weekly prefix.");
            True(text.Any(value => value.StartsWith("Quota updated ", StringComparison.Ordinal)), $"{style} did not label quota freshness.");
            True(text.Any(value => value.StartsWith("OpenAI status · checked ", StringComparison.Ordinal)), $"{style} did not label status freshness.");

            var topLevel = popup.Controls.OfType<FlowLayoutPanel>().Single().Controls.Cast<Control>().ToList();
            var trayIndex = topLevel.FindIndex(control => control.AccessibleName?.StartsWith("Tray indicator, ", StringComparison.Ordinal) == true);
            var firstQuotaIndex = topLevel.FindIndex(control => control.AccessibleName?.Contains("% left", StringComparison.Ordinal) == true);
            var statusIndex = topLevel.FindIndex(control => control.AccessibleName?.StartsWith("OpenAI status, ", StringComparison.Ordinal) == true);
            True(trayIndex >= 0 && trayIndex == firstQuotaIndex, $"{style} did not place the Frontier tray bucket first.");
            True(statusIndex > trayIndex, $"{style} did not place quota before OpenAI status.");
        }

        return Task.CompletedTask;
    }

    private static JsonDocument CreateOpenAiStatusSummary(
        IReadOnlyDictionary<string, string>? overrides = null,
        string? omit = null)
    {
        string[] componentNames =
        [
            "Codex Web",
            "VS Code extension",
            "Codex API",
            "CLI",
            "Sites",
            "Codex in ChatGPT Desktop",
            "Login",
            "Conversations",
            "Search",
            "Sora",
            "GPTs",
            "ChatGPT Atlas",
            "Deep Research",
            "Image Generation",
            "Agent",
            "Voice mode",
            "Connectors/Apps",
            "ChatGPT Work",
            "File uploads"
        ];
        var components = componentNames
            .Where(name => !string.Equals(name, omit, StringComparison.OrdinalIgnoreCase))
            .Select((name, index) => new
            {
                id = $"test-{index}",
                name,
                status = overrides is not null && overrides.TryGetValue(name, out var value)
                    ? value
                    : "operational"
            });
        return JsonDocument.Parse(JsonSerializer.Serialize(new { components }));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void True(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
    }

    private static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false.");
    private static void Empty<T>(IReadOnlyCollection<T> values) => Equal(0, values.Count);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }
    private static void NotEqual<T>(T left, T right)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException($"Expected values to differ, both were '{left}'.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
}
