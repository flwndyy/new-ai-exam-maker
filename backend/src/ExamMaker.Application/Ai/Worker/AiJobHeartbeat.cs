using Microsoft.Extensions.Logging;

namespace ExamMaker.Application.Ai.Worker;

/// <summary>
/// Keeps a claimed job alive while it runs: every tick extends the DB lease first, then the SQS visibility, so the
/// message never becomes visible while the lease is still valid. Losing the lease cancels the job.
/// </summary>
internal sealed class AiJobHeartbeat
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private volatile bool _leaseLost;

    private AiJobHeartbeat()
    {
    }

    public bool LeaseLost => _leaseLost;

    public static AiJobHeartbeat Start(IAiJobWorkStore store, AiJobClaim claim, IAiQueueMessage message,
        AiWorkerOptions options, TimeProvider time, CancellationTokenSource jobCancellation, ILogger logger)
    {
        var heartbeat = new AiJobHeartbeat();
        heartbeat._loop = heartbeat.RunAsync(store, claim, message, options, time, jobCancellation, logger);
        return heartbeat;
    }

    public async ValueTask StopAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }

    private async Task RunAsync(IAiJobWorkStore store, AiJobClaim claim, IAiQueueMessage message,
        AiWorkerOptions options, TimeProvider time, CancellationTokenSource jobCancellation, ILogger logger)
    {
        var lease = TimeSpan.FromSeconds(options.LeaseSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                bool extended;
                try
                {
                    extended = await store.ExtendLeaseAsync(claim, lease, _stop.Token);
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    // The current lease still has most of its time left; try again on the next tick.
                    logger.LogWarning("Extending the lease of AI job {JobId} failed ({ErrorType}).",
                        claim.JobId, ex.GetType().Name);
                    continue;
                }

                if (!extended)
                {
                    _leaseLost = true;
                    logger.LogWarning("AI job {JobId} lost its lease (attempt {Attempt}); stopping.", claim.JobId, claim.Attempt);
                    await jobCancellation.CancelAsync();
                    return;
                }

                try
                {
                    await message.ChangeVisibilityAsync(lease, _stop.Token);
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    // The DB lease still guards correctness; the next tick extends visibility again.
                    logger.LogWarning("Extending the queue visibility of AI job {JobId} failed ({ErrorType}).",
                        claim.JobId, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }
}
