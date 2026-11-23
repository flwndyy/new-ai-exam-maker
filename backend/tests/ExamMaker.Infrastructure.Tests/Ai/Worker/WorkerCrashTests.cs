using ExamMaker.Application.Ai.Generation;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>
/// Crashes in the middle of a job (docs/context.md 4.1.1): results and job state commit together or not at all, a dead
/// worker's job is recovered and re-sent, and the dead worker can no longer write once another one has taken over.
/// </summary>
public sealed class WorkerCrashTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact(Timeout = 60_000)]
    public async Task Failure_inside_save_transaction_leaves_nothing_and_retry_completes_once()
    {
        var worker = new WorkerHarness(fixture);
        var (userId, jobId) = await worker.CreateJobAsync();

        // SHARE on questions lets the duplicate-key read through but makes the question INSERT wait out lock_timeout
        // (2 s) — after the save transaction has already set the job Succeeded. Nothing of that may survive.
        await using (var blocker = new NpgsqlConnection(fixture.ConnectionString))
        {
            await blocker.OpenAsync();
            await using var lockTransaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("LOCK TABLE questions IN SHARE MODE", blocker, lockTransaction))
            {
                await command.ExecuteNonQueryAsync();
            }

            var message = Assert.Single(worker.Queue.Receive());
            await worker.HandleAsync(message, new ScriptedModel(ScriptedModel.Valid(5)));

            var failed = await worker.ReadJobAsync(jobId);
            Assert.Equal("Queued", failed.Status);
            Assert.Equal(1, failed.Attempt);
            Assert.Equal(AiErrorCodes.Unknown, failed.ErrorCode);
            Assert.Empty(failed.Ordinals);
            Assert.DoesNotContain(failed.JobAuditNotes, n => n.Contains("questions=", StringComparison.Ordinal));
            Assert.Equal([TimeSpan.FromSeconds(30)], message.VisibilityChanges);
            Assert.False(message.Deleted);

            await lockTransaction.RollbackAsync();
        }

        var model = new ScriptedModel(ScriptedModel.Valid(5));
        await worker.DrainAsync(model);

        Assert.Equal(1, model.Calls);
        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Succeeded", state.Status);
        Assert.Equal(2, state.Attempt);
        Assert.Equal([1, 2, 3, 4, 5], state.Ordinals);
        Assert.Single(state.JobAuditNotes, n => n.StartsWith("AI worker; questions=5", StringComparison.Ordinal));
        // Both generation calls were paid for, including the one whose results were rolled back.
        Assert.Equal(2 * ScriptedModel.TokensPerCall, await worker.UsageTokensAsync(userId));
        Assert.Empty(worker.Queue.Dlq);
    }

    private async Task<DateTime?> LeaseAsync(Guid jobId)
    {
        await using var db = fixture.CreateContext();
        return await db.AiGenerationJobs.AsNoTracking()
            .Where(j => j.Id == jobId).Select(j => j.LeaseExpiresAt).SingleAsync();
    }

    [PostgresFact(Timeout = 60_000)]
    public async Task Dead_worker_is_recovered_resent_and_zombie_writes_are_rejected()
    {
        var worker = new WorkerHarness(fixture);
        var (_, jobId) = await worker.CreateJobAsync();
        var original = Assert.Single(worker.Queue.Receive());
        var stale = await worker.CrashAfterClaimAsync(jobId);
        await worker.ExpireLeaseAsync(jobId);

        await worker.RunRecoveryAsync();
        Assert.Equal("Queued", (await worker.ReadJobAsync(jobId)).Status);
        Assert.Single(worker.Queue.For(jobId));

        // No worker picked the job up for longer than RequeueAfterMinutes: recovery sends a fresh message.
        await worker.AgeLastEnqueuedAsync(jobId, TimeSpan.FromMinutes(11));
        await worker.RunRecoveryAsync();
        var resent = Assert.Single(worker.Queue.For(jobId), m => !ReferenceEquals(m, original));
        Assert.Equal(0, resent.ReceiveCount);

        // The new worker claims attempt 2 and blocks in the model; the dead worker's late writes must bounce off it.
        var gate = new ModelGate();
        var model = new ScriptedModel(ScriptedModel.Gated(gate, 5));
        var drain = worker.DrainAsync(model);
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            var leaseBefore = await LeaseAsync(jobId);

            await using (var db = fixture.CreateContext())
            {
                var zombie = new EfAiJobWorkStore(db);
                Assert.False(await zombie.FailAsync(stale, AiErrorCodes.Unknown, "zombie", CancellationToken.None));
                Assert.False(await zombie.ReleaseForRetryAsync(stale, AiErrorCodes.Unknown, "zombie", CancellationToken.None));
                Assert.False(await zombie.ExtendLeaseAsync(stale, TimeSpan.FromHours(1), CancellationToken.None));
            }

            var running = await worker.ReadJobAsync(jobId);
            Assert.Equal("Running", running.Status);
            Assert.Equal(2, running.Attempt);
            Assert.NotEqual(AiErrorCodes.Unknown, running.ErrorCode);
            Assert.Equal(leaseBefore, await LeaseAsync(jobId));
        }
        finally
        {
            gate.Release();
        }

        await drain.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, model.Calls);
        Assert.True(resent.Deleted);
        Assert.True(original.Deleted);

        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Succeeded", state.Status);
        Assert.Equal(2, state.Attempt);
        Assert.Equal([1, 2, 3, 4, 5], state.Ordinals);
        Assert.Single(state.JobAuditNotes, n => n.StartsWith("AI worker; questions=5", StringComparison.Ordinal));
        Assert.Empty(worker.Queue.Dlq);
    }
}
