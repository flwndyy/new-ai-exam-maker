using ExamMaker.Application.Ai;
using ExamMaker.Application.Authentication;

namespace ExamMaker.Application.Tests.Ai;

public sealed class AiJobQueriesTests
{
    private static readonly Guid DepartmentId = Guid.NewGuid();

    private static readonly AuthenticatedUser Teacher = new(
        Guid.NewGuid(), "teacher-sub", "t@example.invalid", "Teacher", Roles.Teacher, DepartmentId, "Dept", true);

    private static readonly AuthenticatedUser OtherTeacher = Teacher with { Id = Guid.NewGuid(), CognitoSub = "other" };

    private static readonly AuthenticatedUser Head = Teacher with { Id = Guid.NewGuid(), Role = Roles.Head };

    private static readonly AuthenticatedUser Admin = Teacher with
    {
        Id = Guid.NewGuid(),
        Role = Roles.Admin,
        DepartmentId = null,
        DepartmentName = null,
    };

    private readonly FakeJobReader _reader = new();

    private AiJobQueries CreateQueries() => new(_reader);

    private AiJobView AddJob(Guid ownerId)
    {
        var job = new AiJobView(
            Id: Guid.NewGuid(),
            RequestedBy: new AiUserRef(ownerId, "Owner"),
            Status: "Queued",
            Params: new AiGenerateRequest(Guid.NewGuid(), Guid.NewGuid(), 10, "MultipleChoice", "Recognition", 3, null),
            Attempt: 0,
            MaxAttempts: 3,
            ErrorCode: null,
            ErrorMessage: null,
            ModelId: null,
            PromptVersion: null,
            InputTokens: 0,
            OutputTokens: 0,
            DuplicatesDropped: 0,
            QuestionIds: [],
            CreatedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            FinishedAt: null);
        _reader.Jobs[job.Id] = job;
        return job;
    }

    [Fact]
    public async Task Get_OwnerSeesOwnJob()
    {
        var job = AddJob(Teacher.Id);

        var result = await CreateQueries().GetAsync(Teacher, job.Id, CancellationToken.None);

        Assert.Equal(job, result);
    }

    [Fact]
    public async Task Get_AnotherTeacherCannotSeeTheJob()
    {
        var job = AddJob(Teacher.Id);

        Assert.Null(await CreateQueries().GetAsync(OtherTeacher, job.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Get_HeadOfTheSameDepartmentCannotSeeAnotherUsersJob()
    {
        var job = AddJob(Teacher.Id);

        Assert.Null(await CreateQueries().GetAsync(Head, job.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Get_AdminSeesAnyJob()
    {
        var job = AddJob(Teacher.Id);

        Assert.Equal(job, await CreateQueries().GetAsync(Admin, job.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Get_UnknownJobIsNull()
    {
        Assert.Null(await CreateQueries().GetAsync(Admin, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_InactiveOwnerCannotSeeTheJob()
    {
        var job = AddJob(Teacher.Id);

        Assert.Null(await CreateQueries().GetAsync(Teacher with { IsActive = false }, job.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(Roles.Teacher)]
    [InlineData(Roles.Head)]
    public async Task List_ContentAuthorsOnlyListTheirOwnJobs(string role)
    {
        var user = Teacher with { Role = role };

        var result = await CreateQueries().ListAsync(user, "Running", 2, 50, CancellationToken.None);

        Assert.IsType<AiJobListResult.Ok>(result);
        Assert.Equal((user.Id, "Running", 2, 50), _reader.LastList);
    }

    [Fact]
    public async Task List_AdminListsAllJobs()
    {
        var result = await CreateQueries().ListAsync(Admin, null, 1, 20, CancellationToken.None);

        Assert.IsType<AiJobListResult.Ok>(result);
        Assert.Equal(((Guid?)null, (string?)null, 1, 20), _reader.LastList);
    }

    [Theory]
    [InlineData("page", 0, 20, null)]
    [InlineData("pageSize", 1, 0, null)]
    [InlineData("pageSize", 1, 101, null)]
    [InlineData("status", 1, 20, "Done")]
    public async Task List_InvalidPagingOrStatusIsRejectedWithoutQuerying(
        string field, int page, int pageSize, string? status)
    {
        var result = await CreateQueries().ListAsync(Teacher, status, page, pageSize, CancellationToken.None);

        var invalid = Assert.IsType<AiJobListResult.Invalid>(result);
        Assert.Contains(field, invalid.Errors.Keys);
        Assert.Null(_reader.LastList);
    }

    private sealed class FakeJobReader : IAiJobReader
    {
        public Dictionary<Guid, AiJobView> Jobs { get; } = [];

        public (Guid? RequestedBy, string? Status, int Page, int PageSize)? LastList { get; private set; }

        public Task<AiJobView?> GetAsync(Guid jobId, CancellationToken ct) =>
            Task.FromResult(Jobs.GetValueOrDefault(jobId));

        public Task<AiJobPage> ListAsync(
            Guid? requestedBy, string? status, int page, int pageSize, CancellationToken ct)
        {
            LastList = (requestedBy, status, page, pageSize);
            return Task.FromResult(new AiJobPage([], page, pageSize, 0, 0));
        }
    }
}
