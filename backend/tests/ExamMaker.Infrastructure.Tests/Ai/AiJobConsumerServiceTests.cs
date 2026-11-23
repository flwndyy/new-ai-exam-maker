using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class AiJobConsumerServiceTests
{
    private const string QueueUrl = "https://sqs.ap-southeast-1.amazonaws.com/000000000000/ai-jobs";

    /// <summary>Hands out a fixed number of job messages, then behaves like an empty long poll.</summary>
    private sealed class FakeSqs(int messages)
        : AmazonSQSClient(new AnonymousAWSCredentials(), RegionEndpoint.APSoutheast1)
    {
        private int _left = messages;

        public List<int> Requested { get; } = [];
        public int Deleted;

        public override async Task<ReceiveMessageResponse> ReceiveMessageAsync(
            ReceiveMessageRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requested) Requested.Add(request.MaxNumberOfMessages ?? 1);
            var batch = new List<Message>();
            while (batch.Count < (request.MaxNumberOfMessages ?? 1) && Interlocked.Decrement(ref _left) >= 0)
            {
                batch.Add(new Message
                {
                    Body = $$"""{"jobId":"{{Guid.NewGuid()}}"}""",
                    ReceiptHandle = Guid.NewGuid().ToString(),
                });
            }

            if (batch.Count == 0) await Task.Delay(50, cancellationToken);
            return new ReceiveMessageResponse { Messages = batch };
        }

        public override Task<DeleteMessageResponse> DeleteMessageAsync(
            DeleteMessageRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Deleted);
            return Task.FromResult(new DeleteMessageResponse());
        }

        public override Task<ChangeMessageVisibilityResponse> ChangeMessageVisibilityAsync(
            ChangeMessageVisibilityRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChangeMessageVisibilityResponse());
    }

    private sealed class NoopQueue : IAiJobQueue
    {
        public Task EnqueueAsync(Guid jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private static AiJobConsumerService Create(
        FakeSqs sqs, int maxConcurrency, Func<Guid, IAiQueueMessage, CancellationToken, Task<AiJobOutcome>> process)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AiJobMessageHandler(process, new NoopQueue(), NullLogger<AiJobMessageHandler>.Instance));
        var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AiServiceCollectionExtensions.QueueUrlKey] = QueueUrl })
            .Build();
        return new AiJobConsumerService(provider.GetRequiredService<IServiceScopeFactory>(), sqs, configuration,
            new AiWorkerOptions { MaxConcurrency = maxConcurrency }, NullLogger<AiJobConsumerService>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Never_runs_more_jobs_than_MaxConcurrency_and_asks_only_for_free_slots()
    {
        var sqs = new FakeSqs(messages: 6);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, finished = 0;
        var service = Create(sqs, maxConcurrency: 2, async (_, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await gate.Task;
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref finished);
            return new AiJobOutcome.Delete();
        });

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref running) == 2);
        var receivesWhileFull = sqs.Requested.Count;
        await Task.Delay(200);
        Assert.Equal(receivesWhileFull, sqs.Requested.Count);

        gate.SetResult();
        await WaitUntilAsync(() => Volatile.Read(ref finished) == 6);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, peak);
        Assert.Equal(6, sqs.Deleted);
        lock (sqs.Requested) Assert.All(sqs.Requested, n => Assert.InRange(n, 1, 2));
    }

    [Fact]
    public async Task Stop_waits_for_running_jobs_and_passes_them_the_stopping_token()
    {
        var sqs = new FakeSqs(messages: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var service = Create(sqs, maxConcurrency: 2, async (_, _, stoppingToken) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }

            await Task.Delay(200, CancellationToken.None);
            finished = true;
            return new AiJobOutcome.Abandon();
        });

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(finished);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
