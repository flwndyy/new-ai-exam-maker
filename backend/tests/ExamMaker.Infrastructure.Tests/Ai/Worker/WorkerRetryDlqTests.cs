using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>
/// Retry and DLQ behaviour of docs/context.md 4.1.1: backoff through ChangeMessageVisibility (30 s, 120 s), at most
/// three attempts, and a message that keeps coming back from dead workers ends in the DLQ while the job ends Failed.
/// </summary>
public sealed class WorkerRetryDlqTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact]
    public async Task Retryable_failures_back_off_then_succeed()
    {
        var worker = new WorkerHarness(fixture);
        var (_, jobId) = await worker.CreateJobAsync();
        var message = Assert.Single(worker.Queue.For(jobId));
        var model = new ScriptedModel(
            ScriptedModel.Fail(AiModelErrorKind.Throttled),
            ScriptedModel.Fail(AiModelErrorKind.ServiceUnavailable),
            ScriptedModel.Valid(5));

        Assert.Equal(3, await worker.DrainAsync(model));

        Assert.Equal(3, model.Calls);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)], message.VisibilityChanges);
        Assert.True(message.Deleted);
        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Succeeded", state.Status);
        Assert.Equal(3, state.Attempt);
        Assert.Null(state.ErrorCode);
        Assert.Equal([1, 2, 3, 4, 5], state.Ordinals);
        Assert.Empty(worker.Queue.Dlq);
    }

    [PostgresFact]
    public async Task Retryable_failure_on_every_attempt_fails_job_without_dlq()
    {
        var worker = new WorkerHarness(fixture);
        var (_, jobId) = await worker.CreateJobAsync();
        var message = Assert.Single(worker.Queue.For(jobId));
        var model = new ScriptedModel(
            ScriptedModel.Fail(AiModelErrorKind.Throttled),
            ScriptedModel.Fail(AiModelErrorKind.Throttled),
            ScriptedModel.Fail(AiModelErrorKind.ServiceUnavailable));

        Assert.Equal(3, await worker.DrainAsync(model));

        Assert.Equal(3, model.Calls);
        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Failed", state.Status);
        Assert.Equal(3, state.Attempt);
        Assert.Equal(AiErrorCodes.ServiceUnavailable, state.ErrorCode);
        // Out of attempts: the stored text must no longer promise another try.
        Assert.False(string.IsNullOrWhiteSpace(state.ErrorMessage));
        Assert.DoesNotContain("sẽ thử lại", state.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(state.Ordinals);
        Assert.Equal("AI worker; error=ServiceUnavailable", state.JobAuditNotes[^1]);
        Assert.True(message.Deleted);
        Assert.Empty(worker.Queue.Dlq);
    }

    [PostgresFact]
    public async Task Worker_dying_on_every_receive_sends_message_to_dlq_and_recovery_fails_job()
    {
        var worker = new WorkerHarness(fixture);
        var (_, jobId) = await worker.CreateJobAsync();

        for (var receive = 1; receive <= 3; receive++)
        {
            var message = Assert.Single(worker.Queue.Receive(), m => m.JobId == jobId);
            Assert.Equal(receive, message.ReceiveCount);
            var claim = await worker.CrashAfterClaimAsync(jobId);
            Assert.Equal(receive, claim.Attempt);
            await worker.ExpireLeaseAsync(jobId);
            worker.Time.Advance(FakeSqsQueue.VisibilityTimeout);
        }

        Assert.DoesNotContain(worker.Queue.Receive(), m => m.JobId == jobId);
        Assert.Single(worker.Queue.Dlq, m => m.JobId == jobId);
        Assert.Equal("Running", (await worker.ReadJobAsync(jobId)).Status);

        await worker.RunRecoveryAsync();

        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Failed", state.Status);
        Assert.Equal(3, state.Attempt);
        Assert.Equal(AiErrorCodes.AttemptsExhausted, state.ErrorCode);
        Assert.StartsWith("AI worker recovery; error=AttemptsExhausted", state.JobAuditNotes[^1], StringComparison.Ordinal);
        Assert.Empty(state.Ordinals);
    }
}
