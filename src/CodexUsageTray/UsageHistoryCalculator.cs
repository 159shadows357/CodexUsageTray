namespace CodexUsageTray;

public enum UsageHistoryRange
{
    SevenDays,
    ThirtyDays,
    AllTime
}

public sealed record UsageHistorySummary
{
    public UsageHistoryRange Range { get; init; }
    public long TotalTokens { get; init; }
    public long PricedTokens { get; init; }
    public long UnpricedTokens { get; init; }
    public decimal EstimatedUsd { get; init; }
    public long ExcludedUndatedTokens { get; init; }
    public List<DailyUsageEstimate> DailyUsage { get; init; } = [];
    public List<ProjectUsageEstimate> Projects { get; init; } = [];
}

public static class UsageHistoryCalculator
{
    public static UsageHistorySummary Summarize(
        ApiEquivalentEstimate estimate,
        UsageHistoryRange range,
        DateOnly localToday)
    {
        if (range == UsageHistoryRange.AllTime)
        {
            return new UsageHistorySummary
            {
                Range = range,
                TotalTokens = estimate.TotalTokens,
                PricedTokens = estimate.PricedTokens,
                UnpricedTokens = estimate.UnpricedTokens,
                EstimatedUsd = estimate.EstimatedUsd,
                DailyUsage = estimate.DailyUsage.OrderBy(item => item.Date).ToList(),
                Projects = estimate.Projects
                    .OrderByDescending(project => project.EstimatedUsd)
                    .ThenByDescending(project => project.TotalTokens)
                    .ToList()
            };
        }

        var days = range == UsageHistoryRange.SevenDays ? 7 : 30;
        var start = localToday.AddDays(-(days - 1));
        var selectedDaily = estimate.DailyUsage
            .Where(item => item.Date >= start && item.Date <= localToday)
            .ToDictionary(item => item.Date);
        var daily = Enumerable.Range(0, days)
            .Select(offset =>
            {
                var date = start.AddDays(offset);
                return selectedDaily.TryGetValue(date, out var usage)
                    ? usage
                    : new DailyUsageEstimate(date, 0, 0, 0, 0);
            })
            .ToList();

        var projects = estimate.Projects
            .Select(project => SummarizeProject(project, start, localToday))
            .Where(project => project.TotalTokens > 0)
            .OrderByDescending(project => project.EstimatedUsd)
            .ThenByDescending(project => project.TotalTokens)
            .ToList();

        return new UsageHistorySummary
        {
            Range = range,
            TotalTokens = daily.Sum(item => item.TotalTokens),
            PricedTokens = daily.Sum(item => item.PricedTokens),
            UnpricedTokens = daily.Sum(item => item.UnpricedTokens),
            EstimatedUsd = daily.Sum(item => item.EstimatedUsd),
            ExcludedUndatedTokens = estimate.UndatedTokens,
            DailyUsage = daily,
            Projects = projects
        };
    }

    private static ProjectUsageEstimate SummarizeProject(
        ProjectUsageEstimate project,
        DateOnly start,
        DateOnly end)
    {
        var daily = project.DailyUsage
            .Where(item => item.Date >= start && item.Date <= end)
            .OrderBy(item => item.Date)
            .ToList();
        return new ProjectUsageEstimate(
            project.Name,
            daily.Sum(item => item.TotalTokens),
            daily.Sum(item => item.EstimatedUsd),
            daily.Sum(item => item.UnpricedTokens))
        {
            DailyUsage = daily
        };
    }
}
