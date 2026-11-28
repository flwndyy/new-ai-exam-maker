namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>
/// SQS standard queues deliver at least once (docs/context.md 4.1.1): the same job id may arrive several times, even
/// concurrently, and must still produce exactly one set of questions, one Succeeded audit and one token charge.
/// </summary>
public sealed class WorkerDuplicateDeliveryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly int[] FiveOrdinals = [1, 2, 3, 4, 5];

    private static void AssertSucceededOnce(JobState state, int attempt)
    {
        Assert.Equal("Succeeded", state.Status);
        Assert.Equal(attempt, state.Attempt);
        Assert.Equal(FiveOrdinals, state.Ordinals);
        Assert.Single(state.JobAuditNotes, n => n.StartsWith("AI worker; questions=5", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Duplicate_message_after_success_is_deleted_without_new_work()
    {
        var worker = new WorkerHarness(fixture);
        var (userId, jobId) = await worker.CreateJobAsync();
        await worker.Queue.EnqueueAsync(jobId, CancellationToken.None);
        var model = new ScriptedModel(ScriptedModel.Valid(5));

        await worker.DrainAsync(model);

        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal(1, model.Calls);
        AssertSucceededOnce(state, attempt: 1);
        Assert.Equal(10, state.InputTokens);
        Assert.Equal(10, state.OutputTokens);
        Assert.Equal(ScriptedModel.TokensPerCall, await worker.UsageTokensAsync(userId));
        Assert.Empty(worker.Queue.For(jobId));
        Assert.Empty(worker.Queue.Dlq);
    }

    [PostgresFact(Timeout = 60_000)]
    public async Task Two_workers_on_the_same_job_produce_one_result()
    {
        var worker = new WorkerHarness(fixture);
        var (userId, jobId) = await worker.CreateJobAsync();
        await worker.Queue.EnqueueAsync(jobId, CancellationToken.None);
        var gate = new ModelGate();
        var messages = worker.Queue.Receive();
        Assert.Equal(2, messages.Count);

        // Worker 1 claims and then blocks inside the model call; only then does worker 2 get the duplicate.
        var first = worker.HandleAsync(messages[0], new ScriptedModel(ScriptedModel.Gated(gate, 5)));
        var idle = new ScriptedModel();
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            await worker.HandleAsync(messages[1], idle);

            Assert.Equal(0, idle.Calls);
            Assert.False(messages[1].Deleted);
            var delay = Assert.Single(messages[1].VisibilityChanges);
            Assert.InRange(delay, TimeSpan.FromTicks(1), FakeSqsQueue.VisibilityTimeout);
        }
        finally
        {
            // Never leave worker 1 blocked in the model (with an open connection) when an assertion above fails.
            gate.Release();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(messages[0].Deleted);

        await worker.DrainAsync(idle);

        Assert.Equal(0, idle.Calls);
        Assert.True(messages[1].Deleted);
        AssertSucceededOnce(await worker.ReadJobAsync(jobId), attempt: 1);
        Assert.Equal(ScriptedModel.TokensPerCall, await worker.UsageTokensAsync(userId));
        Assert.Empty(worker.Queue.Dlq);
    }

    [PostgresFact]
    public async Task Delete_failure_after_commit_redelivers_without_duplicating()
    {
        var worker = new WorkerHarness(fixture);
        var (_, jobId) = await worker.CreateJobAsync();
        worker.Queue.FailNextDeletes = 1;
        var model = new ScriptedModel(ScriptedModel.Valid(5));

        var deliveries = await worker.DrainAsync(model);

        Assert.Equal(2, deliveries);
        Assert.Equal(1, model.Calls);
        AssertSucceededOnce(await worker.ReadJobAsync(jobId), attempt: 1);
        Assert.Empty(worker.Queue.For(jobId));
        Assert.Empty(worker.Queue.Dlq);
    }
}
