using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Questions;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Worker-side persistence for AI jobs. Claim and every later write use conditional SQL so that two workers can never
/// both own a job: the attempt number returned by the claim fences all writes after it (docs/context.md 4.1.2, B6).
/// </summary>
public sealed class EfAiJobWorkStore(ExamMakerDbContext db) : IAiJobWorkStore, IAiJobRecoveryStore
{
    private const string WorkerNote = "AI worker";
    private const string RecoveryNote = "AI worker recovery";

    public async Task<AiJobClaim?> TryClaimAsync(Guid jobId, TimeSpan lease, string promptVersion, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClaimRow>($"""
            UPDATE ai_generation_jobs
            SET status = 'Running', attempt = attempt + 1,
                lease_expires_at = now() + {lease.TotalSeconds} * interval '1 second',
                started_at = COALESCE(started_at, now()),
                prompt_version = {promptVersion}
            WHERE id = {jobId} AND attempt < max_attempts
              AND (status = 'Queued' OR (status = 'Running' AND lease_expires_at < now()))
            RETURNING attempt AS "Attempt", max_attempts AS "MaxAttempts"
            """).ToListAsync(ct);

        return rows.Count == 0 ? null : new AiJobClaim(jobId, rows[0].Attempt, rows[0].MaxAttempts);
    }

    public async Task<AiClaimMiss> DescribeMissAsync(Guid jobId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<MissRow>($"""
            SELECT status AS "Status", attempt AS "Attempt", max_attempts AS "MaxAttempts",
                   lease_expires_at AS "LeaseExpiresAt", now() AS "Now"
            FROM ai_generation_jobs WHERE id = {jobId}
            """).ToListAsync(ct);

        if (rows.Count == 0) return new AiClaimMiss(AiClaimMissReason.NotFound, TimeSpan.Zero);
        var row = rows[0];
        if (row.Status is "Succeeded" or "Failed") return new AiClaimMiss(AiClaimMissReason.Finished, TimeSpan.Zero);
        if (row.Status == "Running" && row.LeaseExpiresAt > row.Now)
            return new AiClaimMiss(AiClaimMissReason.HeldByOther, row.LeaseExpiresAt.Value - row.Now);
        if (row.Attempt >= row.MaxAttempts) return new AiClaimMiss(AiClaimMissReason.AttemptsExhausted, TimeSpan.Zero);

        // Claimable again (released between our claim and this read): let the message come back shortly.
        return new AiClaimMiss(AiClaimMissReason.HeldByOther, TimeSpan.Zero);
    }

    public async Task<AiJobContextLoad> LoadContextAsync(AiJobClaim claim, CancellationToken ct)
    {
        var job = await db.AiGenerationJobs.AsNoTracking()
            .Where(j => j.Id == claim.JobId)
            .Select(j => new { j.RequestedBy, j.Params })
            .SingleAsync(ct);

        AiGenerateRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AiGenerateRequest>(job.Params, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null) return Failure(AiErrorCodes.InvalidRequest);

        var subject = await db.Subjects.AsNoTracking()
            .Where(s => s.Id == request.SubjectId)
            .Select(s => new { s.Name, s.DepartmentId })
            .SingleOrDefaultAsync(ct);
        var topicName = await db.Topics.AsNoTracking()
            .Where(t => t.Id == request.TopicId && t.SubjectId == request.SubjectId && t.Grade == request.Grade)
            .Select(t => t.Name)
            .SingleOrDefaultAsync(ct);
        if (subject is null || topicName is null) return Failure(AiErrorCodes.InvalidRequest);

        if (!await IsEligibleAsync(job.RequestedBy, subject.DepartmentId, ct))
            return Failure(AiErrorCodes.RequesterUnavailable);

        return new AiJobContextLoad(
            new AiJobContext(claim.JobId, job.RequestedBy, subject.DepartmentId, request, subject.Name, topicName),
            null);
    }

    /// <summary>
    /// Called by the heartbeat while the job itself uses this store, so it runs on its own short-lived context: a
    /// DbContext is not thread-safe, and sharing the connection would put the lease update inside the job's transaction.
    /// </summary>
    public async Task<bool> ExtendLeaseAsync(AiJobClaim claim, TimeSpan lease, CancellationToken ct)
    {
        var options = (DbContextOptions<ExamMakerDbContext>)db.GetService<IDbContextOptions>();
        await using var leaseContext = new ExamMakerDbContext(options);
        return await leaseContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_generation_jobs
            SET lease_expires_at = now() + {lease.TotalSeconds} * interval '1 second'
            WHERE id = {claim.JobId} AND status = 'Running' AND attempt = {claim.Attempt}
            """, ct) == 1;
    }

    public async Task RecordUsageAsync(
        Guid jobId, Guid userId, DateOnly usageDate, AiModelResponse response, CancellationToken ct)
    {
        var tokens = response.InputTokens + response.OutputTokens;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_generation_jobs
            SET input_tokens = input_tokens + {response.InputTokens},
                output_tokens = output_tokens + {response.OutputTokens},
                model_id = {response.ModelId}
            WHERE id = {jobId}
            """, ct);
        // Tokens count on the day of the call; the job slot was already reserved by the API.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_usage_daily (user_id, date, jobs, tokens)
            VALUES ({userId}, {usageDate}, 0, {tokens})
            ON CONFLICT (user_id, date) DO UPDATE SET tokens = ai_usage_daily.tokens + EXCLUDED.tokens
            """, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> ReleaseForRetryAsync(AiJobClaim claim, string code, string message, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_generation_jobs
            SET status = 'Queued', lease_expires_at = NULL, last_enqueued_at = now(),
                error_code = {code}, error_message = {message}
            WHERE id = {claim.JobId} AND status = 'Running' AND attempt = {claim.Attempt}
            """, ct) == 1;

    public async Task<bool> ReleaseOnShutdownAsync(AiJobClaim claim, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_generation_jobs
            SET status = 'Queued', lease_expires_at = NULL, last_enqueued_at = now(), attempt = attempt - 1
            WHERE id = {claim.JobId} AND status = 'Running' AND attempt = {claim.Attempt}
            """, ct) == 1;

    public async Task<bool> FailAsync(AiJobClaim claim, string code, string message, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Database.SqlQuery<FailedRow>($"""
            UPDATE ai_generation_jobs j
            SET status = 'Failed', lease_expires_at = NULL, finished_at = now(),
                error_code = {code}, error_message = {message}
            WHERE j.id = {claim.JobId} AND j.status = 'Running' AND j.attempt = {claim.Attempt}
            RETURNING j.id AS "Id", 'Running' AS "FromStatus", j.error_code AS "ErrorCode",
                      (SELECT s.department_id FROM subjects s WHERE s.id::text = lower(j.params ->> 'subjectId')) AS "DepartmentId"
            """).ToListAsync(ct);
        if (rows.Count == 0) return false;

        AddFailedAudit(claim.JobId, rows[0], WorkerNote);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task FailExhaustedAsync(Guid jobId, string message, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Database.SqlQuery<FailedRow>($"""
            WITH target AS (
                SELECT id, status FROM ai_generation_jobs
                WHERE id = {jobId} AND attempt >= max_attempts
                  AND (status = 'Queued' OR (status = 'Running' AND lease_expires_at < now()))
                FOR UPDATE)
            UPDATE ai_generation_jobs j
            SET status = 'Failed', lease_expires_at = NULL, finished_at = now(),
                error_code = COALESCE(j.error_code, {AiErrorCodes.AttemptsExhausted}),
                error_message = {message}
            FROM target WHERE j.id = target.id
            RETURNING j.id AS "Id", target.status AS "FromStatus", j.error_code AS "ErrorCode",
                      (SELECT s.department_id FROM subjects s WHERE s.id::text = lower(j.params ->> 'subjectId')) AS "DepartmentId"
            """).ToListAsync(ct);
        if (rows.Count == 0) return;

        AddFailedAudit(jobId, rows[0], WorkerNote);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<AiCompleteResult> CompleteAsync(
        AiJobClaim claim, AiJobContext context, IReadOnlyList<AiStoredQuestion> questions, int duplicatesDropped,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);

        // Re-check the requester under a share lock so a concurrent deactivation or department move cannot interleave.
        var requester = await db.Database.SqlQuery<RequesterRow>($"""
            SELECT is_active AS "IsActive", role AS "Role", department_id AS "DepartmentId"
            FROM users WHERE id = {context.RequesterId} FOR SHARE
            """).ToListAsync(ct);
        if (requester.Count == 0 || !requester[0].IsActive || requester[0].Role is not ("teacher" or "head")
            || requester[0].DepartmentId != context.DepartmentId)
            return AiCompleteResult.RequesterUnavailable;

        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_generation_jobs
            SET status = 'Succeeded', finished_at = now(), lease_expires_at = NULL,
                error_code = NULL, error_message = NULL, duplicates_dropped = {duplicatesDropped}
            WHERE id = {claim.JobId} AND status = 'Running' AND attempt = {claim.Attempt}
            """, ct);
        if (updated != 1) return AiCompleteResult.LeaseLost;

        // PostgreSQL stores microseconds; truncate so the in-memory and stored timestamps agree (same rule as A6).
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var request = context.Request;
        foreach (var stored in questions)
        {
            var question = new Question
            {
                Id = Guid.NewGuid(),
                SubjectId = request.SubjectId,
                TopicId = request.TopicId,
                DepartmentId = context.DepartmentId,
                Grade = request.Grade,
                Type = request.Type,
                Difficulty = request.Difficulty,
                Stem = stored.Question.Stem,
                Payload = stored.Question.Payload.GetRawText(),
                Explanation = stored.Question.Explanation,
                Status = "Draft",
                Revision = 1,
                Source = "AI",
                AiJobId = claim.JobId,
                AiOrdinal = stored.Question.Ordinal,
                AiVerification = stored.Verification.Verification,
                AiVerificationNote = stored.Verification.Note,
                AiReviewedByAuthor = false,
                CreatedBy = context.RequesterId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Questions.Add(question);
            db.AuditLogs.Add(AuditLog.Create(
                null, "Question", question.Id, context.DepartmentId, "CreateQuestion", null, "Draft",
                $"{WorkerNote}; job={claim.JobId}; ordinal={stored.Question.Ordinal}; revision=1"));
        }

        db.AuditLogs.Add(AuditLog.Create(
            null, "AiJob", claim.JobId, context.DepartmentId, "Succeeded", "Running", "Succeeded",
            $"{WorkerNote}; questions={questions.Count}; duplicatesDropped={duplicatesDropped}"));
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            // The transaction rolls back, but the entities stay Added in the change tracker; without this, a following
            // FailAsync on the same context would save these questions and the Succeeded audit under a Failed job.
            db.ChangeTracker.Clear();
            throw;
        }

        return AiCompleteResult.Completed;
    }

    private async Task<bool> IsEligibleAsync(Guid userId, Guid departmentId, CancellationToken ct) =>
        await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive
            && (u.Role == "teacher" || u.Role == "head") && u.DepartmentId == departmentId, ct);

    private void AddFailedAudit(Guid jobId, FailedRow row, string source) =>
        db.AuditLogs.Add(AuditLog.Create(
            null, "AiJob", jobId, row.DepartmentId, "Failed", row.FromStatus, "Failed",
            $"{source}; error={row.ErrorCode}"));

    private static AiJobContextLoad Failure(string code) => new(null, code);

    public async Task<AiExpiredLeaseResult> RecoverExpiredLeasesAsync(string exhaustedMessage, CancellationToken ct)
    {
        // The SQS message of the dead worker comes back by itself; last_enqueued_at = now() delays a duplicate re-send.
        var requeued = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_generation_jobs
            SET status = 'Queued', lease_expires_at = NULL, last_enqueued_at = now()
            WHERE status = 'Running' AND lease_expires_at < now() AND attempt < max_attempts
            """, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var failed = await db.Database.SqlQuery<FailedRow>($"""
            UPDATE ai_generation_jobs j
            SET status = 'Failed', lease_expires_at = NULL, finished_at = now(),
                error_code = COALESCE(j.error_code, {AiErrorCodes.AttemptsExhausted}),
                error_message = {exhaustedMessage}
            WHERE j.status = 'Running' AND j.lease_expires_at < now() AND j.attempt >= j.max_attempts
            RETURNING j.id AS "Id", 'Running' AS "FromStatus", j.error_code AS "ErrorCode",
                      (SELECT s.department_id FROM subjects s WHERE s.id::text = lower(j.params ->> 'subjectId')) AS "DepartmentId"
            """).ToListAsync(ct);
        foreach (var row in failed)
            AddFailedAudit(row.Id, row, RecoveryNote);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AiExpiredLeaseResult(requeued, failed.Count);
    }

    public async Task<IReadOnlyList<Guid>> MarkStaleQueuedForResendAsync(TimeSpan olderThan, CancellationToken ct) =>
        await db.Database.SqlQuery<Guid>($"""
            UPDATE ai_generation_jobs
            SET last_enqueued_at = now()
            WHERE status = 'Queued' AND last_enqueued_at < now() - {olderThan.TotalSeconds} * interval '1 second'
            RETURNING id AS "Value"
            """).ToListAsync(ct);

    public async Task<int> CountQueuedOlderThanAsync(TimeSpan age, CancellationToken ct) =>
        (await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM ai_generation_jobs
            WHERE status = 'Queued' AND created_at < now() - {age.TotalSeconds} * interval '1 second'
            """).ToListAsync(ct))[0];

    private sealed class ClaimRow
    {
        public int Attempt { get; set; }
        public int MaxAttempts { get; set; }
    }

    private sealed class MissRow
    {
        public string Status { get; set; } = string.Empty;
        public int Attempt { get; set; }
        public int MaxAttempts { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public DateTime Now { get; set; }
    }

    private sealed class RequesterRow
    {
        public bool IsActive { get; set; }
        public string Role { get; set; } = string.Empty;
        public Guid? DepartmentId { get; set; }
    }

    private sealed class FailedRow
    {
        public Guid Id { get; set; }
        public string FromStatus { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public Guid? DepartmentId { get; set; }
    }
}
