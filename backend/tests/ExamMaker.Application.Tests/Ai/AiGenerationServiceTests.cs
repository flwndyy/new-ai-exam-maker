using ExamMaker.Application.Ai;
using Microsoft.Extensions.Time.Testing;

namespace ExamMaker.Application.Tests.Ai;

public sealed class AiGenerationServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 30, 0, TimeSpan.Zero);

    private readonly FakeUsageReader _usage = new();
    private readonly FakeJobQueue _queue = new();
    private readonly FakeJobStore _jobs;
    private readonly AiQuotaOptions _options = new();
    private readonly FakeTimeProvider _time = new(Now);

    public AiGenerationServiceTests()
    {
        _jobs = new FakeJobStore(_queue);
    }

    private AiGenerationService CreateService() => new(_options, _usage, _jobs, _queue, _time);

    private static AiGenerateRequest ValidRequest(int count = 5, string? note = null) => new(
        SubjectId: Guid.NewGuid(),
        TopicId: Guid.NewGuid(),
        Grade: 10,
        Type: "MultipleChoice",
        Difficulty: "Understanding",
        Count: count,
        Note: note);

    [Fact]
    public async Task GetQuota_ReturnsConfiguredLimitsAndUsageForUtcDay()
    {
        _usage.User = new AiUsage(JobsUsed: 3, TokensUsed: 12_000);

        var quota = await CreateService().GetQuotaAsync(UserId, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 25), quota.Date);
        Assert.Equal(10, quota.JobsLimit);
        Assert.Equal(3, quota.JobsUsed);
        Assert.Equal(100_000, quota.TokensLimit);
        Assert.Equal(12_000, quota.TokensUsed);
        Assert.Equal(10, quota.MaxQuestionsPerJob);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero), quota.ResetsAt);
        Assert.Equal(new DateOnly(2026, 11, 25), _usage.LastDate);
    }

    [Fact]
    public async Task Submit_ValidRequest_CreatesQueuedJobThenEnqueuesItsId()
    {
        var request = ValidRequest();

        var result = await CreateService().SubmitAsync(UserId, request, CancellationToken.None);

        var accepted = Assert.IsType<AiSubmitResult.Accepted>(result);
        Assert.Equal(_jobs.CreatedJobId, accepted.JobId);
        Assert.Equal(new[] { _jobs.CreatedJobId }, _queue.Enqueued);
        Assert.Equal(UserId, _jobs.LastUserId);
        Assert.Equal(request, _jobs.LastRequest);
        Assert.Equal(new DateOnly(2026, 11, 25), _jobs.LastUsageDate);
        Assert.Equal(10, _jobs.LastJobsLimit);
        Assert.Equal(3, _jobs.LastMaxAttempts);
        Assert.Equal(new[] { "create", "enqueue" }, _queue.Timeline);
    }

    [Fact]
    public async Task Submit_CountAboveMaxQuestionsPerJob_FailsValidationWithoutSideEffects()
    {
        var result = await CreateService().SubmitAsync(UserId, ValidRequest(count: 11), CancellationToken.None);

        var failed = Assert.IsType<AiSubmitResult.ValidationFailed>(result);
        Assert.Contains("count", failed.Errors.Keys);
        Assert.Equal(0, _jobs.CreateCalls);
        Assert.Empty(_queue.Enqueued);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Submit_CountBelowOne_FailsValidation(int count)
    {
        var result = await CreateService().SubmitAsync(UserId, ValidRequest(count: count), CancellationToken.None);

        var failed = Assert.IsType<AiSubmitResult.ValidationFailed>(result);
        Assert.Contains("count", failed.Errors.Keys);
    }

    [Fact]
    public async Task Submit_NoteLongerThanLimit_FailsValidation()
    {
        var result = await CreateService().SubmitAsync(
            UserId, ValidRequest(note: new string('x', 1001)), CancellationToken.None);

        var failed = Assert.IsType<AiSubmitResult.ValidationFailed>(result);
        Assert.Contains("note", failed.Errors.Keys);
        Assert.Equal(0, _jobs.CreateCalls);
    }

    [Fact]
    public async Task Submit_NoteAtLimit_IsAccepted()
    {
        var result = await CreateService().SubmitAsync(
            UserId, ValidRequest(note: new string('x', 1000)), CancellationToken.None);

        Assert.IsType<AiSubmitResult.Accepted>(result);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(13)]
    public async Task Submit_GradeOutsideTenToTwelve_FailsValidation(int grade)
    {
        var request = ValidRequest() with { Grade = grade };

        var result = await CreateService().SubmitAsync(UserId, request, CancellationToken.None);

        var failed = Assert.IsType<AiSubmitResult.ValidationFailed>(result);
        Assert.Contains("grade", failed.Errors.Keys);
    }

    [Theory]
    [InlineData("type", "Unknown")]
    [InlineData("difficulty", "Impossible")]
    public async Task Submit_UnknownEnumValue_FailsValidation(string field, string value)
    {
        var request = field == "type"
            ? ValidRequest() with { Type = value }
            : ValidRequest() with { Difficulty = value };

        var result = await CreateService().SubmitAsync(UserId, request, CancellationToken.None);

        var failed = Assert.IsType<AiSubmitResult.ValidationFailed>(result);
        Assert.Contains(field, failed.Errors.Keys);
    }

    [Fact]
    public async Task Submit_EmptySubjectOrTopic_FailsValidation()
    {
        var request = ValidRequest() with { SubjectId = Guid.Empty, TopicId = Guid.Empty };

        var result = await CreateService().SubmitAsync(UserId, request, CancellationToken.None);

        var failed = Assert.IsType<AiSubmitResult.ValidationFailed>(result);
        Assert.Contains("subjectId", failed.Errors.Keys);
        Assert.Contains("topicId", failed.Errors.Keys);
    }

    [Fact]
    public async Task Submit_UserTokensAtDailyLimit_IsQuotaExceededWithoutCreatingJob()
    {
        _usage.User = new AiUsage(JobsUsed: 1, TokensUsed: 100_000);

        var result = await CreateService().SubmitAsync(UserId, ValidRequest(), CancellationToken.None);

        var exceeded = Assert.IsType<AiSubmitResult.QuotaExceeded>(result);
        Assert.Equal(AiQuotaReason.UserTokens, exceeded.Reason);
        Assert.Equal(0, _jobs.CreateCalls);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task Submit_SystemTokensAtDailyCap_IsQuotaExceeded()
    {
        _usage.SystemTokensUsed = 500_000;

        var result = await CreateService().SubmitAsync(UserId, ValidRequest(), CancellationToken.None);

        var exceeded = Assert.IsType<AiSubmitResult.QuotaExceeded>(result);
        Assert.Equal(AiQuotaReason.SystemTokens, exceeded.Reason);
        Assert.Equal(0, _jobs.CreateCalls);
    }

    [Fact]
    public async Task Submit_JobSlotNotReservable_IsQuotaExceededAndNothingEnqueued()
    {
        _jobs.ReserveSucceeds = false;

        var result = await CreateService().SubmitAsync(UserId, ValidRequest(), CancellationToken.None);

        var exceeded = Assert.IsType<AiSubmitResult.QuotaExceeded>(result);
        Assert.Equal(AiQuotaReason.UserJobs, exceeded.Reason);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task Submit_QuotaExceeded_RetryAfterIsTimeUntilNextUtcMidnight()
    {
        _usage.User = new AiUsage(JobsUsed: 0, TokensUsed: 100_000);

        var result = await CreateService().SubmitAsync(UserId, ValidRequest(), CancellationToken.None);

        var exceeded = Assert.IsType<AiSubmitResult.QuotaExceeded>(result);
        Assert.Equal(TimeSpan.FromHours(13.5), exceeded.RetryAfter);
    }

    [Fact]
    public async Task Submit_EnqueueFails_StillAcceptsBecauseJobStaysQueuedForRecovery()
    {
        _queue.Failure = new InvalidOperationException("sqs down");

        var result = await CreateService().SubmitAsync(UserId, ValidRequest(), CancellationToken.None);

        var accepted = Assert.IsType<AiSubmitResult.Accepted>(result);
        Assert.Equal(_jobs.CreatedJobId, accepted.JobId);
        Assert.Equal(1, _jobs.CreateCalls);
    }

    [Fact]
    public async Task Submit_EnqueueCancelled_PropagatesCancellationInsteadOfSwallowingIt()
    {
        _queue.Failure = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService().SubmitAsync(UserId, ValidRequest(), CancellationToken.None));
    }

    private sealed class FakeUsageReader : IAiUsageReader
    {
        public AiUsage User { get; set; } = new(0, 0);

        public long SystemTokensUsed { get; set; }

        public DateOnly? LastDate { get; private set; }

        public Task<AiUsage> GetUserUsageAsync(Guid userId, DateOnly date, CancellationToken ct)
        {
            LastDate = date;
            return Task.FromResult(User);
        }

        public Task<long> GetSystemTokensAsync(DateOnly date, CancellationToken ct) =>
            Task.FromResult(SystemTokensUsed);
    }

    private sealed class FakeJobStore(FakeJobQueue timeline) : IAiJobStore
    {
        public bool ReserveSucceeds { get; set; } = true;

        public Guid CreatedJobId { get; } = Guid.NewGuid();

        public int CreateCalls { get; private set; }

        public Guid? LastUserId { get; private set; }

        public AiGenerateRequest? LastRequest { get; private set; }

        public DateOnly? LastUsageDate { get; private set; }

        public int? LastJobsLimit { get; private set; }

        public int? LastMaxAttempts { get; private set; }

        public Task<Guid?> TryCreateQueuedJobAsync(
            Guid userId, AiGenerateRequest request, DateOnly usageDate, int jobsLimit, int maxAttempts,
            CancellationToken ct)
        {
            CreateCalls++;
            LastUserId = userId;
            LastRequest = request;
            LastUsageDate = usageDate;
            LastJobsLimit = jobsLimit;
            LastMaxAttempts = maxAttempts;
            timeline.Record("create");
            return Task.FromResult<Guid?>(ReserveSucceeds ? CreatedJobId : null);
        }
    }

    private sealed class FakeJobQueue : IAiJobQueue
    {
        public List<Guid> Enqueued { get; } = [];

        public List<string> Timeline { get; } = [];

        public Exception? Failure { get; set; }

        public void Record(string step) => Timeline.Add(step);

        public Task EnqueueAsync(Guid jobId, CancellationToken ct)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Enqueued.Add(jobId);
            Timeline.Add("enqueue");
            return Task.CompletedTask;
        }
    }
}
