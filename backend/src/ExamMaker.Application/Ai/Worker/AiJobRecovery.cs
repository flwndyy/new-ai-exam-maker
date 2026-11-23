using ExamMaker.Application.Ai.Generation;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Application.Ai.Worker;

/// <summary>
/// One pass of the periodic recovery task (docs/context.md 4.1.1): release jobs whose worker died, re-send Queued jobs
/// whose message was lost, and warn about jobs waiting too long. Each step runs even if an earlier one failed.
/// </summary>
public sealed class AiJobRecovery(
    IAiJobRecoveryStore store,
    IAiJobQueue queue,
    AiWorkerOptions options,
    ILogger<AiJobRecovery> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var expired = await store.RecoverExpiredLeasesAsync(
                AiMessages.GenerationFailed(AiErrorCodes.AttemptsExhausted), ct);
            if (expired.Requeued > 0 || expired.Failed > 0)
                logger.LogWarning("Recovered AI jobs with expired leases: {Requeued} requeued, {Failed} failed.",
                    expired.Requeued, expired.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI job recovery: releasing expired leases failed.");
        }

        try
        {
            var stale = await store.MarkStaleQueuedForResendAsync(TimeSpan.FromMinutes(options.RequeueAfterMinutes), ct);
            foreach (var jobId in stale)
            {
                try
                {
                    await queue.EnqueueAsync(jobId, ct);
                    logger.LogInformation("Re-sent queue message for stale AI job {JobId}.", jobId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // last_enqueued_at was already bumped, so the next pass after RequeueAfterMinutes tries again.
                    logger.LogError(ex, "Re-sending queue message for AI job {JobId} failed.", jobId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI job recovery: selecting stale queued jobs failed.");
        }

        try
        {
            var old = await store.CountQueuedOlderThanAsync(TimeSpan.FromMinutes(options.StaleQueuedWarnMinutes), ct);
            if (old > 0)
                logger.LogWarning("{Count} AI jobs have been Queued for more than {Minutes} minutes.",
                    old, options.StaleQueuedWarnMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI job recovery: counting old queued jobs failed.");
        }
    }
}
