using ExamMaker.Application.Ai.Generation;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

// Sweeps scan the whole table, so tests only assert on their own jobs, never on totals.
public sealed class EfAiJobRecoveryStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<Guid> RunningJobWithExpiredLeaseAsync(int attempt)
    {
        var userId = await AiTestData.CreateSubjectUserAsync(fixture);
        var jobId = await AiTestData.CreateQueuedJobAsync(fixture, userId);
        await AiTestData.ExecuteAsync(fixture, $"""
            UPDATE ai_generation_jobs
            SET status = 'Running', attempt = {attempt}, started_at = now(),
                lease_expires_at = now() - interval '1 second', last_enqueued_at = now() - interval '1 hour'
            WHERE id = {jobId}
            """);
        return jobId;
    }

    private async Task<Guid> QueuedJobAsync()
    {
        var userId = await AiTestData.CreateSubjectUserAsync(fixture);
        return await AiTestData.CreateQueuedJobAsync(fixture, userId);
    }

    private async Task<T> WithStoreAsync<T>(Func<EfAiJobWorkStore, Task<T>> action)
    {
        await using var context = fixture.CreateContext();
        return await action(new EfAiJobWorkStore(context));
    }

    private async Task<Domain.Ai.AiGenerationJob> JobAsync(Guid jobId)
    {
        await using var context = fixture.CreateContext();
        return await context.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    [PostgresFact]
    public async Task Expired_running_job_with_attempts_left_is_requeued_with_fresh_last_enqueued_at()
    {
        var jobId = await RunningJobWithExpiredLeaseAsync(attempt: 1);

        var result = await WithStoreAsync(s => s.RecoverExpiredLeasesAsync("x", CancellationToken.None));

        Assert.True(result.Requeued >= 1);
        var job = await JobAsync(jobId);
        Assert.Equal("Queued", job.Status);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(1, job.Attempt);
        Assert.True(job.LastEnqueuedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [PostgresFact]
    public async Task Expired_running_job_without_attempts_left_is_failed_with_recovery_audit()
    {
        var jobId = await RunningJobWithExpiredLeaseAsync(attempt: 3);

        var result = await WithStoreAsync(s => s.RecoverExpiredLeasesAsync("exhausted text", CancellationToken.None));

        Assert.True(result.Failed >= 1);
        var job = await JobAsync(jobId);
        Assert.Equal("Failed", job.Status);
        Assert.Equal(AiErrorCodes.AttemptsExhausted, job.ErrorCode);
        Assert.Equal("exhausted text", job.ErrorMessage);
        Assert.NotNull(job.FinishedAt);
        await using var context = fixture.CreateContext();
        var audit = await context.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == jobId);
        Assert.Null(audit.ActorId);
        Assert.Equal("AiJob", audit.EntityType);
        Assert.Equal("Failed", audit.Action);
        Assert.Equal("Running", audit.FromStatus);
        Assert.Equal("AI worker recovery; error=AttemptsExhausted", audit.Note);
        Assert.NotNull(audit.DepartmentId);
    }

    [PostgresFact]
    public async Task Exhausted_job_keeps_last_error_code_but_gets_final_message()
    {
        var jobId = await RunningJobWithExpiredLeaseAsync(attempt: 3);
        await AiTestData.ExecuteAsync(fixture, $"""
            UPDATE ai_generation_jobs SET error_code = 'Throttled', error_message = 'retry text' WHERE id = {jobId}
            """);

        await WithStoreAsync(s => s.RecoverExpiredLeasesAsync("exhausted text", CancellationToken.None));

        var job = await JobAsync(jobId);
        Assert.Equal("Throttled", job.ErrorCode);
        Assert.Equal("exhausted text", job.ErrorMessage);
    }

    [PostgresFact]
    public async Task Running_job_with_valid_lease_is_untouched()
    {
        var jobId = await RunningJobWithExpiredLeaseAsync(attempt: 1);
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET lease_expires_at = now() + interval '5 minutes' WHERE id = {jobId}");

        await WithStoreAsync(s => s.RecoverExpiredLeasesAsync("x", CancellationToken.None));

        Assert.Equal("Running", (await JobAsync(jobId)).Status);
    }

    [PostgresFact]
    public async Task Stale_queued_job_is_returned_once_and_last_enqueued_at_bumped()
    {
        var jobId = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET last_enqueued_at = now() - interval '11 minutes' WHERE id = {jobId}");

        var first = await WithStoreAsync(s => s.MarkStaleQueuedForResendAsync(TimeSpan.FromMinutes(10), CancellationToken.None));
        var second = await WithStoreAsync(s => s.MarkStaleQueuedForResendAsync(TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Contains(jobId, first);
        Assert.DoesNotContain(jobId, second);
        Assert.True((await JobAsync(jobId)).LastEnqueuedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [PostgresFact]
    public async Task Recent_queued_job_is_not_returned()
    {
        var jobId = await QueuedJobAsync();

        var ids = await WithStoreAsync(s => s.MarkStaleQueuedForResendAsync(TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.DoesNotContain(jobId, ids);
    }

    [PostgresFact]
    public async Task Count_includes_queued_job_older_than_age()
    {
        var before = await WithStoreAsync(s => s.CountQueuedOlderThanAsync(TimeSpan.FromMinutes(15), CancellationToken.None));
        var jobId = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET created_at = now() - interval '16 minutes' WHERE id = {jobId}");

        var after = await WithStoreAsync(s => s.CountQueuedOlderThanAsync(TimeSpan.FromMinutes(15), CancellationToken.None));

        Assert.Equal(before + 1, after);
    }
}
