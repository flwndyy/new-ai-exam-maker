using ExamMaker.Application.Ai;
using ExamMaker.Domain.Ai;
using ExamMaker.Infrastructure.Ai;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiUsageReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task AddUsageAsync(Guid userId, DateOnly date, int jobs, long tokens)
    {
        await using var context = fixture.CreateContext();
        context.AiUsageDaily.Add(new AiUsageDaily { UserId = userId, Date = date, Jobs = jobs, Tokens = tokens });
        await context.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task User_without_usage_row_has_zero_usage()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        await using var context = fixture.CreateContext();

        var usage = await new EfAiUsageReader(context)
            .GetUserUsageAsync(userId, AiTestData.UniqueDate(), CancellationToken.None);

        Assert.Equal(new AiUsage(0, 0), usage);
    }

    [PostgresFact]
    public async Task User_usage_is_read_for_the_requested_date_only()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();
        await AddUsageAsync(userId, date, jobs: 4, tokens: 31_000);
        await AddUsageAsync(userId, date.AddDays(1), jobs: 9, tokens: 99_000);
        await using var context = fixture.CreateContext();

        var usage = await new EfAiUsageReader(context)
            .GetUserUsageAsync(userId, date, CancellationToken.None);

        Assert.Equal(new AiUsage(4, 31_000), usage);
    }

    [PostgresFact]
    public async Task System_tokens_sum_all_users_for_the_date()
    {
        var alice = await AiTestData.CreateUserAsync(fixture);
        var bob = await AiTestData.CreateUserAsync(fixture);
        var date = AiTestData.UniqueDate();
        await AddUsageAsync(alice, date, jobs: 1, tokens: 120_000);
        await AddUsageAsync(bob, date, jobs: 2, tokens: 30_000);
        await AddUsageAsync(bob, date.AddDays(1), jobs: 1, tokens: 7_000);
        await using var context = fixture.CreateContext();

        var total = await new EfAiUsageReader(context).GetSystemTokensAsync(date, CancellationToken.None);

        Assert.Equal(150_000, total);
    }

    [PostgresFact]
    public async Task System_tokens_are_zero_when_nobody_used_the_date()
    {
        await using var context = fixture.CreateContext();

        var total = await new EfAiUsageReader(context)
            .GetSystemTokensAsync(AiTestData.UniqueDate(), CancellationToken.None);

        Assert.Equal(0, total);
    }
}
