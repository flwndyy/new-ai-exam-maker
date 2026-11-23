using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Application.Ai.Worker;

/// <summary>
/// Turns one queue message into one processing attempt and applies the outcome to the message. Never throws: a failure
/// here leaves the message to reappear after its visibility timeout (bounded by maxReceiveCount, then the DLQ).
/// </summary>
public sealed class AiJobMessageHandler
{
    // Outcome calls run after the job, possibly during shutdown, so they get their own short budget.
    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<Guid, IAiQueueMessage, CancellationToken, Task<AiJobOutcome>> _process;
    private readonly IAiJobQueue _queue;
    private readonly ILogger<AiJobMessageHandler> _logger;

    public AiJobMessageHandler(AiJobProcessor processor, IAiJobQueue queue, ILogger<AiJobMessageHandler> logger)
        : this(processor.ProcessAsync, queue, logger)
    {
    }

    public AiJobMessageHandler(
        Func<Guid, IAiQueueMessage, CancellationToken, Task<AiJobOutcome>> process,
        IAiJobQueue queue,
        ILogger<AiJobMessageHandler> logger)
    {
        _process = process;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>Reads <c>{"jobId":"&lt;guid&gt;"}</c> as written by the API; anything else is not a job message.</summary>
    public static bool TryParseJobId(string body, out Guid jobId)
    {
        jobId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("jobId", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetGuid(out jobId)
                && jobId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task HandleAsync(IAiQueueMessage message, CancellationToken stoppingToken)
    {
        if (!TryParseJobId(message.Body, out var jobId))
        {
            _logger.LogWarning("Deleting a queue message that is not a valid AI job message.");
            await ApplyAsync(message, new AiJobOutcome.Delete(), null);
            return;
        }

        AiJobOutcome outcome;
        try
        {
            outcome = await _process(jobId, message, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing AI job {JobId} failed unexpectedly; the message will be redelivered.", jobId);
            return;
        }

        await ApplyAsync(message, outcome, jobId);
    }

    private async Task RequeueAsync(IAiQueueMessage message, Guid jobId, CancellationToken ct)
    {
        try
        {
            await _queue.EnqueueAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            // Without a fresh message, reuse this one now; the recovery task re-sends if that is lost too.
            _logger.LogWarning("Re-sending AI job {JobId} failed ({ErrorType}); making its message visible instead.",
                jobId, ex.GetType().Name);
            await message.ChangeVisibilityAsync(TimeSpan.Zero, ct);
            return;
        }

        await message.DeleteAsync(ct);
    }

    private async Task ApplyAsync(IAiQueueMessage message, AiJobOutcome outcome, Guid? jobId)
    {
        using var timeout = new CancellationTokenSource(OutcomeTimeout);
        try
        {
            switch (outcome)
            {
                case AiJobOutcome.Delete:
                    await message.DeleteAsync(timeout.Token);
                    break;
                case AiJobOutcome.RetryAfter retry:
                    await message.ChangeVisibilityAsync(retry.Delay, timeout.Token);
                    break;
                case AiJobOutcome.Requeue when jobId is { } id:
                    await RequeueAsync(message, id, timeout.Token);
                    break;
            }
        }
        catch (Exception ex)
        {
            // A missed delete only means one more delivery, which the idempotent claim turns into a no-op.
            _logger.LogError(ex, "Applying {Outcome} to the queue message of AI job {JobId} failed.",
                outcome.GetType().Name, jobId);
        }
    }
}
