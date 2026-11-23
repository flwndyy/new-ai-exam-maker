using System.Text.Json;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiJobStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<Guid?> CreateAsync(Guid userId, DateOnly date, int limit, int maxAttempts = 3)
    {
        await using var context = fixture.CreateContext();
        return await new EfAiJobStore(context).TryCreateQueuedJobAsync(
            userId, AiTestData.Request(), date, limit, maxAttempts, CancellationToken.None);
    }

    private async Task<int> JobsUsedAsync(Guid userId, DateOnly date)
    {
        await using var context = fixture.CreateContext();
        return await context.AiUsageDaily
            .Where(u => u.UserId == userId && u.Date == date)
            .Select(u => u.Jobs)
            .SingleOrDefaultAsync();
    }

    [PostgresFact]
    public async Task Creates_queued_job_with_request_params_and_counts_one_job()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();

        var jobId = await CreateAsync(userId, date, limit: 10, maxAttempts: 3);

        Assert.NotNull(jobId);
        await using var context = fixture.CreateContext();
        var job = await context.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(userId, job.RequestedBy);
        Assert.Equal("Queued", job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Equal(3, job.MaxAttempts);
        Assert.Equal(0, job.InputTokens);
        Assert.Equal(0, job.OutputTokens);
        Assert.Null(job.LeaseExpiresAt);

        using var parameters = JsonDocument.Parse(job.Params);
        Assert.Equal("MultipleChoice", parameters.RootElement.GetProperty("type").GetString());
        Assert.Equal(11, parameters.RootElement.GetProperty("grade").GetInt32());
        Assert.Equal(5, parameters.RootElement.GetProperty("count").GetInt32());

        Assert.Equal(1, await JobsUsedAsync(userId, date));
    }

    [PostgresFact]
    public async Task Creates_queued_job_with_last_enqueued_at_equal_to_created_at()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);

        var jobId = await CreateAsync(userId, AiTestData.UniqueDate(), limit: 10);

        await using var context = fixture.CreateContext();
        var job = await context.AiGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(job.CreatedAt, job.LastEnqueuedAt);
    }

    [PostgresFact]
    public async Task Each_job_increments_the_daily_counter()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();

        Assert.NotNull(await CreateAsync(userId, date, limit: 10));
        Assert.NotNull(await CreateAsync(userId, date, limit: 10));

        Assert.Equal(2, await JobsUsedAsync(userId, date));
    }

    [PostgresFact]
    public async Task At_the_daily_limit_returns_null_and_creates_nothing()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();
        Assert.NotNull(await CreateAsync(userId, date, limit: 2));
        Assert.NotNull(await CreateAsync(userId, date, limit: 2));

        var third = await CreateAsync(userId, date, limit: 2);

        Assert.Null(third);
        Assert.Equal(2, await JobsUsedAsync(userId, date));
        await using var context = fixture.CreateContext();
        Assert.Equal(2, await context.AiGenerationJobs.CountAsync(j => j.RequestedBy == userId));
    }

    [PostgresFact]
    public async Task A_limit_of_zero_never_creates_a_job()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();

        Assert.Null(await CreateAsync(userId, date, limit: 0));

        Assert.Equal(0, await JobsUsedAsync(userId, date));
        await using var context = fixture.CreateContext();
        Assert.False(await context.AiGenerationJobs.AnyAsync(j => j.RequestedBy == userId));
    }

    [PostgresFact]
    public async Task Concurrent_requests_never_exceed_the_daily_limit()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => Task.Run(() => CreateAsync(userId, date, limit: 3))));

        Assert.Equal(3, results.Count(id => id is not null));
        Assert.Equal(3, await JobsUsedAsync(userId, date));
        await using var context = fixture.CreateContext();
        Assert.Equal(3, await context.AiGenerationJobs.CountAsync(j => j.RequestedBy == userId));
    }

    [PostgresFact]
    public async Task Counters_are_separate_per_utc_date_and_per_user()
    {
        var alice = await AiTestData.CreateUserAsync(fixture);
        var bob = await AiTestData.CreateUserAsync(fixture);
        var today = AiTestData.UniqueDate();
        var tomorrow = today.AddDays(1);
        Assert.NotNull(await CreateAsync(alice, today, limit: 1));

        Assert.Null(await CreateAsync(alice, today, limit: 1));
        Assert.NotNull(await CreateAsync(alice, tomorrow, limit: 1));
        Assert.NotNull(await CreateAsync(bob, today, limit: 1));
    }
}
