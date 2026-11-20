using ExamMaker.Application.Ai;
using ExamMaker.Domain.Questions;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiJobReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<Guid> CreateJobAsync(Guid userId, AiGenerateRequest? request = null)
    {
        await using var context = fixture.CreateContext();
        var id = await new EfAiJobStore(context).TryCreateQueuedJobAsync(
            userId, request ?? AiTestData.Request(), AiTestData.UniqueDate(), 10, 3, CancellationToken.None);
        return id!.Value;
    }

    private async Task<Guid> AddAiQuestionAsync(Guid userId, Guid jobId, int ordinal)
    {
        await using var context = fixture.CreateContext();
        var topic = await context.Topics.Include(t => t.Subject).FirstAsync();
        var question = new Question
        {
            Id = Guid.NewGuid(),
            SubjectId = topic.SubjectId,
            TopicId = topic.Id,
            DepartmentId = topic.Subject!.DepartmentId,
            Grade = topic.Grade,
            Type = "MultipleChoice",
            Difficulty = "Recognition",
            Stem = "Synthetic stem " + ordinal,
            Payload = """{"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":"A"}""",
            Status = "Draft",
            Source = "AI",
            AiJobId = jobId,
            AiOrdinal = ordinal,
            CreatedBy = userId,
        };
        context.Questions.Add(question);
        await context.SaveChangesAsync();
        return question.Id;
    }

    private async Task<AiJobView?> GetAsync(Guid jobId)
    {
        await using var context = fixture.CreateContext();
        return await new EfAiJobReader(context).GetAsync(jobId, CancellationToken.None);
    }

    private async Task<AiJobPage> ListAsync(Guid? requestedBy, string? status, int page, int pageSize)
    {
        await using var context = fixture.CreateContext();
        return await new EfAiJobReader(context).ListAsync(requestedBy, status, page, pageSize, CancellationToken.None);
    }

    [PostgresFact]
    public async Task Unknown_job_is_null()
    {
        Assert.Null(await GetAsync(Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task Queued_job_maps_every_contract_field()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var request = AiTestData.Request();
        var jobId = await CreateJobAsync(userId, request);

        var job = await GetAsync(jobId);

        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
        Assert.Equal(new AiUserRef(userId, "Synthetic teacher"), job.RequestedBy);
        Assert.Equal("Queued", job.Status);
        Assert.Equal(request, job.Params);
        Assert.Equal(0, job.Attempt);
        Assert.Equal(3, job.MaxAttempts);
        Assert.Null(job.ErrorCode);
        Assert.Equal(0, job.InputTokens);
        Assert.Empty(job.QuestionIds);
        Assert.Equal(TimeSpan.Zero, job.CreatedAt.Offset);
        Assert.InRange(job.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Null(job.StartedAt);
        Assert.Null(job.FinishedAt);
    }

    [PostgresFact]
    public async Task Question_ids_are_ordered_by_ai_ordinal()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var jobId = await CreateJobAsync(userId);
        var second = await AddAiQuestionAsync(userId, jobId, ordinal: 2);
        var first = await AddAiQuestionAsync(userId, jobId, ordinal: 1);

        var job = await GetAsync(jobId);

        Assert.Equal([first, second], job!.QuestionIds);
    }

    [PostgresFact]
    public async Task List_filters_by_requester_newest_first_with_paging_metadata()
    {
        var alice = await AiTestData.CreateUserAsync(fixture);
        var bob = await AiTestData.CreateUserAsync(fixture);
        var oldest = await CreateJobAsync(alice);
        var middle = await CreateJobAsync(alice);
        var newest = await CreateJobAsync(alice);
        await CreateJobAsync(bob);

        var firstPage = await ListAsync(alice, null, page: 1, pageSize: 2);
        var secondPage = await ListAsync(alice, null, page: 2, pageSize: 2);

        Assert.Equal([newest, middle], firstPage.Items.Select(j => j.Id));
        Assert.Equal((1, 2, 3, 2), (firstPage.Page, firstPage.PageSize, firstPage.TotalItems, firstPage.TotalPages));
        Assert.Equal([oldest], secondPage.Items.Select(j => j.Id));
    }

    [PostgresFact]
    public async Task List_filters_by_status()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        var queued = await CreateJobAsync(userId);
        var running = await CreateJobAsync(userId);
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ai_generation_jobs
                SET status = 'Running', attempt = 1, started_at = now(), lease_expires_at = now() + interval '5 minutes'
                WHERE id = {running}
                """);
        }

        var page = await ListAsync(userId, "Running", page: 1, pageSize: 20);

        var only = Assert.Single(page.Items);
        Assert.Equal(running, only.Id);
        Assert.Equal("Running", only.Status);
        Assert.NotNull(only.StartedAt);
        Assert.DoesNotContain(page.Items, j => j.Id == queued);
    }

    [PostgresFact]
    public async Task List_without_requester_includes_every_users_jobs()
    {
        var alice = await AiTestData.CreateUserAsync(fixture);
        var bob = await AiTestData.CreateUserAsync(fixture);
        var aliceJob = await CreateJobAsync(alice);
        var bobJob = await CreateJobAsync(bob);

        var page = await ListAsync(null, null, page: 1, pageSize: 100);

        Assert.Contains(page.Items, j => j.Id == aliceJob);
        Assert.Contains(page.Items, j => j.Id == bobJob);
    }

    [PostgresFact]
    public async Task Page_whose_offset_overflows_int_is_empty_instead_of_failing()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        await CreateJobAsync(userId);

        // (int.MaxValue - 1) * 20 overflows a 32-bit offset; PostgreSQL rejects a negative OFFSET.
        var page = await ListAsync(userId, null, page: int.MaxValue, pageSize: 20);

        Assert.Empty(page.Items);
        Assert.Equal(int.MaxValue, page.Page);
        Assert.Equal(1, page.TotalItems);
        Assert.Equal(1, page.TotalPages);
    }

    [PostgresFact]
    public async Task Page_beyond_the_last_is_empty_but_keeps_totals()
    {
        var userId = await AiTestData.CreateUserAsync(fixture);
        await CreateJobAsync(userId);

        var page = await ListAsync(userId, null, page: 5, pageSize: 20);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalItems);
        Assert.Equal(1, page.TotalPages);
    }
}
