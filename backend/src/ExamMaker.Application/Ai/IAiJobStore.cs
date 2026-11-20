namespace ExamMaker.Application.Ai;

public interface IAiJobStore
{
    /// <summary>
    /// Atomically (one transaction) reserves a job slot in ai_usage_daily for <paramref name="usageDate"/>
    /// (conditional upsert: only while jobs &lt; <paramref name="jobsLimit"/>) and inserts an
    /// ai_generation_jobs row with status Queued.
    /// Returns the new job id, or null when the daily job limit is already reached.
    /// </summary>
    Task<Guid?> TryCreateQueuedJobAsync(
        Guid userId,
        AiGenerateRequest request,
        DateOnly usageDate,
        int jobsLimit,
        int maxAttempts,
        CancellationToken ct);
}
