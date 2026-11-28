using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiJobWorkStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(300);

    private async Task<T> WithStoreAsync<T>(Func<EfAiJobWorkStore, Task<T>> action)
    {
        await using var context = fixture.CreateContext();
        return await action(new EfAiJobWorkStore(context));
    }

    private Task<AiJobClaim?> ClaimAsync(Guid jobId) =>
        WithStoreAsync(s => s.TryClaimAsync(jobId, Lease, AiPromptBuilder.Version, CancellationToken.None));

    private async Task<Domain.Ai.AiGenerationJob> JobAsync(Guid jobId)
    {
        await using var context = fixture.CreateContext();
        return await context.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private async Task<(Guid UserId, Guid JobId)> QueuedJobAsync(string role = "teacher", bool isActive = true)
    {
        var userId = await AiTestData.CreateSubjectUserAsync(fixture, role, isActive);
        // The API never creates jobs for admins, but the worker must still reject them; create as teacher, then change.
        var jobUser = role == "teacher" && isActive ? userId : await AiTestData.CreateSubjectUserAsync(fixture);
        var jobId = await AiTestData.CreateQueuedJobAsync(fixture, jobUser);
        if (jobUser != userId)
            await AiTestData.ExecuteAsync(fixture, $"UPDATE ai_generation_jobs SET requested_by = {userId} WHERE id = {jobId}");
        return (userId, jobId);
    }

    private Task ExpireLeaseAsync(Guid jobId) =>
        AiTestData.ExecuteAsync(fixture, $"UPDATE ai_generation_jobs SET lease_expires_at = now() - interval '1 second' WHERE id = {jobId}");

    private static AiModelResponse Response(long input, long output) =>
        new("{}", AiStopReason.EndTurn, input, output, "fake-model");

    [PostgresFact]
    public async Task Claim_moves_queued_job_to_running_and_returns_attempt_1()
    {
        var (_, jobId) = await QueuedJobAsync();

        var claim = await ClaimAsync(jobId);

        Assert.Equal(new AiJobClaim(jobId, 1, 3), claim);
        var job = await JobAsync(jobId);
        Assert.Equal("Running", job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.NotNull(job.StartedAt);
        Assert.Equal("question-gen-v1", job.PromptVersion);
        Assert.InRange(job.LeaseExpiresAt!.Value, DateTime.UtcNow.AddSeconds(290), DateTime.UtcNow.AddSeconds(310));
    }

    [PostgresFact]
    public async Task Second_claim_while_lease_valid_returns_null_and_miss_is_HeldByOther()
    {
        var (_, jobId) = await QueuedJobAsync();
        Assert.NotNull(await ClaimAsync(jobId));

        Assert.Null(await ClaimAsync(jobId));

        var miss = await WithStoreAsync(s => s.DescribeMissAsync(jobId, CancellationToken.None));
        Assert.Equal(AiClaimMissReason.HeldByOther, miss.Reason);
        Assert.InRange(miss.RemainingLease, TimeSpan.FromSeconds(1), Lease);
    }

    [PostgresFact]
    public async Task Claim_succeeds_on_running_job_with_expired_lease()
    {
        var (_, jobId) = await QueuedJobAsync();
        await ClaimAsync(jobId);
        var startedAt = (await JobAsync(jobId)).StartedAt;
        await ExpireLeaseAsync(jobId);

        var claim = await ClaimAsync(jobId);

        Assert.Equal(2, claim!.Attempt);
        Assert.Equal(startedAt, (await JobAsync(jobId)).StartedAt);
    }

    [PostgresFact]
    public async Task Claim_returns_null_when_attempts_exhausted_and_miss_is_AttemptsExhausted()
    {
        var (_, jobId) = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture, $"UPDATE ai_generation_jobs SET attempt = 3 WHERE id = {jobId}");

        Assert.Null(await ClaimAsync(jobId));

        var miss = await WithStoreAsync(s => s.DescribeMissAsync(jobId, CancellationToken.None));
        Assert.Equal(AiClaimMissReason.AttemptsExhausted, miss.Reason);
    }

    [PostgresFact]
    public async Task Miss_is_Finished_for_succeeded_or_failed_job()
    {
        var (_, succeeded) = await QueuedJobAsync();
        var (_, failed) = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET status = 'Succeeded', finished_at = now() WHERE id = {succeeded}");
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET status = 'Failed', error_code = 'Throttled', finished_at = now() WHERE id = {failed}");

        foreach (var jobId in new[] { succeeded, failed })
        {
            Assert.Null(await ClaimAsync(jobId));
            var miss = await WithStoreAsync(s => s.DescribeMissAsync(jobId, CancellationToken.None));
            Assert.Equal(AiClaimMissReason.Finished, miss.Reason);
        }
    }

    [PostgresFact]
    public async Task Miss_is_NotFound_for_unknown_id()
    {
        var miss = await WithStoreAsync(s => s.DescribeMissAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(AiClaimMissReason.NotFound, miss.Reason);
    }

    [PostgresFact]
    public async Task Concurrent_claims_only_one_wins()
    {
        var (_, jobId) = await QueuedJobAsync();

        var claims = await Task.WhenAll(ClaimAsync(jobId), ClaimAsync(jobId));

        Assert.Single(claims, c => c is not null);
    }

    [PostgresFact]
    public async Task Load_context_returns_names_and_subject_department()
    {
        var (userId, jobId) = await QueuedJobAsync();
        var claim = (await ClaimAsync(jobId))!;

        var load = await WithStoreAsync(s => s.LoadContextAsync(claim, CancellationToken.None));

        Assert.Null(load.FailureCode);
        var request = AiTestData.Request();
        await using var context = fixture.CreateContext();
        var subject = await context.Subjects.AsNoTracking().SingleAsync(s => s.Id == request.SubjectId);
        var topic = await context.Topics.AsNoTracking().SingleAsync(t => t.Id == request.TopicId);
        Assert.Equal(new AiJobContext(jobId, userId, subject.DepartmentId, request, subject.Name, topic.Name), load.Context);
    }

    [PostgresFact]
    public async Task Load_context_fails_RequesterUnavailable_for_inactive_user()
    {
        var (_, jobId) = await QueuedJobAsync(isActive: false);
        await AssertLoadFailsAsync(jobId, AiErrorCodes.RequesterUnavailable);
    }

    [PostgresFact]
    public async Task Load_context_fails_RequesterUnavailable_for_other_department()
    {
        var (userId, jobId) = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture, $"""
            UPDATE users SET department_id = (SELECT id FROM departments WHERE id <> users.department_id LIMIT 1)
            WHERE id = {userId}
            """);
        await AssertLoadFailsAsync(jobId, AiErrorCodes.RequesterUnavailable);
    }

    [PostgresFact]
    public async Task Load_context_fails_RequesterUnavailable_for_admin_role()
    {
        var (_, jobId) = await QueuedJobAsync(role: "admin");
        await AssertLoadFailsAsync(jobId, AiErrorCodes.RequesterUnavailable);
    }

    [PostgresFact]
    public async Task Load_context_fails_InvalidRequest_for_unparseable_params()
    {
        var (_, jobId) = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture, $"UPDATE ai_generation_jobs SET params = '{{\"x\":1}}' WHERE id = {jobId}");
        await AssertLoadFailsAsync(jobId, AiErrorCodes.InvalidRequest);
    }

    private async Task AssertLoadFailsAsync(Guid jobId, string code)
    {
        var claim = (await ClaimAsync(jobId))!;
        var load = await WithStoreAsync(s => s.LoadContextAsync(claim, CancellationToken.None));
        Assert.Null(load.Context);
        Assert.Equal(code, load.FailureCode);
    }

    [PostgresFact]
    public async Task Extend_lease_with_stale_attempt_returns_false_and_changes_nothing()
    {
        var (_, jobId) = await QueuedJobAsync();
        var first = (await ClaimAsync(jobId))!;
        await ExpireLeaseAsync(jobId);
        await ClaimAsync(jobId);
        var before = await JobAsync(jobId);

        Assert.False(await WithStoreAsync(s => s.ExtendLeaseAsync(first, Lease, CancellationToken.None)));

        Assert.Equal(before.LeaseExpiresAt, (await JobAsync(jobId)).LeaseExpiresAt);
    }

    [PostgresFact]
    public async Task Extend_lease_with_current_attempt_moves_expiry_forward()
    {
        var (_, jobId) = await QueuedJobAsync();
        var claim = (await ClaimAsync(jobId))!;
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET lease_expires_at = now() + interval '10 seconds' WHERE id = {jobId}");

        Assert.True(await WithStoreAsync(s => s.ExtendLeaseAsync(claim, Lease, CancellationToken.None)));

        Assert.True((await JobAsync(jobId)).LeaseExpiresAt > DateTime.UtcNow.AddSeconds(290));
    }

    [PostgresFact]
    public async Task Extend_lease_does_not_run_inside_the_jobs_open_transaction()
    {
        // The heartbeat runs concurrently with the job's own DB work, so it must not share the job's context/connection.
        var (_, jobId) = await QueuedJobAsync();
        var claim = (await ClaimAsync(jobId))!;
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET lease_expires_at = now() + interval '10 seconds' WHERE id = {jobId}");
        await using var context = fixture.CreateContext();
        var store = new EfAiJobWorkStore(context);
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.True(await store.ExtendLeaseAsync(claim, TimeSpan.FromSeconds(300), CancellationToken.None));
            await transaction.RollbackAsync();
        }

        Assert.True((await JobAsync(jobId)).LeaseExpiresAt > DateTime.UtcNow.AddSeconds(290));
    }

    [PostgresFact]
    public async Task Record_usage_adds_tokens_to_job_and_daily_row_without_touching_jobs()
    {
        var (userId, jobId) = await QueuedJobAsync();
        var date = AiTestData.UniqueDate();

        await WithStoreAsync(async s => { await s.RecordUsageAsync(jobId, userId, date, Response(100, 50), CancellationToken.None); return 0; });
        await WithStoreAsync(async s => { await s.RecordUsageAsync(jobId, userId, date, Response(30, 20), CancellationToken.None); return 0; });

        var job = await JobAsync(jobId);
        Assert.Equal(130, job.InputTokens);
        Assert.Equal(70, job.OutputTokens);
        Assert.Equal("fake-model", job.ModelId);
        await using var context = fixture.CreateContext();
        var usage = await context.AiUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == userId && u.Date == date);
        Assert.Equal(200, usage.Tokens);
        Assert.Equal(0, usage.Jobs);
    }

    [PostgresFact]
    public async Task Release_for_retry_sets_queued_clears_lease_stores_error_and_bumps_last_enqueued_at()
    {
        var (_, jobId) = await QueuedJobAsync();
        var claim = (await ClaimAsync(jobId))!;
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET last_enqueued_at = now() - interval '1 hour' WHERE id = {jobId}");

        Assert.True(await WithStoreAsync(s => s.ReleaseForRetryAsync(claim, "Throttled", "retry text", CancellationToken.None)));

        var job = await JobAsync(jobId);
        Assert.Equal("Queued", job.Status);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal("Throttled", job.ErrorCode);
        Assert.Equal("retry text", job.ErrorMessage);
        Assert.Equal(1, job.Attempt);
        Assert.True(job.LastEnqueuedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [PostgresFact]
    public async Task Release_on_shutdown_decrements_attempt()
    {
        var (_, jobId) = await QueuedJobAsync();
        var claim = (await ClaimAsync(jobId))!;

        Assert.True(await WithStoreAsync(s => s.ReleaseOnShutdownAsync(claim, CancellationToken.None)));

        var job = await JobAsync(jobId);
        Assert.Equal("Queued", job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Null(job.LeaseExpiresAt);
    }

    [PostgresFact]
    public async Task Fail_sets_failed_with_code_message_finished_at_and_writes_AiJob_audit()
    {
        var (_, jobId) = await QueuedJobAsync();
        var claim = (await ClaimAsync(jobId))!;

        Assert.True(await WithStoreAsync(s => s.FailAsync(claim, "Throttled", "failed text", CancellationToken.None)));

        var job = await JobAsync(jobId);
        Assert.Equal("Failed", job.Status);
        Assert.Equal("Throttled", job.ErrorCode);
        Assert.Equal("failed text", job.ErrorMessage);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.LeaseExpiresAt);
        await AssertJobAuditAsync(jobId, "Running", "AI worker; error=Throttled");
    }

    [PostgresFact]
    public async Task Fail_with_stale_attempt_returns_false()
    {
        var (_, jobId) = await QueuedJobAsync();
        var first = (await ClaimAsync(jobId))!;
        await ExpireLeaseAsync(jobId);
        await ClaimAsync(jobId);

        Assert.False(await WithStoreAsync(s => s.FailAsync(first, "Throttled", "x", CancellationToken.None)));

        Assert.Equal("Running", (await JobAsync(jobId)).Status);
    }

    [PostgresFact]
    public async Task Fail_exhausted_keeps_existing_error_code()
    {
        var (_, jobId) = await QueuedJobAsync();
        await AiTestData.ExecuteAsync(fixture, $"""
            UPDATE ai_generation_jobs SET attempt = 3, error_code = 'Throttled', error_message = 'old text' WHERE id = {jobId}
            """);

        await WithStoreAsync(async s => { await s.FailExhaustedAsync(jobId, "exhausted text", CancellationToken.None); return 0; });

        var job = await JobAsync(jobId);
        Assert.Equal("Failed", job.Status);
        Assert.Equal("Throttled", job.ErrorCode);
        Assert.Equal("exhausted text", job.ErrorMessage);
        await AssertJobAuditAsync(jobId, "Queued", "AI worker; error=Throttled");
    }

    [PostgresFact]
    public async Task Fail_exhausted_uses_AttemptsExhausted_when_no_error_recorded()
    {
        var (_, jobId) = await QueuedJobAsync();
        await ClaimAsync(jobId);
        await AiTestData.ExecuteAsync(fixture, $"UPDATE ai_generation_jobs SET attempt = 3 WHERE id = {jobId}");
        await ExpireLeaseAsync(jobId);

        await WithStoreAsync(async s => { await s.FailExhaustedAsync(jobId, "exhausted text", CancellationToken.None); return 0; });

        var job = await JobAsync(jobId);
        Assert.Equal("Failed", job.Status);
        Assert.Equal("AttemptsExhausted", job.ErrorCode);
        Assert.Equal("exhausted text", job.ErrorMessage);
        await AssertJobAuditAsync(jobId, "Running", "AI worker; error=AttemptsExhausted");
    }

    [PostgresFact]
    public async Task Fail_exhausted_does_nothing_when_attempts_remain()
    {
        var (_, jobId) = await QueuedJobAsync();

        await WithStoreAsync(async s => { await s.FailExhaustedAsync(jobId, "x", CancellationToken.None); return 0; });

        Assert.Equal("Queued", (await JobAsync(jobId)).Status);
    }

    private async Task AssertJobAuditAsync(Guid jobId, string fromStatus, string note)
    {
        await using var context = fixture.CreateContext();
        var departmentId = await context.Subjects
            .Where(s => s.Id == AiTestData.Request().SubjectId).Select(s => s.DepartmentId).SingleAsync();
        var audit = await context.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == jobId);
        Assert.Null(audit.ActorId);
        Assert.Equal("AiJob", audit.EntityType);
        Assert.Equal("Failed", audit.Action);
        Assert.Equal(fromStatus, audit.FromStatus);
        Assert.Equal("Failed", audit.ToStatus);
        Assert.Equal(departmentId, audit.DepartmentId);
        Assert.Equal(note, audit.Note);
    }
}
