using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamMaker.Application.Exams;
using ExamMaker.Application.Questions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Api.Tests;

public sealed class ExamsHttpTests
{
    [Theory]
    [InlineData("approve", 400, "ValidationFailed")]
    [InlineData("reject", 400, "ValidationFailed")]
    [InlineData("approve", 401, "Unauthorized")]
    [InlineData("reject", 401, "Unauthorized")]
    [InlineData("approve", 403, "SelfReview")]
    [InlineData("reject", 403, "SelfReview")]
    [InlineData("approve", 404, "NotFound")]
    [InlineData("reject", 404, "NotFound")]
    [InlineData("approve", 409, "InvalidStateTransition")]
    [InlineData("reject", 409, "InvalidStateTransition")]
    // An absent HTTP body, not merely {}, must still reach the live permission/state/token checks.
    public async Task Empty_review_bodies_preserve_service_error_precedence(string action, int status, string code)
    {
        using var app = new ExamsTestFactory(); app.Directory.User = AuthTestFactory.Teacher with { Role = "head" };
        app.Management.Error = new(status, code, "Safe error", status == 400 ? "submittedAt" : null);
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsync($"/api/v1/exam-versions/{app.Management.Version.Id}/{action}", null);
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Equal(1, app.Management.Calls);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Redacted_exam_item_serializes_required_content_fields_as_explicit_nulls()
    {
        using var app = new ExamsTestFactory(); app.Management.RedactItem = true;
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/v1/exam-versions/{app.Management.Version.Id}");
        var item = body.GetProperty("items")[0];
        foreach (var field in new[] { "type", "difficulty", "stem", "payload", "explanation", "currentSourceRevision" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(field).ValueKind);
        Assert.Equal("SourceContentUnavailable", body.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Routes_dispatch_actors_inputs_and_review_tokens_with_flat_OpenAPI_response_shapes()
    {
        using var app = new ExamsTestFactory(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        var exam = app.Management.Exam.Id; var version = app.Management.Version.Id;
        using var created = await client.PostAsJsonAsync("/api/v1/exams", new { title = "Synthetic", subjectId = Guid.NewGuid(), grade = 10 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.EndsWith(exam.ToString(), created.Headers.Location!.ToString());
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("versions", out _)); Assert.True(json.TryGetProperty("title", out _)); Assert.False(json.TryGetProperty("exam", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/exams?mine=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/exams/{exam}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/exams/{exam}", new { title = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/exams/{exam}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/v1/exams/{exam}/versions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/exam-versions/{version}", new { durationMinutes = 45, items = Array.Empty<object>() })).StatusCode);
        foreach (var action in new[] { "submit", "withdraw", "archive" })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/exam-versions/{version}/{action}", null)).StatusCode);
        app.Directory.User = AuthTestFactory.Teacher with { Role = "head" };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/review/exam-versions")).StatusCode);
        var token = DateTimeOffset.UtcNow;
        foreach (var action in new[] { "approve", "reject" })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/exam-versions/{version}/{action}", new { submittedAt = token, note = "Reason" })).StatusCode);
        Assert.Equal(token, app.Management.Review!.SubmittedAt); Assert.Equal(AuthTestFactory.Teacher.Id, app.Management.Actor);
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        foreach (var action in new[] { "publish", "unpublish" })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/exam-versions/{version}/{action}", null)).StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/exam-versions/{version}");
        Assert.True(detail.TryGetProperty("items", out _)); Assert.True(detail.TryGetProperty("status", out _));
    }

    [Theory]
    [InlineData(400, "ValidationFailed", "submittedAt")]
    [InlineData(401, "Unauthorized", null)]
    [InlineData(403, "SelfReview", null)]
    [InlineData(404, "NotFound", null)]
    [InlineData(409, "QuestionsNotApproved", null)]
    [InlineData(409, "PublishedVersionExists", null)]
    public async Task Missing_review_token_reaches_service_and_safe_errors_preserve_contract(int status, string code, string? field)
    {
        using var app = new ExamsTestFactory(); app.Directory.User = AuthTestFactory.Teacher with { Role = "head" };
        var source = Guid.NewGuid(); app.Management.Error = new(status, code, "Safe synthetic error", field, code == "QuestionsNotApproved" ? [source] : null);
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsJsonAsync($"/api/v1/exam-versions/{app.Management.Version.Id}/approve", new { });
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Equal(1, app.Management.Calls);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.True(json.TryGetProperty("traceId", out _));
        if (field != null) Assert.True(json.GetProperty("errors").TryGetProperty(field, out _));
        if (status == 401) Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        if (code == "QuestionsNotApproved") Assert.Equal(source, json.GetProperty("invalidQuestionIds")[0].GetGuid());
    }

    [Fact]
    public async Task Anonymous_requests_and_non_admin_publishing_never_dispatch()
    {
        using var app = new ExamsTestFactory(); using var client = app.CreateClient(); var id = app.Management.Version.Id;
        foreach (var path in new[] { "/api/v1/exams", $"/api/v1/exams/{id}", $"/api/v1/exams/{id}/versions", $"/api/v1/exam-versions/{id}", "/api/v1/review/exam-versions" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        foreach (var action in new[] { "submit", "withdraw", "approve", "reject", "archive", "publish", "unpublish" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/exam-versions/{id}/{action}", new { })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        foreach (var action in new[] { "approve", "reject", "publish", "unpublish" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/exam-versions/{id}/{action}", new { })).StatusCode);
        Assert.Equal(0, app.Management.Calls);
    }
}

public sealed class ExamsTestFactory : AuthTestFactory
{
    public FakeExams Management { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => { services.RemoveAll<IExamManagement>(); services.AddSingleton<IExamManagement>(Management); });
    }
}

public sealed class FakeExams : IExamManagement
{
    public ExamView Exam { get; } = new(Guid.NewGuid(), "Synthetic exam", Guid.NewGuid(), 10, AuthTestFactory.DepartmentId,
        new QuestionUser(AuthTestFactory.Teacher.Id, "Synthetic"), 1, null, DateTime.UtcNow, DateTime.UtcNow);
    public ExamVersionView Version { get; }
    public FakeExams() => Version = new(new(Guid.NewGuid(), Exam.Id, Exam.Title, Exam.SubjectId, Exam.Grade, Exam.DepartmentId,
        Exam.CreatedBy, 1, "Draft", 45, 0, 0, null, null, null, null, null, null, null, Exam.CreatedAt, Exam.UpdatedAt), [], []);
    public ExamException? Error { get; set; }
    public int Calls { get; private set; }
    public Guid Actor { get; private set; }
    public ExamReviewInput? Review { get; private set; }
    public bool RedactItem { get; set; }
    private void Capture(Guid actor) { Calls++; Actor = actor; if (Error != null) throw Error; }
    public Task<ExamPage> ListAsync(Guid actor, ExamQuery query, CancellationToken cancellationToken) { Capture(actor); return Task.FromResult(new ExamPage(1, 20, 1, 1, [Exam])); }
    public Task<ExamDetail> GetAsync(Guid actor, Guid id, CancellationToken cancellationToken) { Capture(actor); return Task.FromResult(new ExamDetail(Exam, [Version])); }
    public Task<ExamDetail> CreateAsync(Guid actor, ExamCreateInput input, CancellationToken cancellationToken) => GetAsync(actor, Exam.Id, cancellationToken);
    public Task<ExamDetail> UpdateAsync(Guid actor, Guid id, ExamUpdateInput input, CancellationToken cancellationToken) => GetAsync(actor, id, cancellationToken);
    public Task<ExamVersionPage> VersionsAsync(Guid actor, Guid id, int page, int pageSize, CancellationToken cancellationToken)
    { Capture(actor); return Task.FromResult(new ExamVersionPage(1, 20, 1, 1, [Version])); }
    public Task<ExamVersionView> GetVersionAsync(Guid actor, Guid id, CancellationToken cancellationToken)
    {
        Capture(actor);
        return Task.FromResult(RedactItem ? Version with
        {
            Items = [new(1, 1, Guid.NewGuid(), 1, null, null, null, null, null, null)],
            Warnings = [new("SourceContentUnavailable", "Unavailable")]
        } : Version);
    }
    public Task<ExamVersionView> CreateVersionAsync(Guid actor, Guid exam, ExamVersionCreateInput input, CancellationToken cancellationToken) => GetVersionAsync(actor, Version.Id, cancellationToken);
    public Task<ExamVersionView> UpdateVersionAsync(Guid actor, Guid id, ExamVersionInput input, CancellationToken cancellationToken) => GetVersionAsync(actor, id, cancellationToken);
    public Task<ExamVersionView> TransitionAsync(Guid actor, Guid id, ExamAction action, ExamReviewInput? input, CancellationToken cancellationToken)
    { Review = input; return GetVersionAsync(actor, id, cancellationToken); }
    public Task<ExamVersionPage> ReviewQueueAsync(Guid actor, ExamReviewQuery query, CancellationToken cancellationToken) => VersionsAsync(actor, Exam.Id, query.Page, query.PageSize, cancellationToken);
}
