using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Verification;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Application.Ai.Worker;

/// <summary>
/// Runs one delivery of an AI job message (docs/context.md 4.1.1 and 4.1.2, B6): claim, generate (B3), drop
/// duplicates and verify (B4) under the token quota, then persist; every failure is mapped to a job state and to what
/// the consumer must do with the message.
/// </summary>
public sealed class AiJobProcessor(
    IAiJobWorkStore store,
    IAiDuplicateKeySource duplicateKeys,
    IAiModelClient model,
    IAiUsageReader usage,
    AiQuotaOptions quota,
    AiGenerationOptions generation,
    AiWorkerOptions worker,
    TimeProvider time,
    ILogger<AiJobProcessor> logger)
{
    private static readonly AiVerificationOutcome NotChecked = new(AiVerificationValues.NotChecked, null);
    private static readonly AiJobOutcome Delete = new AiJobOutcome.Delete();
    private static readonly AiJobOutcome Abandon = new AiJobOutcome.Abandon();

    private TimeSpan Lease => TimeSpan.FromSeconds(worker.LeaseSeconds);

    public async Task<AiJobOutcome> ProcessAsync(Guid jobId, IAiQueueMessage message, CancellationToken stoppingToken)
    {
        var claim = await store.TryClaimAsync(jobId, Lease, AiPromptBuilder.Version, stoppingToken);
        if (claim is null) return await HandleMissAsync(jobId, stoppingToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(worker.JobTimeoutMinutes), time);
        using var job = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        var heartbeat = AiJobHeartbeat.Start(store, claim, message, worker, time, job, logger);

        AiJobOutcome? outcome = null;
        Exception? failure = null;
        try
        {
            outcome = await RunAsync(claim, job.Token);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await heartbeat.StopAsync();
        }

        return failure is null ? outcome! : await HandleFailureAsync(claim, failure, heartbeat.LeaseLost, stoppingToken);
    }

    private async Task<AiJobOutcome> RunAsync(AiJobClaim claim, CancellationToken ct)
    {
        var load = await store.LoadContextAsync(claim, ct);
        if (load.Context is not { } context) return await FailAsync(claim, load.FailureCode ?? AiErrorCodes.InvalidRequest);

        var request = context.Request;
        if (AiGenerateRequestValidator.Validate(request, quota).Count > 0)
            return await FailAsync(claim, AiErrorCodes.InvalidRequest);

        var gated = new QuotaGatedModelClient(model, usage, quota, time, context.RequesterId);
        // Tokens already spent must always reach the ledger, even if the job is being cancelled.
        Task RecordUsage(AiModelResponse response, CancellationToken _) =>
            store.RecordUsageAsync(claim.JobId, context.RequesterId, Today(), response, CancellationToken.None);

        var spec = new AiGenerationSpec(request.SubjectId, request.TopicId, request.Grade, request.Type,
            request.Difficulty, request.Count, context.SubjectName, context.TopicName, request.Note);
        var generated = await new AiQuestionGenerator(gated, generation).GenerateAsync(spec, RecordUsage, ct);

        var keys = await duplicateKeys.GetKeysAsync(request.SubjectId, request.TopicId, request.Type, ct);
        var filtered = AiDuplicateFilter.Filter(request.Type, generated.Questions, keys);

        var verification = await new AiAnswerVerifier(gated, generation)
            .VerifyAsync(request.Type, filtered.Kept, RecordUsage, ct);
        var stored = filtered.Kept
            .Select(q => new AiStoredQuestion(q, verification.GetValueOrDefault(q.Ordinal, NotChecked)))
            .ToList();

        return await store.CompleteAsync(claim, context, stored, filtered.Dropped, ct) switch
        {
            AiCompleteResult.Completed => Delete,
            AiCompleteResult.RequesterUnavailable => await FailAsync(claim, AiErrorCodes.RequesterUnavailable),
            _ => LeaseLost(claim),
        };
    }

    private async Task<AiJobOutcome> HandleMissAsync(Guid jobId, CancellationToken ct)
    {
        var miss = await store.DescribeMissAsync(jobId, ct);
        switch (miss.Reason)
        {
            case AiClaimMissReason.NotFound:
                logger.LogWarning("Queue message for unknown AI job {JobId}; deleting it.", jobId);
                return Delete;
            case AiClaimMissReason.Finished:
                return Delete;
            case AiClaimMissReason.AttemptsExhausted:
                await store.FailExhaustedAsync(jobId, AiMessages.GenerationFailed(AiErrorCodes.AttemptsExhausted), ct);
                return Delete;
            default:
                // Another worker holds the job. Come back just after its lease would expire, so a dead holder is
                // replaced without waiting for the recovery task.
                var delay = miss.RemainingLease + TimeSpan.FromSeconds(5);
                return new AiJobOutcome.RetryAfter(delay < Lease ? delay : Lease);
        }
    }

    private async Task<AiJobOutcome> HandleFailureAsync(
        AiJobClaim claim, Exception exception, bool leaseLost, CancellationToken stoppingToken)
    {
        if (leaseLost) return LeaseLost(claim);

        if (stoppingToken.IsCancellationRequested)
        {
            // Shutdown is not the job's fault: give the attempt back and requeue with a fresh message (fresh receive count).
            logger.LogInformation("Releasing AI job {JobId} because the worker is stopping.", claim.JobId);
            return await store.ReleaseOnShutdownAsync(claim, CancellationToken.None)
                ? new AiJobOutcome.Requeue()
                : LeaseLost(claim);
        }

        if (exception is AiQuotaExceededException)
            return await FailAsync(claim, AiErrorCodes.QuotaExceeded);

        // A job-timeout cancellation is an OperationCanceledException, which the classifier maps to retryable Timeout.
        var classified = AiErrorClassifier.Classify(exception);
        logger.LogWarning("AI job {JobId} attempt {Attempt} failed: {Code} ({ErrorType}).",
            claim.JobId, claim.Attempt, classified.Code, exception.GetType().Name);

        if (!classified.IsRetryable)
            return await FailAsync(claim, classified.Code);

        // Out of attempts: keep the cause as the code, but the retry wording ("hệ thống sẽ thử lại") would now be false.
        if (claim.Attempt >= claim.MaxAttempts)
            return await FailAsync(claim, classified.Code, AiMessages.GenerationFailed(AiErrorCodes.AttemptsExhausted));

        return await store.ReleaseForRetryAsync(claim, classified.Code, classified.Message, CancellationToken.None)
            ? new AiJobOutcome.RetryAfter(AiRetryPolicy.VisibilityDelay(claim.Attempt))
            : LeaseLost(claim);
    }

    private async Task<AiJobOutcome> FailAsync(AiJobClaim claim, string code, string? message = null)
    {
        logger.LogInformation("AI job {JobId} failed permanently: {Code}.", claim.JobId, code);
        return await store.FailAsync(claim, code, message ?? AiMessages.GenerationFailed(code), CancellationToken.None)
            ? Delete
            : LeaseLost(claim);
    }

    private AiJobOutcome LeaseLost(AiJobClaim claim)
    {
        logger.LogWarning("AI job {JobId} attempt {Attempt} was taken over by another worker.", claim.JobId, claim.Attempt);
        return Abandon;
    }

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
