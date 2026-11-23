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

public sealed class AdminAiUsageTests : IDisposable
{
    private readonly AuthTestFactory _auth = new();
    private readonly FakeUsageReport _report = new();
    private readonly WebApplicationFactory<Program> _host;

    public AdminAiUsageTests()
    {
        _host = _auth.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiUsageReportReader>();
            services.AddSingleton<IAiUsageReportReader>(_report);
        }));
    }

    public void Dispose()
    {
        _host.Dispose();
        _auth.Dispose();
    }

    private HttpClient Client(string? role)
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

    [Fact]
    public async Task Admin_gets_rows_and_totals_in_contract_shape()
    {
        using var client = Client(Roles.Admin);
        var userId = Guid.NewGuid();

        using var response = await client.GetAsync(
            $"/api/v1/admin/ai-usage?page=1&pageSize=20&userId={userId}&from=2026-11-01&to=2026-11-23");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items")[0];
        Assert.Equal("Synthetic Teacher", item.GetProperty("user").GetProperty("fullName").GetString());
        Assert.Equal("2026-11-23", item.GetProperty("date").GetString());
        Assert.Equal(2, item.GetProperty("jobs").GetInt32());
        Assert.Equal(16000, item.GetProperty("tokens").GetInt64());
        Assert.Equal(0.096m, item.GetProperty("estimatedCostUsd").GetDecimal());
        Assert.Equal(1, body.GetProperty("totalItems").GetInt32());
        Assert.Equal(0.096m, body.GetProperty("totals").GetProperty("estimatedCostUsd").GetDecimal());
        Assert.Equal(
            new AiUsageFilter(userId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 23), 1, 20),
            _report.LastFilter);
    }

    [Theory]
    [InlineData(Roles.Teacher)]
    [InlineData(Roles.Head)]
    public async Task Non_admin_is_forbidden(string role)
    {
        using var client = Client(role);

        using var response = await client.GetAsync("/api/v1/admin/ai-usage");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_report.LastFilter);
    }

    [Fact]
    public async Task Missing_token_is_unauthorized()
    {
        using var client = Client(null);

        using var response = await client.GetAsync("/api/v1/admin/ai-usage");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_page_size_is_a_validation_problem()
    {
        using var client = Client(Roles.Admin);

        using var response = await client.GetAsync("/api/v1/admin/ai-usage?pageSize=500");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("pageSize", out _));
        Assert.Null(_report.LastFilter);
    }

    private sealed class FakeUsageReport : IAiUsageReportReader
    {
        public AiUsageFilter? LastFilter { get; private set; }

        public Task<AiUsagePage> ReadAsync(AiUsageFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            var row = new AiUsageRow(
                new AiUserRef(Guid.NewGuid(), "Synthetic Teacher"), new DateOnly(2026, 11, 23), 2, 16000, 0.096m);
            return Task.FromResult(new AiUsagePage([row], filter.Page, filter.PageSize, 1, 1, new AiUsageTotals(2, 16000, 0.096m)));
        }
    }
}
