using System.Security.Cryptography;
using System.Text;

namespace CodexUsageTray;

public enum ResetCreditActionKind
{
    None,
    Notify,
    Consume
}

public sealed record ResetCreditAutomationDecision(
    ResetCreditActionKind Action,
    RateLimitResetCredit? Credit = null,
    string? IdempotencyKey = null,
    int? Stage = null,
    int? LeadTimeMinutes = null,
    bool IsFinalStage = false);

internal sealed record ResetCreditAttemptState(
    string CreditKey,
    DateTimeOffset? ExpiresAt,
    bool NotificationSent,
    bool Completed,
    int LastCompletedStage,
    int? LastCompletedLeadTimeMinutes,
    int[]? CompletedWindowMinutes,
    int? PendingStage,
    int? PendingLeadTimeMinutes,
    string? IdempotencyKey,
    DateTimeOffset? RetryAt);

internal sealed record ResetCreditAutomationState
{
    public List<ResetCreditAttemptState> Credits { get; init; } = [];
}

public sealed class ResetCreditAutomation
{
    public const int DefaultLeadTimeMinutes = 6 * 60;
    public const int MinimumLeadTimeMinutes = 5;
    public const int MaximumLeadTimeMinutes = 7 * 24 * 60;

    private static readonly (int Stage, TimeSpan Window)[] FallbackAttemptWindows =
    [
        (1, TimeSpan.FromHours(1)),
        (2, TimeSpan.FromMinutes(15)),
        (3, TimeSpan.FromMinutes(5))
    ];
    private static readonly TimeSpan TransportRetryDelay = TimeSpan.FromMinutes(1);

    private readonly string _statePath;
    private readonly JsonStore _store;
    private ResetCreditAutomationState _state;

    public ResetCreditAutomation(string statePath, JsonStore store)
    {
        _statePath = statePath;
        _store = store;
        _state = store.ReadValue<ResetCreditAutomationState>(statePath) ?? new ResetCreditAutomationState();
    }

    public ResetCreditAutomationDecision Evaluate(
        IReadOnlyList<RateLimitResetCredit>? credits,
        ResetCreditMode mode,
        DateTimeOffset now,
        int leadTimeMinutes = DefaultLeadTimeMinutes)
    {
        PruneExpiredState(now);
        var available = EligibleCredits(credits, now);
        if (mode == ResetCreditMode.Off || available.Count == 0)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        var credit = available[0];
        var expiresAt = credit.ExpiresAtLocal!.Value;
        var normalizedLeadTime = NormalizeLeadTimeMinutes(leadTimeMinutes);
        var attemptWindows = CreateAttemptWindows(normalizedLeadTime);
        var state = FindState(credit);
        if (mode == ResetCreditMode.UseBeforeExpiry && state?.Completed == true)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        if (mode == ResetCreditMode.UseBeforeExpiry && state?.PendingStage is not null)
        {
            if (state.RetryAt is null || state.RetryAt <= now)
            {
                var pendingLeadTime = NormalizeLeadTimeMinutes(
                    state.PendingLeadTimeMinutes ?? DefaultLeadTimeMinutes);
                return new ResetCreditAutomationDecision(
                    ResetCreditActionKind.Consume,
                    credit,
                    state.IdempotencyKey,
                    state.PendingStage,
                    pendingLeadTime,
                    IsFinalStage(state.PendingStage.Value, CreateAttemptWindows(pendingLeadTime)));
            }

            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        var stage = EligibleStage(expiresAt - now, attemptWindows);
        if (stage < 0)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        state ??= GetOrCreateState(credit);
        if (mode == ResetCreditMode.NotifyBeforeExpiry)
        {
            if (state.NotificationSent)
            {
                return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
            }

            ReplaceState(state with { NotificationSent = true });
            Save();
            return new ResetCreditAutomationDecision(ResetCreditActionKind.Notify, credit);
        }

        if (state.Completed)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        if (IsStageCompleted(state, stage, normalizedLeadTime))
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        var idempotencyKey = Guid.NewGuid().ToString("D");
        ReplaceState(state with
        {
            PendingStage = stage,
            PendingLeadTimeMinutes = normalizedLeadTime,
            IdempotencyKey = idempotencyKey,
            RetryAt = null
        });
        Save();
        return new ResetCreditAutomationDecision(
            ResetCreditActionKind.Consume,
            credit,
            idempotencyKey,
            stage,
            normalizedLeadTime,
            IsFinalStage(stage, attemptWindows));
    }

    public void RecordOutcome(
        ResetCreditAutomationDecision decision,
        ResetCreditConsumeOutcome outcome,
        DateTimeOffset now)
    {
        var state = FindState(decision.Credit);
        if (state is null)
        {
            return;
        }

        var completedStage = state.PendingStage ?? state.LastCompletedStage;
        var completedWindowMinutes = StageWindowMinutes(
            completedStage,
            decision.LeadTimeMinutes ?? state.PendingLeadTimeMinutes ?? DefaultLeadTimeMinutes);
        var next = outcome switch
        {
            ResetCreditConsumeOutcome.NothingToReset => state with
            {
                LastCompletedStage = Math.Max(state.LastCompletedStage, completedStage),
                LastCompletedLeadTimeMinutes = completedStage == 0
                    ? decision.LeadTimeMinutes ?? DefaultLeadTimeMinutes
                    : state.LastCompletedLeadTimeMinutes,
                CompletedWindowMinutes = AddCompletedWindow(
                    state.CompletedWindowMinutes,
                    completedWindowMinutes),
                PendingStage = null,
                PendingLeadTimeMinutes = null,
                IdempotencyKey = null,
                RetryAt = null,
                Completed = decision.IsFinalStage
            },
            ResetCreditConsumeOutcome.Reset or
            ResetCreditConsumeOutcome.NoCredit or
            ResetCreditConsumeOutcome.AlreadyRedeemed => state with
            {
                Completed = true,
                PendingStage = null,
                PendingLeadTimeMinutes = null,
                IdempotencyKey = null,
                RetryAt = null
            },
            _ => state with
            {
                Completed = true,
                PendingStage = null,
                PendingLeadTimeMinutes = null,
                IdempotencyKey = null,
                RetryAt = null
            }
        };
        ReplaceState(next);
        Save();
    }

    public void RecordTransportFailure(ResetCreditAutomationDecision decision, DateTimeOffset now)
    {
        var state = FindState(decision.Credit);
        if (state is null)
        {
            return;
        }

        var retryAt = SafeTransportRetryAt(state.ExpiresAt, now);
        ReplaceState(state with { RetryAt = retryAt, Completed = retryAt is null });
        Save();
    }

    public DateTimeOffset? NextEvaluationAt(
        IReadOnlyList<RateLimitResetCredit>? credits,
        ResetCreditMode mode,
        DateTimeOffset now,
        int leadTimeMinutes = DefaultLeadTimeMinutes)
    {
        if (mode == ResetCreditMode.Off)
        {
            return null;
        }

        var credit = EligibleCredits(credits, now).FirstOrDefault();
        if (credit?.ExpiresAtLocal is null)
        {
            return null;
        }

        var state = FindState(credit);
        if (state?.Completed == true || mode == ResetCreditMode.NotifyBeforeExpiry && state?.NotificationSent == true)
        {
            return null;
        }

        if (mode == ResetCreditMode.UseBeforeExpiry && state?.PendingStage is not null)
        {
            return state.RetryAt ?? now;
        }

        var normalizedLeadTime = NormalizeLeadTimeMinutes(leadTimeMinutes);
        var attemptWindows = CreateAttemptWindows(normalizedLeadTime);
        var candidates = attemptWindows
            .Select(item => (At: credit.ExpiresAtLocal.Value - item.Window, item.Stage))
            .Where(item =>
                !IsStageCompleted(state, item.Stage, normalizedLeadTime) &&
                item.At > now)
            .Select(item => item.At)
            .ToList();
        return candidates.Count > 0 ? candidates.Min() : now;
    }

    public static int NormalizeLeadTimeMinutes(int value)
        => Math.Clamp(value, MinimumLeadTimeMinutes, MaximumLeadTimeMinutes);

    internal static string CreditKey(string creditId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(creditId))).ToLowerInvariant();

    private static List<RateLimitResetCredit> EligibleCredits(
        IReadOnlyList<RateLimitResetCredit>? credits,
        DateTimeOffset now)
        => credits?
            .Where(credit =>
                string.Equals(credit.Status, "available", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(credit.ResetType, "codexRateLimits", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(credit.Id) &&
                credit.ExpiresAtLocal is not null &&
                credit.ExpiresAtLocal > now)
            .OrderBy(credit => credit.ExpiresAtLocal)
            .ThenBy(credit => credit.Id, StringComparer.Ordinal)
            .ToList() ?? [];

    private static List<(int Stage, TimeSpan Window)> CreateAttemptWindows(int leadTimeMinutes)
    {
        var normalized = NormalizeLeadTimeMinutes(leadTimeMinutes);
        var windows = new List<(int Stage, TimeSpan Window)>
        {
            (0, TimeSpan.FromMinutes(normalized))
        };
        windows.AddRange(FallbackAttemptWindows.Where(item => item.Window.TotalMinutes < normalized));
        return windows;
    }

    private static int EligibleStage(
        TimeSpan remaining,
        IReadOnlyList<(int Stage, TimeSpan Window)> attemptWindows)
    {
        for (var index = attemptWindows.Count - 1; index >= 0; index--)
        {
            if (remaining <= attemptWindows[index].Window)
            {
                return attemptWindows[index].Stage;
            }
        }

        return -1;
    }

    private static bool IsFinalStage(
        int stage,
        IReadOnlyList<(int Stage, TimeSpan Window)> attemptWindows)
        => attemptWindows.All(item => item.Stage <= stage);

    private static bool IsStageCompleted(
        ResetCreditAttemptState? state,
        int stage,
        int leadTimeMinutes)
    {
        if (state is null)
        {
            return false;
        }

        var windowMinutes = StageWindowMinutes(stage, leadTimeMinutes);
        if (state.CompletedWindowMinutes?.Contains(windowMinutes) == true)
        {
            return true;
        }

        if (state.CompletedWindowMinutes is not null)
        {
            return false;
        }

        if (state.LastCompletedStage < 0)
        {
            return false;
        }

        var legacyCompletedWindows = FallbackAttemptWindows
            .Where(item => item.Stage <= state.LastCompletedStage)
            .Select(item => (int)item.Window.TotalMinutes)
            .ToList();
        if (state.LastCompletedStage >= 0)
        {
            legacyCompletedWindows.Add(NormalizeLeadTimeMinutes(
                state.LastCompletedLeadTimeMinutes ?? DefaultLeadTimeMinutes));
        }

        return legacyCompletedWindows.Contains(windowMinutes);
    }

    private static int StageWindowMinutes(int stage, int leadTimeMinutes)
        => stage == 0
            ? NormalizeLeadTimeMinutes(leadTimeMinutes)
            : (int)FallbackAttemptWindows.Single(item => item.Stage == stage).Window.TotalMinutes;

    private static int[] AddCompletedWindow(int[]? completedWindows, int windowMinutes)
        => (completedWindows ?? [])
            .Append(windowMinutes)
            .Distinct()
            .Order()
            .ToArray();

    private ResetCreditAttemptState GetOrCreateState(RateLimitResetCredit credit)
    {
        var creditKey = CreditKey(credit.Id);
        var existing = _state.Credits.FirstOrDefault(item => item.CreditKey == creditKey);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ResetCreditAttemptState(
            creditKey,
            credit.ExpiresAtLocal,
            NotificationSent: false,
            Completed: false,
            LastCompletedStage: -1,
            LastCompletedLeadTimeMinutes: null,
            CompletedWindowMinutes: [],
            PendingStage: null,
            PendingLeadTimeMinutes: null,
            IdempotencyKey: null,
            RetryAt: null);
        _state.Credits.Add(created);
        return created;
    }

    private ResetCreditAttemptState? FindState(RateLimitResetCredit? credit)
    {
        if (credit is null)
        {
            return null;
        }

        var creditKey = CreditKey(credit.Id);
        return _state.Credits.FirstOrDefault(item => item.CreditKey == creditKey);
    }

    private void ReplaceState(ResetCreditAttemptState replacement)
    {
        var index = _state.Credits.FindIndex(item => item.CreditKey == replacement.CreditKey);
        if (index >= 0)
        {
            _state.Credits[index] = replacement;
        }
        else
        {
            _state.Credits.Add(replacement);
        }
    }

    private void PruneExpiredState(DateTimeOffset now)
    {
        var removed = _state.Credits.RemoveAll(item => item.ExpiresAt is not null && item.ExpiresAt < now.AddDays(-1));
        if (removed > 0)
        {
            Save();
        }
    }

    private static DateTimeOffset? SafeTransportRetryAt(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var retryAt = now + TransportRetryDelay;
        return expiresAt is not null && retryAt < expiresAt ? retryAt : null;
    }

    private void Save() => _store.Write(_statePath, _state);
}
