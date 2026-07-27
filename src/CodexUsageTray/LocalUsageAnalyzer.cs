using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray;

public readonly record struct TokenUsageCounters(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens)
{
    public long TotalTokens => Math.Max(0, InputTokens) + Math.Max(0, OutputTokens);

    public TokenUsageCounters DeltaFrom(TokenUsageCounters previous) => new(
        Math.Max(0, InputTokens - previous.InputTokens),
        Math.Max(0, CachedInputTokens - previous.CachedInputTokens),
        Math.Max(0, CacheWriteInputTokens - previous.CacheWriteInputTokens),
        Math.Max(0, OutputTokens - previous.OutputTokens));
}

public readonly record struct ApiCostCalculation(
    long TotalTokens,
    long PricedTokens,
    long UnpricedTokens,
    decimal EstimatedUsd,
    bool UsesProxyPricing);

public static class ApiCostEstimator
{
    public const string PricingReferenceDate = "2026-07-21";
    private const decimal TokensPerMillion = 1_000_000m;
    private const long LongContextThreshold = 272_000;

    public static ApiCostCalculation Calculate(string? model, TokenUsageCounters usage)
    {
        var totalTokens = usage.TotalTokens;
        if (totalTokens == 0)
        {
            return new(0, 0, 0, 0, false);
        }

        if (!TryResolvePricing(model, out var pricing))
        {
            return new(totalTokens, 0, totalTokens, 0, false);
        }

        var inputTokens = Math.Max(0, usage.InputTokens);
        var cachedTokens = Math.Min(inputTokens, Math.Max(0, usage.CachedInputTokens));
        var cacheWriteTokens = Math.Min(inputTokens - cachedTokens, Math.Max(0, usage.CacheWriteInputTokens));
        var uncachedTokens = inputTokens - cachedTokens - cacheWriteTokens;
        var outputTokens = Math.Max(0, usage.OutputTokens);
        var isLongContext = pricing.HasLongContextMultiplier && inputTokens > LongContextThreshold;
        var inputMultiplier = isLongContext ? 2m : 1m;
        var outputMultiplier = isLongContext ? 1.5m : 1m;

        var estimatedUsd =
            (uncachedTokens * pricing.InputPerMillion * inputMultiplier / TokensPerMillion) +
            (cachedTokens * pricing.CachedInputPerMillion * inputMultiplier / TokensPerMillion) +
            (cacheWriteTokens * pricing.InputPerMillion * pricing.CacheWriteMultiplier * inputMultiplier / TokensPerMillion) +
            (outputTokens * pricing.OutputPerMillion * outputMultiplier / TokensPerMillion);

        return new(totalTokens, totalTokens, 0, estimatedUsd, pricing.IsProxy);
    }

    private static bool TryResolvePricing(string? model, out ModelPricing pricing)
    {
        pricing = default;
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalized = model.Trim().ToLowerInvariant();
        if (normalized.StartsWith("gpt-5.6-terra", StringComparison.Ordinal))
        {
            pricing = new(2.50m, 0.25m, 15m, 1.25m, true, false);
            return true;
        }
        if (normalized.StartsWith("gpt-5.6-luna", StringComparison.Ordinal))
        {
            pricing = new(1m, 0.10m, 6m, 1.25m, true, false);
            return true;
        }
        if (normalized is "gpt-5.6" || normalized.StartsWith("gpt-5.6-sol", StringComparison.Ordinal))
        {
            pricing = new(5m, 0.50m, 30m, 1.25m, true, false);
            return true;
        }
        if (normalized.StartsWith("gpt-5.5", StringComparison.Ordinal))
        {
            pricing = new(5m, 0.50m, 30m, 1m, true, false);
            return true;
        }
        if (normalized.StartsWith("gpt-5.3-codex-spark", StringComparison.Ordinal))
        {
            pricing = new(1.75m, 0.175m, 14m, 1m, false, true);
            return true;
        }
        if (normalized.StartsWith("gpt-5.3-codex", StringComparison.Ordinal))
        {
            pricing = new(1.75m, 0.175m, 14m, 1m, false, false);
            return true;
        }

        return false;
    }

    private readonly record struct ModelPricing(
        decimal InputPerMillion,
        decimal CachedInputPerMillion,
        decimal OutputPerMillion,
        decimal CacheWriteMultiplier,
        bool HasLongContextMultiplier,
        bool IsProxy);
}

public sealed class LocalUsageAnalyzer
{
    private const int IndexVersion = 1;
    private readonly Dictionary<string, ProjectIdentity> _projectCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalUsageIndexEntry> _sessionCache = new(StringComparer.Ordinal);
    private readonly string? _indexPath;
    private readonly JsonStore _store;
    private bool _indexLoaded;

    public LocalUsageAnalyzer(string? indexPath = null, JsonStore? store = null)
    {
        _indexPath = indexPath;
        _store = store ?? new JsonStore();
    }

    internal LocalUsageAnalysisStats LastAnalysisStats { get; private set; } = new(0, 0, 0, 0);

    public ApiEquivalentEstimate Analyze(CancellationToken cancellationToken = default) => Analyze(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"),
        cancellationToken);

    public ApiEquivalentEstimate Analyze(string sessionsRoot, CancellationToken cancellationToken = default)
    {
        EnsureIndexLoaded();
        if (!Directory.Exists(sessionsRoot))
        {
            LastAnalysisStats = new(0, 0, 0, _sessionCache.Count);
            return new ApiEquivalentEstimate();
        }

        string[] sessionFiles;
        try
        {
            sessionFiles = Directory.GetFiles(
                sessionsRoot,
                "*.jsonl",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastAnalysisStats = new(0, 0, 0, _sessionCache.Count);
            return new ApiEquivalentEstimate();
        }

        var aggregate = new UsageAggregate();
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        var filesRead = 0;
        var filesFromCache = 0;
        var indexDirty = false;
        foreach (var sessionFile in sessionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionKey = HashPath(sessionFile);
            activeKeys.Add(sessionKey);
            _sessionCache.TryGetValue(sessionKey, out var cached);
            LocalSessionContribution? contribution = null;
            try
            {
                var file = new FileInfo(sessionFile);
                if (cached is not null && cached.Length == file.Length && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                {
                    contribution = cached.Contribution;
                    filesFromCache++;
                }
                else
                {
                    filesRead++;
                    contribution = AnalyzeSession(sessionFile, cancellationToken);
                    if (contribution is not null)
                    {
                        _sessionCache[sessionKey] = new LocalUsageIndexEntry(
                            sessionKey,
                            file.Length,
                            file.LastWriteTimeUtc,
                            contribution);
                        indexDirty = true;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                contribution = cached?.Contribution;
            }

            if (contribution is not null)
            {
                aggregate.Merge(contribution);
            }
        }

        foreach (var staleKey in _sessionCache.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _sessionCache.Remove(staleKey);
            indexDirty = true;
        }

        if (indexDirty)
        {
            SaveIndex();
        }

        var duplicateNames = aggregate.Projects.Values
            .GroupBy(project => project.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectEstimates = aggregate.Projects.Values
            .OrderByDescending(project => project.EstimatedUsd)
            .ThenByDescending(project => project.TotalTokens)
            .Select(project => new ProjectUsageEstimate(
                duplicateNames.Contains(project.Identity.Name)
                    ? $"{project.Identity.Name} ({project.Identity.ParentName})"
                    : project.Identity.Name,
                project.TotalTokens,
                project.EstimatedUsd,
                project.UnpricedTokens)
            {
                DailyUsage = project.DailyUsage
                    .OrderBy(item => item.Date)
                    .Select(item => item.ToEstimate())
                    .ToList()
            })
            .ToList();

        LastAnalysisStats = new(sessionFiles.Length, filesRead, filesFromCache, _sessionCache.Count);
        return new ApiEquivalentEstimate
        {
            IsAvailable = aggregate.TotalTokens > 0,
            TotalTokens = aggregate.TotalTokens,
            PricedTokens = aggregate.PricedTokens,
            UnpricedTokens = aggregate.UnpricedTokens,
            EstimatedUsd = aggregate.EstimatedUsd,
            ProjectCount = aggregate.Projects.Count,
            Projects = projectEstimates,
            DailyUsage = aggregate.DailyUsage
                .OrderBy(item => item.Date)
                .Select(item => item.ToEstimate())
                .ToList(),
            UndatedTokens = aggregate.UndatedTokens,
            UsesProxyPricing = aggregate.UsesProxyPricing
        };
    }

    private LocalSessionContribution? AnalyzeSession(string sessionFile, CancellationToken cancellationToken)
    {
        try
        {
            var aggregate = new UsageAggregate();
            using var stream = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? currentWorkingDirectory = null;
            string? currentModel = null;
            TokenUsageCounters? previousTotal = null;
            var lineNumber = 0;

            while (reader.ReadLine() is { } line)
            {
                if (++lineNumber % 256 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (!line.Contains("\"session_meta\"", StringComparison.Ordinal) &&
                    !line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
                    !line.Contains("\"token_count\"", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    var recordType = ReadString(root, "type");
                    var localDate = ReadLocalDate(root);
                    if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (recordType == "session_meta")
                    {
                        currentWorkingDirectory = ReadString(payload, "cwd") ?? currentWorkingDirectory;
                        continue;
                    }
                    if (recordType == "turn_context")
                    {
                        currentWorkingDirectory = ReadString(payload, "cwd") ?? currentWorkingDirectory;
                        currentModel = ReadString(payload, "model") ?? currentModel;
                        continue;
                    }
                    if (ReadString(payload, "type") != "token_count" ||
                        !payload.TryGetProperty("info", out var info) ||
                        info.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var lastUsage = ReadUsage(info, "last_token_usage");
                    var cumulativeUsage = ReadUsage(info, "total_token_usage");
                    TokenUsageCounters? usage = null;
                    if (cumulativeUsage is { } cumulative)
                    {
                        usage = previousTotal is not { } previous
                            ? cumulative
                            : cumulative.TotalTokens >= previous.TotalTokens
                                ? cumulative.DeltaFrom(previous)
                                : lastUsage ?? cumulative;
                        previousTotal = cumulative;
                    }
                    else if (lastUsage is not null)
                    {
                        usage = lastUsage;
                    }

                    if (usage is not { TotalTokens: > 0 } tokenUsage)
                    {
                        continue;
                    }

                    var cost = ApiCostEstimator.Calculate(currentModel, tokenUsage);
                    var identity = ResolveProject(currentWorkingDirectory);
                    aggregate.Add(identity, cost, localDate);
                }
            }

            return aggregate.ToContribution();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A live session can rotate while it is being scanned. The next refresh will retry it.
            return null;
        }
    }

    private void EnsureIndexLoaded()
    {
        if (_indexLoaded)
        {
            return;
        }

        _indexLoaded = true;
        if (string.IsNullOrWhiteSpace(_indexPath))
        {
            return;
        }

        var index = _store.ReadValue<LocalUsageIndex>(_indexPath);
        if (index is null || index.Version != IndexVersion)
        {
            return;
        }

        foreach (var entry in index.Sessions)
        {
            if (entry.Key.Length == 64 && entry.Contribution is not null)
            {
                _sessionCache[entry.Key] = entry;
            }
        }
    }

    private void SaveIndex()
    {
        if (string.IsNullOrWhiteSpace(_indexPath))
        {
            return;
        }

        try
        {
            _store.Write(_indexPath, new LocalUsageIndex
            {
                Version = IndexVersion,
                Sessions = _sessionCache.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList()
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The aggregate remains usable in memory. A later refresh can retry the index write.
        }
    }

    private ProjectIdentity ResolveProject(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new("unknown", "Unknown", "local history");
        }

        if (_projectCache.TryGetValue(workingDirectory, out var cached))
        {
            return cached;
        }

        string normalized;
        try { normalized = Path.GetFullPath(workingDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var fallbackName = workingDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            return new(HashText(workingDirectory), string.IsNullOrWhiteSpace(fallbackName) ? "Unknown" : fallbackName, "unknown path");
        }

        var projectRoot = normalized;
        try
        {
            var current = new DirectoryInfo(normalized);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                {
                    projectRoot = current.FullName;
                    break;
                }
                current = current.Parent;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            projectRoot = normalized;
        }

        var root = new DirectoryInfo(projectRoot);
        var name = string.IsNullOrWhiteSpace(root.Name) ? "Drive root" : root.Name;
        var parentName = root.Parent?.Name ?? root.Root.Name.TrimEnd(Path.DirectorySeparatorChar);
        var identity = new ProjectIdentity(HashPath(projectRoot), name, parentName);
        _projectCache[workingDirectory] = identity;
        return identity;
    }

    private static DateOnly? ReadLocalDate(JsonElement root)
    {
        var timestamp = ReadString(root, "timestamp");
        if (string.IsNullOrWhiteSpace(timestamp) ||
            !DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return null;
        }

        return DateOnly.FromDateTime(parsed.ToLocalTime().DateTime);
    }

    private static string HashPath(string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = path.ToUpperInvariant();
        }

        return HashText(normalized);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static TokenUsageCounters? ReadUsage(JsonElement info, string propertyName)
    {
        if (!info.TryGetProperty(propertyName, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new TokenUsageCounters(
            ReadInt64(usage, "input_tokens"),
            ReadInt64(usage, "cached_input_tokens"),
            ReadInt64(usage, "cache_write_input_tokens"),
            ReadInt64(usage, "output_tokens"));
    }

    private static long ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number) ? Math.Max(0, number) : 0;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class ProjectAccumulator(ProjectIdentity identity)
    {
        public ProjectIdentity Identity { get; } = identity;
        public List<DailyAccumulator> DailyUsage => _dailyUsage.Values.ToList();
        public long TotalTokens { get; private set; }
        public long UnpricedTokens { get; private set; }
        public decimal EstimatedUsd { get; private set; }
        private readonly Dictionary<DateOnly, DailyAccumulator> _dailyUsage = [];

        public void Add(ApiCostCalculation calculation, DateOnly? date)
        {
            TotalTokens += calculation.TotalTokens;
            UnpricedTokens += calculation.UnpricedTokens;
            EstimatedUsd += calculation.EstimatedUsd;
            if (date is { } localDate)
            {
                GetDaily(localDate).Add(calculation);
            }
        }

        public void Merge(LocalProjectContribution contribution)
        {
            TotalTokens += contribution.TotalTokens;
            UnpricedTokens += contribution.UnpricedTokens;
            EstimatedUsd += contribution.EstimatedUsd;
            foreach (var daily in contribution.DailyUsage)
            {
                GetDaily(daily.Date).Merge(daily);
            }
        }

        public LocalProjectContribution ToContribution() => new(
            Identity,
            TotalTokens,
            UnpricedTokens,
            EstimatedUsd,
            _dailyUsage.Values.OrderBy(item => item.Date).Select(item => item.ToEstimate()).ToList());

        private DailyAccumulator GetDaily(DateOnly date)
        {
            if (!_dailyUsage.TryGetValue(date, out var daily))
            {
                daily = new DailyAccumulator(date);
                _dailyUsage.Add(date, daily);
            }
            return daily;
        }
    }

    private sealed class UsageAggregate
    {
        public Dictionary<string, ProjectAccumulator> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DailyAccumulator> DailyUsage => _dailyUsage.Values.ToList();
        public long TotalTokens { get; private set; }
        public long PricedTokens { get; private set; }
        public long UnpricedTokens { get; private set; }
        public decimal EstimatedUsd { get; private set; }
        public long UndatedTokens { get; private set; }
        public bool UsesProxyPricing { get; private set; }
        private readonly Dictionary<DateOnly, DailyAccumulator> _dailyUsage = [];

        public void Add(ProjectIdentity identity, ApiCostCalculation calculation, DateOnly? date)
        {
            if (!Projects.TryGetValue(identity.Key, out var project))
            {
                project = new ProjectAccumulator(identity);
                Projects.Add(identity.Key, project);
            }
            project.Add(calculation, date);
            TotalTokens += calculation.TotalTokens;
            PricedTokens += calculation.PricedTokens;
            UnpricedTokens += calculation.UnpricedTokens;
            EstimatedUsd += calculation.EstimatedUsd;
            UsesProxyPricing |= calculation.UsesProxyPricing;
            if (date is { } localDate)
            {
                GetDaily(localDate).Add(calculation);
            }
            else
            {
                UndatedTokens += calculation.TotalTokens;
            }
        }

        public void Merge(LocalSessionContribution contribution)
        {
            foreach (var projectContribution in contribution.Projects)
            {
                if (!Projects.TryGetValue(projectContribution.Identity.Key, out var project))
                {
                    project = new ProjectAccumulator(projectContribution.Identity);
                    Projects.Add(projectContribution.Identity.Key, project);
                }
                project.Merge(projectContribution);
            }
            TotalTokens += contribution.TotalTokens;
            PricedTokens += contribution.PricedTokens;
            UnpricedTokens += contribution.UnpricedTokens;
            EstimatedUsd += contribution.EstimatedUsd;
            UndatedTokens += contribution.UndatedTokens;
            UsesProxyPricing |= contribution.UsesProxyPricing;
            foreach (var daily in contribution.DailyUsage)
            {
                GetDaily(daily.Date).Merge(daily);
            }
        }

        public LocalSessionContribution ToContribution() => new(
            Projects.Values.Select(project => project.ToContribution()).ToList(),
            _dailyUsage.Values.OrderBy(item => item.Date).Select(item => item.ToEstimate()).ToList(),
            TotalTokens,
            PricedTokens,
            UnpricedTokens,
            EstimatedUsd,
            UndatedTokens,
            UsesProxyPricing);

        private DailyAccumulator GetDaily(DateOnly date)
        {
            if (!_dailyUsage.TryGetValue(date, out var daily))
            {
                daily = new DailyAccumulator(date);
                _dailyUsage.Add(date, daily);
            }
            return daily;
        }
    }

    private sealed class DailyAccumulator(DateOnly date)
    {
        public DateOnly Date { get; } = date;
        private long TotalTokens { get; set; }
        private long PricedTokens { get; set; }
        private long UnpricedTokens { get; set; }
        private decimal EstimatedUsd { get; set; }

        public void Add(ApiCostCalculation calculation)
        {
            TotalTokens += calculation.TotalTokens;
            PricedTokens += calculation.PricedTokens;
            UnpricedTokens += calculation.UnpricedTokens;
            EstimatedUsd += calculation.EstimatedUsd;
        }

        public void Merge(DailyUsageEstimate estimate)
        {
            TotalTokens += estimate.TotalTokens;
            PricedTokens += estimate.PricedTokens;
            UnpricedTokens += estimate.UnpricedTokens;
            EstimatedUsd += estimate.EstimatedUsd;
        }

        public DailyUsageEstimate ToEstimate() => new(Date, TotalTokens, PricedTokens, UnpricedTokens, EstimatedUsd);
    }

    internal sealed record ProjectIdentity(string Key, string Name, string ParentName);
}

internal sealed record LocalUsageAnalysisStats(
    int SessionFiles,
    int FilesRead,
    int FilesFromCache,
    int IndexEntries);

internal sealed record LocalProjectContribution(
    LocalUsageAnalyzer.ProjectIdentity Identity,
    long TotalTokens,
    long UnpricedTokens,
    decimal EstimatedUsd,
    List<DailyUsageEstimate> DailyUsage);

internal sealed record LocalSessionContribution(
    List<LocalProjectContribution> Projects,
    List<DailyUsageEstimate> DailyUsage,
    long TotalTokens,
    long PricedTokens,
    long UnpricedTokens,
    decimal EstimatedUsd,
    long UndatedTokens,
    bool UsesProxyPricing);

internal sealed record LocalUsageIndexEntry(
    string Key,
    long Length,
    DateTime LastWriteTimeUtc,
    LocalSessionContribution Contribution);

internal sealed record LocalUsageIndex
{
    public int Version { get; init; }
    public List<LocalUsageIndexEntry> Sessions { get; init; } = [];
}
