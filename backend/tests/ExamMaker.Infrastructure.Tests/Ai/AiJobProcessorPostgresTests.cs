using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ExamMaker.Infrastructure.Tests.Ai;

/// <summary>Runs the processor against the real EF store, so DbContext sharing between job and heartbeat is exercised.</summary>
public sealed class AiJobProcessorPostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class ScriptedModel(string text) : IAiModelClient
    {
        public Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiModelResponse(text, AiStopReason.EndTurn, 10, 10, "fake-model"));
    }

    private sealed class ProbeKeySource(Func<CancellationToken, Task> probe) : IAiDuplicateKeySource
    {
        public async Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId, string type, CancellationToken ct)
        {
            await probe(ct);
            return new HashSet<string>();
        }
    }

    private sealed class NoopMessage : IAiQueueMessage
    {
        public string Body => string.Empty;
        public Task ChangeVisibilityAsync(TimeSpan visibility, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private async Task<DateTime?> LeaseAsync(Guid jobId)
    {
        await using var context = fixture.CreateContext();
        return await context.AiGenerationJobs.AsNoTracking()
            .Where(j => j.Id == jobId).Select(j => j.LeaseExpiresAt).SingleAsync();
    }

    [PostgresFact]
    public async Task Heartbeat_extends_lease_while_job_uses_its_database_context()
    {
        var userId = await AiTestData.CreateSubjectUserAsync(fixture);
        var jobId = await AiTestData.CreateQueuedJobAsync(fixture, userId);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var db = fixture.CreateContext();
        DateTime? before = null, after = null;
        var keys = new ProbeKeySource(async ct =>
        {
            before = await LeaseAsync(jobId);
            // Fire a heartbeat tick while the job's own context is busy with a slow statement.
            time.Advance(TimeSpan.FromSeconds(60));
            await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(1)", ct);
            after = await LeaseAsync(jobId);
        });
        var processor = new AiJobProcessor(new EfAiJobWorkStore(db), keys,
            new ScriptedModel(AiTestData.ModelOutput(AiTestData.Request().Count)), new EfAiUsageReader(db), new AiQuotaOptions(),
            new AiGenerationOptions(), new AiWorkerOptions(), time, NullLogger<AiJobProcessor>.Instance);

        var outcome = await processor.ProcessAsync(jobId, new NoopMessage(), CancellationToken.None);

        Assert.IsType<AiJobOutcome.Delete>(outcome);
        Assert.True(after > before, "The heartbeat must extend the lease while the job is busy on its own context.");
    }
}
