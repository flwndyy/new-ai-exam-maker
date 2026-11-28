using ExamMaker.Application.Ai;
using ExamMaker.Domain.Ai;
using ExamMaker.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiUsageReportReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Haiku = "global.anthropic.claude-haiku-4-5-20251001-v1:0";
    private const string Sonnet = "global.anthropic.claude-sonnet-4-6";

    private static readonly AiCostEstimator Cost = new(new AiPricingOptions { Models = [.. AiPricingOptions.Defaults] });

    private async Task<AiUsagePage> ReadAsync(Guid userId, DateOnly? from = null, DateOnly? to = null, int page = 1, int pageSize = 20)
    {
        await using var context = fixture.CreateContext();
        return await new EfAiUsageReportReader(context, Cost, NullLogger<EfAiUsageReportReader>.Instance)
            .ReadAsync(new AiUsageFilter(userId, from, to, page, pageSize), CancellationToken.None);
    }

    private async Task AddUsageAsync(Guid userId, DateOnly date, int jobs, long tokens)
    {
        await using var context = fixture.CreateContext();
        context.AiUsageDaily.Add(new AiUsageDaily { UserId = userId, Date = date, Jobs = jobs, Tokens = tokens });
        await context.SaveChangesAsync();
    }

    private async Task AddJobAsync(
        Guid userId, string status, string? modelId, long input, long output,
        DateTime createdAt, DateTime? startedAt = null, DateTime? finishedAt = null)
    {
        await using var context = fixture.CreateContext();
        context.AiGenerationJobs.Add(new AiGenerationJob
        {
            Id = Guid.NewGuid(),
            RequestedBy = userId,
            Status = status,
            Attempt = status == "Queued" ? 0 : 1,
            ModelId = modelId,
            InputTokens = input,
            OutputTokens = output,
            CreatedAt = createdAt,
            LastEnqueuedAt = createdAt,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
        });
        await context.SaveChangesAsync();
    }

    private static DateTime At(DateOnly date, int hour, int minute = 0) =>
        DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc);

    [PostgresFact]
    public async Task Rows_come_from_usage_daily_sorted_by_date_desc_with_user_ref()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        await AddUsageAsync(userId, day, jobs: 2, tokens: 100);
        await AddUsageAsync(userId, day.AddDays(1), jobs: 1, tokens: 50);

        var page = await ReadAsync(userId);

        Assert.Equal([day.AddDays(1), day], page.Items.Select(i => i.Date));
        Assert.All(page.Items, i => Assert.Equal(userId, i.User.Id));
        Assert.All(page.Items, i => Assert.Equal("Synthetic teacher", i.User.FullName));
        Assert.Equal(2, page.Items[1].Jobs);
        Assert.Equal(100, page.Items[1].Tokens);
    }

    [PostgresFact]
    public async Task Cost_sums_jobs_by_model_on_cost_day()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        await AddUsageAsync(userId, day, jobs: 2, tokens: 16_000);
        await AddJobAsync(userId, "Succeeded", Haiku, 4000, 4000, At(day, 1), At(day, 1), At(day, 2));
        await AddJobAsync(userId, "Succeeded", Sonnet, 4000, 4000, At(day, 3), At(day, 3), At(day, 4));

        var page = await ReadAsync(userId);

        Assert.Equal(0.0960m, Assert.Single(page.Items).EstimatedCostUsd);
        Assert.Equal(0.0960m, page.Totals.EstimatedCostUsd);
    }

    [PostgresFact]
    public async Task Job_created_before_midnight_finished_after_counts_on_finish_day()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        await AddUsageAsync(userId, day, jobs: 1, tokens: 0);
        await AddUsageAsync(userId, day.AddDays(1), jobs: 0, tokens: 8000);
        await AddJobAsync(userId, "Succeeded", Haiku, 4000, 4000,
            At(day, 15, 59), At(day.AddDays(1), 0, 1), At(day.AddDays(1), 0, 5));

        var page = await ReadAsync(userId);

        Assert.Equal(0.0240m, page.Items.Single(i => i.Date == day.AddDays(1)).EstimatedCostUsd);
        Assert.Equal(0m, page.Items.Single(i => i.Date == day).EstimatedCostUsd);
    }

    [PostgresFact]
    public async Task Queued_job_without_model_adds_no_cost()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        await AddUsageAsync(userId, day, jobs: 1, tokens: 0);
        await AddJobAsync(userId, "Queued", null, 0, 0, At(day, 9));

        var page = await ReadAsync(userId);

        Assert.Equal(0m, Assert.Single(page.Items).EstimatedCostUsd);
        Assert.Equal(0m, page.Totals.EstimatedCostUsd);
    }

    [PostgresFact]
    public async Task Unknown_model_costs_zero()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        await AddUsageAsync(userId, day, jobs: 1, tokens: 8000);
        await AddJobAsync(userId, "Succeeded", "global.anthropic.retired-model", 4000, 4000, At(day, 9), At(day, 9), At(day, 10));

        var page = await ReadAsync(userId);

        Assert.Equal(0m, Assert.Single(page.Items).EstimatedCostUsd);
    }

    [PostgresFact]
    public async Task Totals_cover_all_filtered_rows_not_only_page()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        for (var i = 0; i < 3; i++)
        {
            await AddUsageAsync(userId, day.AddDays(i), jobs: 1, tokens: 8000);
            await AddJobAsync(userId, "Succeeded", Haiku, 4000, 4000,
                At(day.AddDays(i), 8), At(day.AddDays(i), 8), At(day.AddDays(i), 9));
        }

        var page = await ReadAsync(userId, pageSize: 1);

        Assert.Single(page.Items);
        Assert.Equal(day.AddDays(2), page.Items[0].Date);
        Assert.Equal(3, page.TotalItems);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new AiUsageTotals(3, 24_000, 0.0720m), page.Totals);
    }

    [PostgresFact]
    // The admin page's default view: no user filter, so cost must still be attributed per user.
    public async Task Without_user_filter_each_user_gets_only_their_own_cost()
    {
        var first = await AiTestData.CreateUserAsync(fixture);
        var second = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        await AddUsageAsync(first, day, jobs: 1, tokens: 8000);
        await AddUsageAsync(second, day, jobs: 1, tokens: 8000);
        await AddJobAsync(first, "Succeeded", Haiku, 4000, 4000, At(day, 8), At(day, 8), At(day, 9));
        await AddJobAsync(second, "Succeeded", Sonnet, 4000, 4000, At(day, 8), At(day, 8), At(day, 9));

        await using var context = fixture.CreateContext();
        var page = await new EfAiUsageReportReader(context, Cost, NullLogger<EfAiUsageReportReader>.Instance)
            .ReadAsync(new AiUsageFilter(null, day, day, 1, 20), CancellationToken.None);

        Assert.Equal(0.0240m, page.Items.Single(i => i.User.Id == first).EstimatedCostUsd);
        Assert.Equal(0.0720m, page.Items.Single(i => i.User.Id == second).EstimatedCostUsd);
        Assert.Equal(2, page.TotalItems);
        Assert.Equal(new AiUsageTotals(2, 16_000, 0.0960m), page.Totals);
    }

    [PostgresFact]
    public async Task From_and_to_are_inclusive()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var day = AiTestData.UniqueDate();
        for (var i = 0; i < 4; i++)
        {
            await AddUsageAsync(userId, day.AddDays(i), jobs: 1, tokens: 10);
            await AddJobAsync(userId, "Succeeded", Haiku, 4000, 4000,
                At(day.AddDays(i), 8), At(day.AddDays(i), 8), At(day.AddDays(i), 9));
        }

        var page = await ReadAsync(userId, from: day.AddDays(1), to: day.AddDays(2));

        Assert.Equal([day.AddDays(2), day.AddDays(1)], page.Items.Select(i => i.Date));
        Assert.Equal(new AiUsageTotals(2, 20, 0.0480m), page.Totals);
    }
}
