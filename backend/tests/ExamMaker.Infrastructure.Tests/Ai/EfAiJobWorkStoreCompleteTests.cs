using System.Text.Json;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Verification;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiJobWorkStoreCompleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly AiVerificationOutcome NotChecked = new(AiVerificationValues.NotChecked, null);

    private async Task<(AiJobClaim Claim, AiJobContext Context)> ClaimedJobAsync()
    {
        var userId = await AiTestData.CreateSubjectUserAsync(fixture);
        var jobId = await AiTestData.CreateQueuedJobAsync(fixture, userId);
        await using var context = fixture.CreateContext();
        var store = new EfAiJobWorkStore(context);
        var claim = (await store.TryClaimAsync(jobId, TimeSpan.FromSeconds(300), AiPromptBuilder.Version, CancellationToken.None))!;
        var load = await store.LoadContextAsync(claim, CancellationToken.None);
        return (claim, load.Context!);
    }

    private static AiStoredQuestion Question(int ordinal, AiVerificationOutcome? verification = null)
    {
        var payload = JsonDocument.Parse("""
            {"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],
             "correctLabel":"B"}
            """).RootElement;
        return new AiStoredQuestion(
            new AiGeneratedQuestion(ordinal, $"Synthetic stem {ordinal} {Guid.NewGuid():N}", payload, "Because."),
            verification ?? NotChecked);
    }

    private async Task<AiCompleteResult> CompleteAsync(
        AiJobClaim claim, AiJobContext context, IReadOnlyList<AiStoredQuestion> questions, int dropped)
    {
        await using var db = fixture.CreateContext();
        return await new EfAiJobWorkStore(db).CompleteAsync(claim, context, questions, dropped, CancellationToken.None);
    }

    [PostgresFact]
    public async Task Complete_inserts_draft_ai_questions_and_marks_job_succeeded()
    {
        var (claim, context) = await ClaimedJobAsync();

        var result = await CompleteAsync(claim, context, [Question(1), Question(3)], dropped: 1);

        Assert.Equal(AiCompleteResult.Completed, result);
        await using var db = fixture.CreateContext();
        var job = await db.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == claim.JobId);
        Assert.Equal("Succeeded", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorMessage);
        Assert.Equal(1, job.DuplicatesDropped);

        var questions = await db.Questions.AsNoTracking().Where(q => q.AiJobId == claim.JobId)
            .OrderBy(q => q.AiOrdinal).ToListAsync();
        Assert.Equal([1, 3], questions.Select(q => q.AiOrdinal!.Value));
        Assert.All(questions, q =>
        {
            Assert.Equal("Draft", q.Status);
            Assert.Equal("AI", q.Source);
            Assert.Equal(1, q.Revision);
            Assert.Equal(context.DepartmentId, q.DepartmentId);
            Assert.Equal(context.RequesterId, q.CreatedBy);
            Assert.Equal(context.Request.SubjectId, q.SubjectId);
            Assert.Equal(context.Request.TopicId, q.TopicId);
            Assert.Equal(context.Request.Grade, q.Grade);
            Assert.Equal(context.Request.Type, q.Type);
            Assert.Equal(context.Request.Difficulty, q.Difficulty);
            Assert.Equal("Because.", q.Explanation);
            Assert.False(q.AiReviewedByAuthor);
            Assert.Equal(q.CreatedAt, q.UpdatedAt);
            using var payload = JsonDocument.Parse(q.Payload);
            Assert.Equal("B", payload.RootElement.GetProperty("correctLabel").GetString());
        });
    }

    [PostgresFact]
    public async Task Complete_stores_verification_flag_and_note()
    {
        var (claim, context) = await ClaimedJobAsync();
        var disagree = new AiVerificationOutcome(AiVerificationValues.Disagree, "Mô hình tự giải chọn C, đáp án gốc là B.");

        await CompleteAsync(claim, context, [Question(1, disagree), Question(2)], dropped: 0);

        await using var db = fixture.CreateContext();
        var questions = await db.Questions.AsNoTracking().Where(q => q.AiJobId == claim.JobId)
            .OrderBy(q => q.AiOrdinal).ToListAsync();
        Assert.Equal("Disagree", questions[0].AiVerification);
        Assert.Equal(disagree.Note, questions[0].AiVerificationNote);
        Assert.Equal("NotChecked", questions[1].AiVerification);
        Assert.Null(questions[1].AiVerificationNote);
    }

    [PostgresFact]
    public async Task Complete_writes_question_and_job_audits()
    {
        var (claim, context) = await ClaimedJobAsync();

        await CompleteAsync(claim, context, [Question(1), Question(2)], dropped: 1);

        await using var db = fixture.CreateContext();
        var questionIds = await db.Questions.Where(q => q.AiJobId == claim.JobId)
            .OrderBy(q => q.AiOrdinal).Select(q => q.Id).ToListAsync();
        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == claim.JobId || questionIds.Contains(a.EntityId)).ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.Null(a.ActorId);
            Assert.Equal(context.DepartmentId, a.DepartmentId);
        });

        var jobAudit = Assert.Single(audits, a => a.EntityType == "AiJob");
        Assert.Equal("Succeeded", jobAudit.Action);
        Assert.Equal("Running", jobAudit.FromStatus);
        Assert.Equal("Succeeded", jobAudit.ToStatus);
        Assert.Equal("AI worker; questions=2; duplicatesDropped=1", jobAudit.Note);

        var first = Assert.Single(audits, a => a.EntityId == questionIds[0]);
        Assert.Equal("Question", first.EntityType);
        Assert.Equal("CreateQuestion", first.Action);
        Assert.Null(first.FromStatus);
        Assert.Equal("Draft", first.ToStatus);
        Assert.Equal($"AI worker; job={claim.JobId}; ordinal=1; revision=1", first.Note);
    }

    [PostgresFact]
    public async Task Complete_with_zero_questions_still_succeeds()
    {
        var (claim, context) = await ClaimedJobAsync();

        Assert.Equal(AiCompleteResult.Completed, await CompleteAsync(claim, context, [], dropped: 3));

        await using var db = fixture.CreateContext();
        var job = await db.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == claim.JobId);
        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(3, job.DuplicatesDropped);
    }

    [PostgresFact]
    public async Task Complete_with_stale_attempt_returns_LeaseLost_and_inserts_nothing()
    {
        var (claim, context) = await ClaimedJobAsync();
        await AiTestData.ExecuteAsync(fixture,
            $"UPDATE ai_generation_jobs SET lease_expires_at = now() - interval '1 second' WHERE id = {claim.JobId}");
        await using (var db = fixture.CreateContext())
            Assert.NotNull(await new EfAiJobWorkStore(db).TryClaimAsync(
                claim.JobId, TimeSpan.FromSeconds(300), AiPromptBuilder.Version, CancellationToken.None));

        Assert.Equal(AiCompleteResult.LeaseLost, await CompleteAsync(claim, context, [Question(1)], dropped: 0));

        await AssertNothingWrittenAsync(claim.JobId);
    }

    [PostgresFact]
    public async Task Complete_returns_RequesterUnavailable_when_user_deactivated_after_claim_and_inserts_nothing()
    {
        var (claim, context) = await ClaimedJobAsync();
        await AiTestData.ExecuteAsync(fixture, $"UPDATE users SET is_active = false WHERE id = {context.RequesterId}");

        Assert.Equal(AiCompleteResult.RequesterUnavailable, await CompleteAsync(claim, context, [Question(1)], dropped: 0));

        await AssertNothingWrittenAsync(claim.JobId);
    }

    [PostgresFact]
    public async Task Failed_complete_leaves_nothing_for_a_later_fail_on_the_same_context()
    {
        var (claim, context) = await ClaimedJobAsync();
        var options = new DbContextOptionsBuilder<Persistence.ExamMakerDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new FailFirstQuestionInsert())
            .Options;
        await using var db = new Persistence.ExamMakerDbContext(options);
        var store = new EfAiJobWorkStore(db);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.CompleteAsync(claim, context, [Question(1)], 0, CancellationToken.None));
        Assert.True(await store.FailAsync(claim, "Timeout", "final", CancellationToken.None));

        await using var check = fixture.CreateContext();
        Assert.False(await check.Questions.AnyAsync(q => q.AiJobId == claim.JobId));
        var audit = Assert.Single(await check.AuditLogs.AsNoTracking().Where(a => a.EntityId == claim.JobId).ToListAsync());
        Assert.Equal("Failed", audit.Action);
    }

    /// <summary>Simulates a transient failure (timeout, lock wait) while saving the generated questions.</summary>
    private sealed class FailFirstQuestionInsert : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _failed;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowOnce(System.Data.Common.DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO questions", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new TimeoutException("simulated save failure");
        }
    }

    private async Task AssertNothingWrittenAsync(Guid jobId)
    {
        await using var db = fixture.CreateContext();
        Assert.Equal("Running", (await db.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status);
        Assert.False(await db.Questions.AnyAsync(q => q.AiJobId == jobId));
        Assert.False(await db.AuditLogs.AnyAsync(a => a.EntityId == jobId));
    }
}
