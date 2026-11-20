using ExamMaker.Application.Ai;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Development-only stand-in used when no SQS queue URL is configured, so the API can be exercised
/// locally. Nothing is sent: jobs stay Queued until a real queue and worker process them.
/// </summary>
public sealed class LoggingAiJobQueue(ILogger<LoggingAiJobQueue> logger) : IAiJobQueue
{
    public Task EnqueueAsync(Guid jobId, CancellationToken ct)
    {
        logger.LogWarning(
            "SQS is not configured; AI job {JobId} was not sent and stays Queued (development only).", jobId);
        return Task.CompletedTask;
    }
}
