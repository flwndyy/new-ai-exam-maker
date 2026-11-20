using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExamMaker.Application.Catalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Api.Tests;

public sealed class CatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("teacher")]
    [InlineData("head")]
    [InlineData("admin")]
    // Exercise all catalog routes through real JWT validation and database-profile role policies.
    public async Task Catalog_reads_allow_all_authenticated_roles_and_writes_require_admin(string? role)
    {
        using var app = new CatalogTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = role ?? "teacher" };
        using var client = app.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        foreach (var entity in new[] { "departments", "subjects", "topics" })
        {
            using var read = await client.GetAsync("/api/v1/" + entity);
            Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, read.StatusCode);
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            {
                var path = "/api/v1/admin/" + entity + (method == HttpMethod.Post ? "" : "/" + app.Management.Id);
                using var request = new HttpRequestMessage(method, path);
                // Strict per-resource DTOs reject unrelated fields, so supply the exact payload.
                request.Content = entity switch
                {
                    "departments" => JsonContent.Create(new { name = "Synthetic" }),
                    "subjects" => JsonContent.Create(new { name = "Synthetic", departmentId = app.Management.Id }),
                    _ => JsonContent.Create(new { name = "Synthetic", subjectId = app.Management.Id, grade = 10, sortOrder = 0 })
                };
                using var response = await client.SendAsync(request);
                Assert.Equal(role is null ? HttpStatusCode.Unauthorized : role != "admin" ? HttpStatusCode.Forbidden
                    : method == HttpMethod.Post ? HttpStatusCode.Created : method == HttpMethod.Put ? HttpStatusCode.OK : HttpStatusCode.NoContent,
                    response.StatusCode);
            }
        }
        if (role is null) Assert.Equal(0, app.Management.Calls);
        else if (role != "admin") Assert.Equal(3, app.Management.Calls);
        else Assert.Equal(AuthTestFactory.Teacher.Id, app.Management.Actor);
    }

    [Theory]
    [InlineData("departments", "{}")]
    [InlineData("departments", "{\"name\":\"\"}")]
    [InlineData("departments", "{\"name\":\"Name\",\"id\":\"spoof\"}")]
    [InlineData("subjects", "{\"name\":\"Name\"}")]
    [InlineData("subjects", "{\"name\":\"Name\",\"departmentId\":\"invalid\"}")]
    [InlineData("topics", "{\"name\":\"Name\",\"subjectId\":\"20000000-0000-0000-0000-000000000001\",\"grade\":10}")]
    [InlineData("topics", "{\"name\":\"Name\",\"subjectId\":\"20000000-0000-0000-0000-000000000001\",\"grade\":9,\"sortOrder\":0}")]
    [InlineData("topics", "{\"name\":\"Name\",\"subjectId\":\"20000000-0000-0000-0000-000000000001\",\"grade\":10,\"sortOrder\":-1}")]
    // Missing required values must fail MVC validation instead of silently accepting CLR defaults.
    public async Task Invalid_payloads_are_rejected_before_the_service(string entity, string json)
    {
        using var app = new CatalogTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, "/api/v1/admin/" + entity + (method == HttpMethod.Put ? "/" + app.Management.Id : ""))
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ValidationFailed", problem.GetProperty("code").GetString());
            Assert.True(problem.TryGetProperty("traceId", out _));
        }
        Assert.Equal(0, app.Management.Calls);
    }

    [Fact]
    // Verify actual query binding and the exact serialized TopicPage/Topic fields.
    public async Task Topic_query_and_page_shape_match_the_contract()
    {
        using var app = new CatalogTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        var result = await client.GetFromJsonAsync<JsonElement>($"/api/v1/topics?page=2&pageSize=3&subjectId={app.Management.Id}&grade=11");
        Assert.Equal(5, result.EnumerateObject().Count());
        Assert.Equal(2, result.GetProperty("page").GetInt32());
        Assert.Equal(3, result.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, result.GetProperty("items")[0].EnumerateObject().Count());
        Assert.Equal(app.Management.Id, app.Management.Query!.SubjectId);
        Assert.Equal(11, app.Management.Query.Grade);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/topics?grade=not-an-integer")).StatusCode);
    }

    [Theory]
    [InlineData(400, "ValidationFailed")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "NotFound")]
    [InlineData(409, "Conflict")]
    // Verify error codes and trace IDs through the registered exception handler.
    public async Task Catalog_failures_return_safe_problem_details(int status, string code)
    {
        using var app = new CatalogTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        app.Management.Failure = new(status, code, "Safe catalog error.");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.DeleteAsync($"/api/v1/admin/topics/{app.Management.Id}");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }
}

public sealed class CatalogTestFactory : AuthTestFactory
{
    public FakeCatalogManagement Management { get; } = new();
    /// <summary>Replace only the catalog gateway while retaining production routing and authentication.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogManagement>();
            services.AddSingleton<ICatalogManagement>(Management);
        });
    }
}

public sealed class FakeCatalogManagement : ICatalogManagement
{
    public Guid Id { get; } = Guid.NewGuid();
    public int Calls { get; private set; }
    public Guid Actor { get; private set; }
    public CatalogQuery? Query { get; private set; }
    public CatalogException? Failure { get; set; }
    /// <summary>Capture dispatch and allow deterministic failure responses in HTTP tests.</summary>
    private void Capture(Guid actor = default) { Calls++; Actor = actor; if (Failure is not null) throw Failure; }
    public Task<CatalogPage<DepartmentView>> DepartmentsAsync(CatalogQuery query, CancellationToken cancellationToken)
    { Capture(); Query = query; return Task.FromResult(new CatalogPage<DepartmentView>(query.Page, query.PageSize, 1, 1, [new DepartmentView(Id, "Synthetic")])); }
    public Task<DepartmentView> SaveDepartmentAsync(Guid actorId, Guid? id, DepartmentInput input, CancellationToken cancellationToken)
    { Capture(actorId); return Task.FromResult(new DepartmentView(Id, "Synthetic")); }
    public Task DeleteDepartmentAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    { Capture(actorId); return Task.CompletedTask; }
    public Task<CatalogPage<SubjectView>> SubjectsAsync(CatalogQuery query, CancellationToken cancellationToken)
    { Capture(); Query = query; return Task.FromResult(new CatalogPage<SubjectView>(query.Page, query.PageSize, 1, 1, [new SubjectView(Id, "Synthetic", Id)])); }
    public Task<SubjectView> SaveSubjectAsync(Guid actorId, Guid? id, SubjectInput input, CancellationToken cancellationToken)
    { Capture(actorId); return Task.FromResult(new SubjectView(Id, "Synthetic", Id)); }
    public Task DeleteSubjectAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    { Capture(actorId); return Task.CompletedTask; }
    public Task<CatalogPage<TopicView>> TopicsAsync(CatalogQuery query, CancellationToken cancellationToken)
    { Capture(); Query = query; return Task.FromResult(new CatalogPage<TopicView>(query.Page, query.PageSize, 1, 1, [new TopicView(Id, Id, 11, "Synthetic", 0)])); }
    public Task<TopicView> SaveTopicAsync(Guid actorId, Guid? id, TopicInput input, CancellationToken cancellationToken)
    { Capture(actorId); return Task.FromResult(new TopicView(Id, Id, 11, "Synthetic", 0)); }
    public Task DeleteTopicAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    { Capture(actorId); return Task.CompletedTask; }
}
