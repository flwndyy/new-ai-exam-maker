using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamMaker.Application.Auditing;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Api.Tests;

public sealed class AuditLogsHttpTests
{
    [Fact]
    public async Task Teacher_cannot_read_audit_logs()
    {
        using var app = new AuditLogsTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());

        using var response = await client.GetAsync("/api/v1/audit-logs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, app.Reader.Calls);
    }

    [Theory]
    [InlineData(Roles.Head)]
    [InlineData(Roles.Admin)]
    public async Task Head_and_admin_receive_audit_logs_and_query_filters_are_bound(string role)
    {
        using var app = new AuditLogsTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = role };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());

        var entityId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var url = $"/api/v1/audit-logs?page=2&pageSize=10&entityType=Question&entityId={entityId}&actorId={actorId}&action=ApproveQuestion&from=2026-11-20T00:00:00Z&to=2026-11-22T00:00:00Z";
        var body = await client.GetFromJsonAsync<JsonElement>(url);

        Assert.Equal(1, app.Reader.Calls);
        Assert.Equal(AuthTestFactory.Teacher.Id, app.Reader.ActorId);
        Assert.Equal(2, app.Reader.LastQuery?.Page);
        Assert.Equal(10, app.Reader.LastQuery?.PageSize);
        Assert.Equal("Question", app.Reader.LastQuery?.EntityType);
        Assert.Equal(entityId, app.Reader.LastQuery?.EntityId);
        Assert.Equal(actorId, app.Reader.LastQuery?.ActorId);
        Assert.Equal("ApproveQuestion", app.Reader.LastQuery?.Action);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), app.Reader.LastQuery?.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero).AddTicks(TimeSpan.TicksPerSecond - 1), app.Reader.LastQuery?.To);

        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Object, items[0].GetProperty("actor").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("actor").ValueKind);
    }

    [Fact]
    public async Task AuditException_is_translated_to_problem_details()
    {
        using var app = new AuditLogsTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = Roles.Head };
        app.Reader.Error = new AuditException(400, "ValidationFailed", "Invalid entityType", "entityType");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());

        using var response = await client.GetAsync("/api/v1/audit-logs?entityType=Bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("entityType", out _));
    }

    [Fact]
    public async Task Offsetless_dates_default_to_utc_and_unparseable_page_returns_validation_code()
    {
        using var app = new AuditLogsTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = Roles.Head };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());

        using var okResponse = await client.GetAsync("/api/v1/audit-logs?from=2026-11-20&to=2026-11-20");
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), app.Reader.LastQuery?.From);
        Assert.Equal(new DateTimeOffset(new DateOnly(2026, 11, 20).ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc), TimeSpan.Zero), app.Reader.LastQuery?.To);

        using var fractionalToResponse = await client.GetAsync("/api/v1/audit-logs?to=2026-11-20T10:00:00.250Z");
        Assert.Equal(HttpStatusCode.OK, fractionalToResponse.StatusCode);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, 250, TimeSpan.Zero), app.Reader.LastQuery?.To);

        using var badPage = await client.GetAsync("/api/v1/audit-logs?page=abc");
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
        var body = await badPage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("page", out _));
    }
}

public sealed class AuditLogsTestFactory : AuthTestFactory
{
    public FakeAuditLogReader Reader { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuditLogReader>();
            services.AddSingleton<IAuditLogReader>(Reader);
        });
    }
}

public sealed class FakeAuditLogReader : IAuditLogReader
{
    public AuditException? Error { get; set; }
    public int Calls { get; private set; }
    public Guid ActorId { get; private set; }
    public AuditLogQuery? LastQuery { get; private set; }

    public Task<AuditLogPage> ListAsync(Guid actorId, AuditLogQuery query, CancellationToken cancellationToken)
    {
        Calls++;
        ActorId = actorId;
        LastQuery = query;
        if (Error is not null) throw Error;

        var now = DateTime.UtcNow;
        var items = new List<AuditLogView>
        {
            new(Guid.NewGuid(), new AuditLogActor(actorId, "Reviewer"), "Question", Guid.NewGuid(), "ApproveQuestion", "Submitted", "Approved", null, now),
            new(Guid.NewGuid(), null, "User", Guid.NewGuid(), "CreateUser", null, "Active", "Bootstrap administrator CLI.", now.AddMinutes(-1))
        };
        return Task.FromResult(new AuditLogPage(query.Page, query.PageSize, items.Count, 1, items));
    }
}
