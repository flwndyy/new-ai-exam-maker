using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>What a test can observe about one job, read straight from PostgreSQL.</summary>
internal sealed record JobState(
    string Status,
    int Attempt,
    string? ErrorCode,
    string? ErrorMessage,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<int> Ordinals,
    IReadOnlyList<string> Verifications,
    IReadOnlyList<string> JobAuditNotes);

/// <summary>
/// Wires the real B6 worker (message handler, processor, EF store, recovery) to PostgreSQL, a scripted model and the
/// in-memory queue. Every handled message gets its own DbContext, like a separate worker process.
/// </summary>
internal sealed class WorkerHarness
{
    private readonly PostgresFixture fixture;

    public WorkerHarness(PostgresFixture fixture)
    {
        this.fixture = fixture;
        // A far-future day of its own keeps the quota (summed over all users per UTC day) free of other tests' usage.
        Today = AiTestData.UniqueDate();
        Time = new FakeTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));
        Queue = new FakeSqsQueue(Time);
    }

    public FakeTimeProvider Time { get; }

    public DateOnly Today { get; }

    public FakeSqsQueue Queue { get; }

    public AiQuotaOptions Quota { get; } = new();

    public AiGenerationOptions Generation { get; } = new();

    public async Task<(Guid UserId, Guid JobId)> CreateJobAsync(bool enqueue = true)
    {
        var userId = await AiTestData.CreateSubjectUserAsync(fixture);
        var jobId = await AiTestData.CreateQueuedJobAsync(fixture, userId);
        if (enqueue) await Queue.EnqueueAsync(jobId, CancellationToken.None);
        return (userId, jobId);
    }

    public async Task HandleAsync(FakeSqsMessage message, IAiModelClient model)
    {
        await using var db = fixture.CreateContext();
        var processor = new AiJobProcessor(new EfAiJobWorkStore(db), new EfAiDuplicateKeySource(db), model,
            new EfAiUsageReader(db), Quota, Generation, new AiWorkerOptions(), Time,
            NullLogger<AiJobProcessor>.Instance);
        var handler = new AiJobMessageHandler(processor, Queue, NullLogger<AiJobMessageHandler>.Instance);
        await handler.HandleAsync(message, CancellationToken.None);
    }

    /// <summary>
    /// Receives and handles messages one at a time, advancing the clock to the next hidden message when nothing is
    /// visible, until the queue is empty. Returns the number of handled deliveries.
    /// </summary>
    public async Task<int> DrainAsync(IAiModelClient model)
    {
        var handled = 0;
        for (var round = 0; round < 20; round++)
        {
            var batch = Queue.Receive();
            if (batch.Count == 0)
            {
                var pending = Queue.InQueue;
                if (pending.Count == 0) return handled;
                var next = pending.Min(m => m.VisibleAt);
                if (next > Time.GetUtcNow()) Time.Advance(next - Time.GetUtcNow());
                continue;
            }

            foreach (var message in batch)
            {
                await HandleAsync(message, model);
                handled++;
            }
        }

        throw new InvalidOperationException("The queue did not drain within 20 rounds.");
    }

    /// <summary>Claims the job like a worker that then dies: nothing after the claim runs.</summary>
    public async Task<AiJobClaim> CrashAfterClaimAsync(Guid jobId)
    {
        await using var db = fixture.CreateContext();
        var claim = await new EfAiJobWorkStore(db).TryClaimAsync(
            jobId, FakeSqsQueue.VisibilityTimeout, AiPromptBuilder.Version, CancellationToken.None);
        Assert.NotNull(claim);
        return claim;
    }

    /// <summary>The lease is compared with PostgreSQL now(), not the fake clock, so it is expired in SQL.</summary>
    public Task ExpireLeaseAsync(Guid jobId) => AiTestData.ExecuteAsync(fixture,
        $"UPDATE ai_generation_jobs SET lease_expires_at = now() - interval '1 second' WHERE id = {jobId}");

    public Task AgeLastEnqueuedAsync(Guid jobId, TimeSpan by) => AiTestData.ExecuteAsync(fixture,
        $"UPDATE ai_generation_jobs SET last_enqueued_at = now() - {by.TotalSeconds} * interval '1 second' WHERE id = {jobId}");

    public async Task RunRecoveryAsync()
    {
        await using var db = fixture.CreateContext();
        await new AiJobRecovery(new EfAiJobWorkStore(db), Queue, new AiWorkerOptions(),
            NullLogger<AiJobRecovery>.Instance).RunOnceAsync(CancellationToken.None);
    }

    public Task SetUsageAsync(Guid userId, long tokens) => AiTestData.ExecuteAsync(fixture, $"""
        INSERT INTO ai_usage_daily (user_id, date, jobs, tokens) VALUES ({userId}, {Today}, 0, {tokens})
        ON CONFLICT (user_id, date) DO UPDATE SET tokens = EXCLUDED.tokens
        """);

    public async Task<long> UsageTokensAsync(Guid userId)
    {
        await using var db = fixture.CreateContext();
        return await db.AiUsageDaily.AsNoTracking()
            .Where(u => u.UserId == userId && u.Date == Today)
            .Select(u => u.Tokens)
            .SingleOrDefaultAsync();
    }

    public async Task<JobState> ReadJobAsync(Guid jobId)
    {
        await using var db = fixture.CreateContext();
        var job = await db.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var questions = await db.Questions.AsNoTracking()
            .Where(q => q.AiJobId == jobId)
            .OrderBy(q => q.AiOrdinal)
            .Select(q => new { Ordinal = q.AiOrdinal!.Value, q.AiVerification })
            .ToListAsync();
        var notes = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "AiJob" && a.EntityId == jobId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => a.Note ?? string.Empty)
            .ToListAsync();
        return new JobState(job.Status, job.Attempt, job.ErrorCode, job.ErrorMessage, job.InputTokens,
            job.OutputTokens, questions.Select(q => q.Ordinal).ToList(),
            questions.Select(q => q.AiVerification).ToList(), notes);
    }
}
