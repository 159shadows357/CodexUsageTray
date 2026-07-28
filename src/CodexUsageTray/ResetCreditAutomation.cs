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
    int? Stage = null);

internal sealed record ResetCreditAttemptState(
    string CreditKey,
    DateTimeOffset? ExpiresAt,
    bool NotificationSent,
    bool Completed,
    int LastCompletedStage,
    int? PendingStage,
    string? IdempotencyKey,
    DateTimeOffset? RetryAt);

internal sealed record ResetCreditAutomationState
{
    public List<ResetCreditAttemptState> Credits { get; init; } = [];
}

public sealed class ResetCreditAutomation
{
    private static readonly TimeSpan[] AttemptWindows =
    [
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(1),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(5)
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
        DateTimeOffset now)
    {
        PruneExpiredState(now);
        var available = EligibleCredits(credits, now);
        if (mode == ResetCreditMode.Off || available.Count == 0)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        var credit = available[0];
        var expiresAt = credit.ExpiresAtLocal!.Value;
        var stage = EligibleStage(expiresAt - now);
        if (stage < 0)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        var state = GetOrCreateState(credit);
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

        if (state.PendingStage is not null)
        {
            if (state.RetryAt is null || state.RetryAt <= now)
            {
                return new ResetCreditAutomationDecision(
                    ResetCreditActionKind.Consume,
                    credit,
                    state.IdempotencyKey,
                    state.PendingStage);
            }

            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        if (stage <= state.LastCompletedStage)
        {
            return new ResetCreditAutomationDecision(ResetCreditActionKind.None);
        }

        var idempotencyKey = Guid.NewGuid().ToString("D");
        ReplaceState(state with
        {
            PendingStage = stage,
            IdempotencyKey = idempotencyKey,
            RetryAt = null
        });
        Save();
        return new ResetCreditAutomationDecision(
            ResetCreditActionKind.Consume,
            credit,
            idempotencyKey,
            stage);
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
        var next = outcome switch
        {
            ResetCreditConsumeOutcome.NothingToReset => state with
            {
                LastCompletedStage = Math.Max(state.LastCompletedStage, completedStage),
                PendingStage = null,
                IdempotencyKey = null,
                RetryAt = null,
                Completed = completedStage >= AttemptWindows.Length - 1
            },
            ResetCreditConsumeOutcome.Reset or
            ResetCreditConsumeOutcome.NoCredit or
            ResetCreditConsumeOutcome.AlreadyRedeemed => state with
            {
                Completed = true,
                PendingStage = null,
                IdempotencyKey = null,
                RetryAt = null
            },
            _ => state with
            {
                Completed = true,
                PendingStage = null,
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
        DateTimeOffset now)
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

        if (state?.PendingStage is not null)
        {
            return state.RetryAt ?? now;
        }

        var nextStage = Math.Max(0, (state?.LastCompletedStage ?? -1) + 1);
        var candidates = AttemptWindows
            .Select((window, stage) => (At: credit.ExpiresAtLocal.Value - window, Stage: stage))
            .Where(item => item.Stage >= nextStage && item.At > now)
            .Select(item => item.At)
            .ToList();
        return candidates.Count > 0 ? candidates.Min() : now;
    }

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

    private static int EligibleStage(TimeSpan remaining)
    {
        for (var stage = AttemptWindows.Length - 1; stage >= 0; stage--)
        {
            if (remaining <= AttemptWindows[stage])
            {
                return stage;
            }
        }

        return -1;
    }

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
            PendingStage: null,
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
