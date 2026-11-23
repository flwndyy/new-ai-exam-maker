using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Verification;

namespace ExamMaker.Application.Ai.Worker;

/// <summary>What the consumer does with the SQS message after one processing attempt.</summary>
public abstract record AiJobOutcome
{
    /// <summary>The job is finished (or can never run): delete the message.</summary>
    public sealed record Delete : AiJobOutcome;

    /// <summary>Make the message visible again after <paramref name="Delay"/>.</summary>
    public sealed record RetryAfter(TimeSpan Delay) : AiJobOutcome;

    /// <summary>
    /// Send a fresh message for the job and delete this one. Used on shutdown: the attempt was given back, so the new
    /// message must not inherit this message's receive count (maxReceiveCount would otherwise move it to the DLQ).
    /// </summary>
    public sealed record Requeue : AiJobOutcome;

    /// <summary>Leave the message alone (lease lost to another worker).</summary>
    public sealed record Abandon : AiJobOutcome;
}

/// <summary>A successful claim; <paramref name="Attempt"/> is the fencing token for every later write.</summary>
public sealed record AiJobClaim(Guid JobId, int Attempt, int MaxAttempts);

public enum AiClaimMissReason
{
    NotFound,
    Finished,
    HeldByOther,
    AttemptsExhausted,
}

public sealed record AiClaimMiss(AiClaimMissReason Reason, TimeSpan RemainingLease);

/// <summary>Everything loaded from the database to run one job.</summary>
public sealed record AiJobContext(
    Guid JobId,
    Guid RequesterId,
    Guid DepartmentId,
    AiGenerateRequest Request,
    string SubjectName,
    string TopicName);

/// <summary>Either a context or the error code the job must fail with.</summary>
public sealed record AiJobContextLoad(AiJobContext? Context, string? FailureCode);

public sealed record AiStoredQuestion(AiGeneratedQuestion Question, AiVerificationOutcome Verification);

public enum AiCompleteResult
{
    Completed,
    LeaseLost,
    RequesterUnavailable,
}

/// <summary>
/// Database side of the worker. Every write after the claim except token usage is fenced by
/// <c>status = 'Running' AND attempt = claim.Attempt</c> and returns false (or <see cref="AiCompleteResult.LeaseLost"/>)
/// when another worker has taken the job over.
/// </summary>
public interface IAiJobWorkStore
{
    Task<AiJobClaim?> TryClaimAsync(Guid jobId, TimeSpan lease, string promptVersion, CancellationToken ct);

    Task<AiClaimMiss> DescribeMissAsync(Guid jobId, CancellationToken ct);

    Task<AiJobContextLoad> LoadContextAsync(AiJobClaim claim, CancellationToken ct);

    Task<bool> ExtendLeaseAsync(AiJobClaim claim, TimeSpan lease, CancellationToken ct);

    /// <summary>Adds one model call's tokens to the job and to ai_usage_daily for <paramref name="usageDate"/>; not fenced.</summary>
    Task RecordUsageAsync(Guid jobId, Guid userId, DateOnly usageDate, AiModelResponse response, CancellationToken ct);

    Task<bool> ReleaseForRetryAsync(AiJobClaim claim, string code, string message, CancellationToken ct);

    Task<bool> ReleaseOnShutdownAsync(AiJobClaim claim, CancellationToken ct);

    Task<bool> FailAsync(AiJobClaim claim, string code, string message, CancellationToken ct);

    /// <summary>Fails a job that is Queued or lease-expired with no attempts left; no-op otherwise.</summary>
    Task FailExhaustedAsync(Guid jobId, string message, CancellationToken ct);

    Task<AiCompleteResult> CompleteAsync(
        AiJobClaim claim,
        AiJobContext context,
        IReadOnlyList<AiStoredQuestion> questions,
        int duplicatesDropped,
        CancellationToken ct);
}

public sealed record AiExpiredLeaseResult(int Requeued, int Failed);

/// <summary>Database side of the periodic recovery task; each method is one set-based statement over all jobs.</summary>
public interface IAiJobRecoveryStore
{
    /// <summary>Running jobs past their lease: back to Queued when attempts remain, otherwise Failed with an audit row.</summary>
    Task<AiExpiredLeaseResult> RecoverExpiredLeasesAsync(string exhaustedMessage, CancellationToken ct);

    /// <summary>Queued jobs whose last send is older than <paramref name="olderThan"/>; their last_enqueued_at is bumped.</summary>
    Task<IReadOnlyList<Guid>> MarkStaleQueuedForResendAsync(TimeSpan olderThan, CancellationToken ct);

    Task<int> CountQueuedOlderThanAsync(TimeSpan age, CancellationToken ct);
}

/// <summary>One received queue message; the SQS implementation wraps the receipt handle.</summary>
public interface IAiQueueMessage
{
    string Body { get; }

    Task ChangeVisibilityAsync(TimeSpan visibility, CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}
