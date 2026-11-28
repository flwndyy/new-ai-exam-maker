using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Verification;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>
/// Token quota is checked before every model call (docs/context.md 4.1.2, B6): a full user or system budget fails the
/// job without calling Bedrock, and a budget filled by generation only skips the verification call.
/// </summary>
public sealed class WorkerQuotaTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task AssertFailedWithoutModelCallAsync(WorkerHarness worker, Guid jobId)
    {
        var message = Assert.Single(worker.Queue.For(jobId));
        var model = new ScriptedModel();

        await worker.DrainAsync(model);

        Assert.Equal(0, model.Calls);
        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Failed", state.Status);
        Assert.Equal(AiErrorCodes.QuotaExceeded, state.ErrorCode);
        Assert.Equal("AI worker; error=QuotaExceeded", state.JobAuditNotes[^1]);
        Assert.Empty(state.Ordinals);
        Assert.True(message.Deleted);
        Assert.Empty(worker.Queue.Dlq);
    }

    [PostgresFact]
    public async Task User_token_quota_reached_fails_job_without_calling_model()
    {
        var worker = new WorkerHarness(fixture);
        var (userId, jobId) = await worker.CreateJobAsync();
        await worker.SetUsageAsync(userId, worker.Quota.TokensPerUserPerDay);

        await AssertFailedWithoutModelCallAsync(worker, jobId);
    }

    [PostgresFact]
    public async Task System_token_quota_reached_fails_job_without_calling_model()
    {
        var worker = new WorkerHarness(fixture);
        worker.Quota.SystemTokensPerDay = 1_000;
        var (_, jobId) = await worker.CreateJobAsync();
        var otherUser = await AiTestData.CreateSubjectUserAsync(fixture);
        await worker.SetUsageAsync(otherUser, 1_000);

        await AssertFailedWithoutModelCallAsync(worker, jobId);
    }

    [PostgresFact]
    public async Task Quota_reached_after_generation_skips_verification_but_keeps_questions()
    {
        var worker = new WorkerHarness(fixture);
        worker.Generation.VerifyAnswers = true;
        var (userId, jobId) = await worker.CreateJobAsync();
        var limit = worker.Quota.TokensPerUserPerDay;
        await worker.SetUsageAsync(userId, limit - ScriptedModel.TokensPerCall);
        var model = new ScriptedModel(ScriptedModel.Valid(5));

        await worker.DrainAsync(model);

        Assert.Equal(1, model.Calls);
        var state = await worker.ReadJobAsync(jobId);
        Assert.Equal("Succeeded", state.Status);
        Assert.Equal([1, 2, 3, 4, 5], state.Ordinals);
        Assert.All(state.Verifications, v => Assert.Equal(AiVerificationValues.NotChecked, v));
        Assert.Equal(limit, await worker.UsageTokensAsync(userId));
    }
}
