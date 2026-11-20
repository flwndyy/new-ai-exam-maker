using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExamMaker.Application.Questions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Api.Tests;

public sealed class QuestionsHttpTests
{
    [Fact]
    // Exercise real routing, JSON contract, actor propagation and every workflow endpoint through JWT middleware.
    public async Task Question_routes_dispatch_verified_actors_and_serialize_the_OpenAPI_shape()
    {
        using var app = new QuestionsTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsJsonAsync("/api/v1/questions", app.Management.Input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith("/api/v1/questions/" + app.Management.View.Id, response.Headers.Location!.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, json.GetProperty("payload").ValueKind);
        Assert.Equal(AuthTestFactory.Teacher.Id, json.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.Equal(AuthTestFactory.Teacher.Id, app.Management.Actor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/questions/" + app.Management.View.Id)).StatusCode);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/questions?page=2&pageSize=3&mine=true&status=Draft");
        Assert.Equal(5, page.EnumerateObject().Count()); Assert.True(app.Management.Query!.Mine); Assert.Equal(2, app.Management.Query.Page);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/questions/" + app.Management.View.Id, app.Management.Input)).StatusCode);
        foreach (var action in new[] { "submit", "withdraw", "archive", "mark-reviewed" })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/questions/{app.Management.View.Id}/{action}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/questions/" + app.Management.View.Id)).StatusCode);
        app.Directory.User = AuthTestFactory.Teacher with { Role = "head" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/questions/{app.Management.View.Id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/questions/{app.Management.View.Id}/reject", new { note = "Synthetic reason" })).StatusCode);
        Assert.Equal("Synthetic reason", app.Management.Note);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/review/questions?page=1&pageSize=5")).StatusCode);
    }

    [Fact]
    // Default authorization must protect all bank, mutation and review endpoints, including forgotten endpoint attributes.
    public async Task Every_question_route_requires_authentication()
    {
        using var app = new QuestionsTestFactory();
        using var client = app.CreateClient();
        foreach (var path in new[] { "/api/v1/questions", "/api/v1/questions/" + app.Management.View.Id, "/api/v1/review/questions" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        foreach (var action in new[] { "submit", "withdraw", "approve", "reject", "archive", "mark-reviewed" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/questions/{app.Management.View.Id}/{action}", new { note = "Reason" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/questions", app.Management.Input)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/v1/questions/" + app.Management.View.Id, app.Management.Input)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/v1/questions/" + app.Management.View.Id)).StatusCode);
        Assert.Equal(0, app.Management.Calls);
    }

    [Theory]
    [InlineData("teacher")]
    [InlineData("admin")]
    public async Task Role_policies_block_non_reviewers_and_admin_authorship_before_dispatch(string role)
    {
        using var app = new QuestionsTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = role };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        if (role == "teacher")
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/review/questions")).StatusCode);
            foreach (var action in new[] { "approve", "reject" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/questions/{app.Management.View.Id}/{action}", new { note = "Reason" })).StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/questions", app.Management.Input)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/questions/" + app.Management.View.Id, app.Management.Input)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/v1/questions/" + app.Management.View.Id)).StatusCode);
            foreach (var action in new[] { "submit", "withdraw", "mark-reviewed" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/questions/{app.Management.View.Id}/{action}", null)).StatusCode);
        }
        Assert.Equal(0, app.Management.Calls);
    }

    [Theory]
    [InlineData(400, "ValidationFailed", "payload")]
    [InlineData(401, "Unauthorized", null)]
    [InlineData(403, "SelfReview", null)]
    [InlineData(404, "NotFound", null)]
    [InlineData(409, "AiReviewRequired", null)]
    [InlineData(409, "QuestionReferenced", null)]
    [InlineData(409, "InvalidStateTransition", null)]
    public async Task Workflow_errors_preserve_the_documented_code_trace_and_validation_field(int status, string code, string? field)
    {
        using var app = new QuestionsTestFactory();
        app.Management.Failure = new(status, code, "Safe error", field);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsync($"/api/v1/questions/{app.Management.View.Id}/submit", null);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (status == 401) Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString()); Assert.True(problem.TryGetProperty("traceId", out _));
        if (field is not null) Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    // Strict review bodies apply only to question endpoints and must reject unknown fields before dispatch.
    public async Task Question_review_bodies_reject_unknown_properties(string action)
    {
        using var app = new QuestionsTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "head" };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsJsonAsync($"/api/v1/questions/{app.Management.View.Id}/{action}",
            new { note = "Synthetic reason", unexpected = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Management.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"subjectId\":\"invalid\"}")]
    [InlineData("{\"subjectId\":null}")]
    [InlineData("{\"status\":\"Approved\"}")]
    // The public body cannot spoof author, source, workflow state or revision.
    public async Task Invalid_or_privilege_bearing_bodies_fail_before_the_service(string body)
    {
        using var app = new QuestionsTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsync("/api/v1/questions", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, app.Management.Calls);
    }
    [Theory]
    [InlineData(@"\ud800")]
    [InlineData(@"\udc00")]
    // Exercise raw HTTP JSON through MVC binding, the real payload validator and ProblemDetails handling.
    public async Task Malformed_surrogate_payload_returns_400_instead_of_500(string escape)
    {
        using var app = new QuestionsTestFactory();
        app.Management.ValidateInputs = true;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        var json = JsonSerializer.Serialize(app.Management.Input, JsonSerializerOptions.Web).Replace("One", escape);
        using var response = await client.PostAsync("/api/v1/questions", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("payload", out _));
    }

}

public sealed class QuestionsTestFactory : AuthTestFactory
{
    public FakeQuestions Management { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => { services.RemoveAll<IQuestionManagement>(); services.AddSingleton<IQuestionManagement>(Management); });
    }
}

public sealed class FakeQuestions : IQuestionManagement
{
    public QuestionInput Input { get; } = new(AuthTestFactory.DepartmentId, AuthTestFactory.DepartmentId, 10, "MultipleChoice", "Recognition",
        "Synthetic question", JsonSerializer.Deserialize<JsonElement>(QuestionRulesTests.ValidPayload));
    public QuestionView View { get; } = new(Guid.NewGuid(), AuthTestFactory.DepartmentId, AuthTestFactory.DepartmentId, AuthTestFactory.DepartmentId,
        10, "MultipleChoice", "Recognition", "Synthetic question", JsonSerializer.Deserialize<JsonElement>(QuestionRulesTests.ValidPayload), null,
        "Draft", 1, "Manual", null, null, "NotChecked", null, false, new(AuthTestFactory.Teacher.Id, "Synthetic teacher"), null, null, null, null, DateTime.UtcNow, DateTime.UtcNow);
    public int Calls { get; private set; }
    public Guid Actor { get; private set; }
    public string? Note { get; private set; }
    public QuestionQuery? Query { get; private set; }
    public QuestionException? Failure { get; set; }
    public bool ValidateInputs { get; set; }
    private void Capture(Guid actor) { Calls++; Actor = actor; if (Failure is not null) throw Failure; }
    public Task<QuestionPage> ListAsync(Guid actorId, QuestionQuery query, CancellationToken cancellationToken)
    { Capture(actorId); Query = query; return Task.FromResult(new QuestionPage(query.Page, query.PageSize, 1, 1, [View])); }
    public Task<QuestionPage> ReviewQueueAsync(Guid actorId, QuestionReviewQuery query, CancellationToken cancellationToken)
    { Capture(actorId); return Task.FromResult(new QuestionPage(query.Page, query.PageSize, 1, 1, [View])); }
    public Task<QuestionView> GetAsync(Guid actorId, Guid id, CancellationToken cancellationToken) { Capture(actorId); return Task.FromResult(View); }
    public Task<QuestionView> CreateAsync(Guid actorId, QuestionInput input, CancellationToken cancellationToken)
    { Capture(actorId); if (ValidateInputs) QuestionRules.Validate(input); return Task.FromResult(View); }
    public Task<QuestionView> UpdateAsync(Guid actorId, Guid id, QuestionInput input, CancellationToken cancellationToken) { Capture(actorId); return Task.FromResult(View); }
    public Task<QuestionView> TransitionAsync(Guid actorId, Guid id, QuestionAction action, string? note, CancellationToken cancellationToken)
    { Capture(actorId); Note = note; return Task.FromResult(View); }
    public Task DeleteAsync(Guid actorId, Guid id, CancellationToken cancellationToken) { Capture(actorId); return Task.CompletedTask; }
}
