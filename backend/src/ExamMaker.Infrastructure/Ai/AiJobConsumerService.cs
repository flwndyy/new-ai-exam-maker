using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Amazon.SQS;
using Amazon.SQS.Model;
using ExamMaker.Application.Ai.Worker;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Long-polls the AI job queue and runs at most <see cref="AiWorkerOptions.MaxConcurrency"/> jobs at once, each in its
/// own DI scope. Messages are only received when a slot is free, so nothing waits in memory past its visibility timeout.
/// </summary>
public sealed class AiJobConsumerService(
    IServiceScopeFactory scopes,
    IAmazonSQS sqs,
    IConfiguration configuration,
    AiWorkerOptions options,
    ILogger<AiJobConsumerService> logger) : BackgroundService
{
    // SQS returns at most 10 messages per receive.
    private const int MaxBatch = 10;
    private static readonly TimeSpan ReceiveErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueUrl = configuration[AiServiceCollectionExtensions.QueueUrlKey]!;
        using var slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        var running = new HashSet<Task>();
        var heartbeat = LogHeartbeatAsync(stoppingToken);
        logger.LogInformation("AI job consumer started (max concurrency {MaxConcurrency}).", options.MaxConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            int free;
            try
            {
                await slots.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            free = 1;
            while (free < MaxBatch && slots.Wait(0)) free++;

            List<Message> messages;
            try
            {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = free,
                    WaitTimeSeconds = 20,
                }, stoppingToken);
                messages = response.Messages ?? [];
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                slots.Release(free);
                break;
            }
            catch (Exception ex)
            {
                slots.Release(free);
                logger.LogError("Receiving from the AI job queue failed ({ErrorType}); retrying.", ex.GetType().Name);
                try
                {
                    await Task.Delay(ReceiveErrorDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (messages.Count < free) slots.Release(free - messages.Count);
            foreach (var message in messages)
            {
                var task = HandleAsync(new SqsAiQueueMessage(sqs, queueUrl, message), slots, stoppingToken);
                lock (running) running.Add(task);
                _ = task.ContinueWith(t => { lock (running) running.Remove(t); }, TaskScheduler.Default);
            }
        }

        Task[] pending;
        lock (running) pending = running.ToArray();
        // Jobs see the stopping token, release themselves and return quickly.
        await Task.WhenAll(pending);
        await heartbeat;
        logger.LogInformation("AI job consumer stopped.");
    }

    private async Task HandleAsync(IAiQueueMessage message, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AiJobMessageHandler>().HandleAsync(message, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while handling an AI job message.");
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task LogHeartbeatAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                logger.LogInformation("AI worker heartbeat.");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
