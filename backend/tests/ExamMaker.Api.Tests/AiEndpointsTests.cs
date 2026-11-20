using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Api.Tests;

public sealed class AiEndpointsTests : IDisposable
{
    private readonly AuthTestFactory _auth = new();
    private readonly InMemoryAi _ai = new();
    private readonly WebApplicationFactory<Program> _host;

    public AiEndpointsTests()
    {
        // Real JWT validation and authorization from A3; only the AI persistence/queue ports are faked.
        _host = _auth.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiUsageReader>();
            services.RemoveAll<IAiJobStore>();
            services.RemoveAll<IAiJobReader>();
            services.RemoveAll<IAiJobQueue>();
            services.AddSingleton<IAiUsageReader>(_ai);
            services.AddSingleton<IAiJobStore>(_ai);
            services.AddSingleton<IAiJobReader>(_ai);
            services.AddSingleton<IAiJobQueue>(_ai);
        }));
    }

    public void Dispose()
    {
        _host.Dispose();
        _auth.Dispose();
    }

    private HttpClient Client(string? role = Roles.Teacher)
    {
        if (role is not null)
        {
            _auth.Directory.User = AuthTestFactory.Teacher with
            {
                Role = role,
                DepartmentId = role == Roles.Admin ? null : AuthTestFactory.DepartmentId,
            };
        }

        var client = _host.CreateClient();
        if (role is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token());
        }

        return client;
    }

    private static object Body(int count = 3, string? note = null) => new
    {
        subjectId = Guid.NewGuid(),
        topicId = Guid.NewGuid(),
        grade = 10,
        type = "MultipleChoice",
        difficulty = "Recognition",
        count,
        note,
    };

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }

    [Fact]
    public async Task Generate_as_teacher_returns_202_with_location_and_queued_job()
    {
        using var client = Client(Roles.Teacher);

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", Body(count: 4));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = job.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/ai/jobs/{jobId}", response.Headers.Location?.OriginalString);
        Assert.Equal("Queued", job.GetProperty("status").GetString());
        Assert.Equal(AuthTestFactory.Teacher.Id, job.GetProperty("requestedBy").GetProperty("id").GetGuid());
        Assert.Equal(4, job.GetProperty("params").GetProperty("count").GetInt32());
        Assert.Equal(0, job.GetProperty("questionIds").GetArrayLength());
        Assert.Equal([jobId], _ai.Enqueued);
    }

    [Fact]
    public async Task Generate_as_head_is_allowed()
    {
        using var client = Client(Roles.Head);

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", Body());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Generate_as_admin_is_forbidden_and_creates_nothing()
    {
        using var client = Client(Roles.Admin);

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", Body());

        await ProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden");
        Assert.Empty(_ai.Jobs);
    }

    [Fact]
    public async Task Generate_without_token_is_unauthorized()
    {
        using var client = Client(role: null);

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", Body());

        await ProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Generate_with_invalid_count_returns_validation_problem_with_field_errors()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", Body(count: 11));

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("count", out _));
        Assert.Empty(_ai.Jobs);
    }

    [Fact]
    public async Task Generate_with_missing_type_returns_validation_problem()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", new
        {
            subjectId = Guid.NewGuid(),
            topicId = Guid.NewGuid(),
            grade = 10,
            difficulty = "Recognition",
            count = 3,
        });

        await ProblemAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Empty(_ai.Jobs);
    }

    [Fact]
    public async Task Generate_over_quota_returns_429_quota_exceeded_with_retry_after()
    {
        _ai.UserUsage = new AiUsage(JobsUsed: 1, TokensUsed: 100_000);
        using var client = Client();

        using var response = await client.PostAsJsonAsync("/api/v1/ai/generate", Body());

        await ProblemAsync(response, HttpStatusCode.TooManyRequests, "QuotaExceeded");
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(values.Single()), 1, 86_400);
        Assert.Empty(_ai.Jobs);
    }

    [Fact]
    public async Task Generate_is_rate_limited_per_user()
    {
        using var client = Client();
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await client.PostAsJsonAsync("/api/v1/ai/generate", Body());
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/v1/ai/generate", Body());

        await ProblemAsync(limited, HttpStatusCode.TooManyRequests, "RateLimited");
    }

    [Fact]
    public async Task Get_job_returns_own_job()
    {
        var job = _ai.Seed(AuthTestFactory.Teacher.Id);
        using var client = Client();

        using var response = await client.GetAsync($"/api/v1/ai/jobs/{job.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(job.Id, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Get_job_of_another_user_is_not_found()
    {
        var job = _ai.Seed(Guid.NewGuid());
        using var client = Client();

        using var response = await client.GetAsync($"/api/v1/ai/jobs/{job.Id}");

        await ProblemAsync(response, HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Admin_can_get_any_job()
    {
        var job = _ai.Seed(Guid.NewGuid());
        using var client = Client(Roles.Admin);

        using var response = await client.GetAsync($"/api/v1/ai/jobs/{job.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task List_jobs_uses_default_paging_and_only_own_jobs_for_teachers()
    {
        _ai.Seed(AuthTestFactory.Teacher.Id);
        _ai.Seed(Guid.NewGuid());
        using var client = Client();

        using var response = await client.GetAsync("/api/v1/ai/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task List_jobs_with_invalid_status_is_a_validation_problem()
    {
        using var client = Client();

        using var response = await client.GetAsync("/api/v1/ai/jobs?status=Done");

        await ProblemAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
    }

    [Fact]
    public async Task Quota_returns_limits_and_usage_for_the_current_user()
    {
        _ai.UserUsage = new AiUsage(JobsUsed: 2, TokensUsed: 9_000);
        using var client = Client();

        using var response = await client.GetAsync("/api/v1/ai/quota");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var quota = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, quota.GetProperty("jobsLimit").GetInt32());
        Assert.Equal(2, quota.GetProperty("jobsUsed").GetInt32());
        Assert.Equal(9_000, quota.GetProperty("tokensUsed").GetInt64());
        Assert.Equal(10, quota.GetProperty("maxQuestionsPerJob").GetInt32());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", quota.GetProperty("date").GetString());
        Assert.Equal(AuthTestFactory.Teacher.Id, _ai.LastUsageUser);
    }

    private sealed class InMemoryAi : IAiUsageReader, IAiJobStore, IAiJobReader, IAiJobQueue
    {
        public Dictionary<Guid, AiJobView> Jobs { get; } = [];

        public List<Guid> Enqueued { get; } = [];

        public AiUsage UserUsage { get; set; } = new(0, 0);

        public Guid? LastUsageUser { get; private set; }

        public AiJobView Seed(Guid ownerId) => Add(ownerId, new AiGenerateRequest(
            Guid.NewGuid(), Guid.NewGuid(), 11, "MultipleChoice", "Application", 2, null));

        private AiJobView Add(Guid ownerId, AiGenerateRequest request)
        {
            var job = new AiJobView(
                Guid.NewGuid(), new AiUserRef(ownerId, "Synthetic"), "Queued", request, 0, 3,
                null, null, null, null, 0, 0, [], DateTimeOffset.UtcNow, null, null);
            Jobs[job.Id] = job;
            return job;
        }

        public Task<AiUsage> GetUserUsageAsync(Guid userId, DateOnly date, CancellationToken ct)
        {
            LastUsageUser = userId;
            return Task.FromResult(UserUsage);
        }

        public Task<long> GetSystemTokensAsync(DateOnly date, CancellationToken ct) => Task.FromResult(0L);

        public Task<Guid?> TryCreateQueuedJobAsync(
            Guid userId, AiGenerateRequest request, DateOnly usageDate, int jobsLimit, int maxAttempts,
            CancellationToken ct) =>
            Task.FromResult<Guid?>(Add(userId, request).Id);

        public Task<AiJobView?> GetAsync(Guid jobId, CancellationToken ct) =>
            Task.FromResult(Jobs.GetValueOrDefault(jobId));

        public Task<AiJobPage> ListAsync(
            Guid? requestedBy, string? status, int page, int pageSize, CancellationToken ct)
        {
            var items = Jobs.Values
                .Where(j => requestedBy is null || j.RequestedBy.Id == requestedBy)
                .Where(j => status is null || j.Status == status)
                .ToList();
            return Task.FromResult(new AiJobPage(
                items.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                page, pageSize, items.Count, (int)Math.Ceiling(items.Count / (double)pageSize)));
        }

        public Task EnqueueAsync(Guid jobId, CancellationToken ct)
        {
            Enqueued.Add(jobId);
            return Task.CompletedTask;
        }
    }
}
