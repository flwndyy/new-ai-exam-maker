using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamMaker.Application.Ai;

public sealed class AiGenerationService(
    AiQuotaOptions options,
    IAiUsageReader usage,
    IAiJobStore jobs,
    IAiJobQueue queue,
    TimeProvider time,
    ILogger<AiGenerationService>? logger = null)
{
    private readonly ILogger<AiGenerationService> _logger = logger ?? NullLogger<AiGenerationService>.Instance;

    public async Task<AiQuotaSnapshot> GetQuotaAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var used = await usage.GetUserUsageAsync(userId, date, ct);

        return new AiQuotaSnapshot(
            date,
            options.JobsPerUserPerDay,
            used.JobsUsed,
            options.TokensPerUserPerDay,
            used.TokensUsed,
            options.MaxQuestionsPerJob,
            NextUtcMidnight(date));
    }

    public async Task<AiSubmitResult> SubmitAsync(Guid userId, AiGenerateRequest request, CancellationToken ct)
    {
        var errors = AiGenerateRequestValidator.Validate(request, options);
        if (errors.Count > 0)
        {
            return new AiSubmitResult.ValidationFailed(errors);
        }

        var now = time.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var retryAfter = NextUtcMidnight(date) - now;

        var userUsage = await usage.GetUserUsageAsync(userId, date, ct);
        if (userUsage.TokensUsed >= options.TokensPerUserPerDay)
        {
            return new AiSubmitResult.QuotaExceeded(AiQuotaReason.UserTokens, retryAfter);
        }

        if (await usage.GetSystemTokensAsync(date, ct) >= options.SystemTokensPerDay)
        {
            return new AiSubmitResult.QuotaExceeded(AiQuotaReason.SystemTokens, retryAfter);
        }

        var jobId = await jobs.TryCreateQueuedJobAsync(
            userId, request, date, options.JobsPerUserPerDay, options.MaxAttempts, ct);
        if (jobId is null)
        {
            return new AiSubmitResult.QuotaExceeded(AiQuotaReason.UserJobs, retryAfter);
        }

        try
        {
            await queue.EnqueueAsync(jobId.Value, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job row is already committed as Queued. The periodic recovery task re-sends stale
            // Queued jobs (safe because the worker is idempotent per jobId), so do not fail the request.
            _logger.LogError(ex, "Failed to enqueue AI job {JobId}; it stays Queued for recovery.", jobId);
        }

        return new AiSubmitResult.Accepted(jobId.Value);
    }

    private static DateTimeOffset NextUtcMidnight(DateOnly date) =>
        new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
