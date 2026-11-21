using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExamMaker.Application.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Api.Tests;

public sealed class AdminUsersTests
{
    [Fact]
    // Global MVC validation returns the same camelCase field keys for admin profiles as for questions.
    public async Task Admin_binding_errors_use_json_field_names()
    {
        using var app = new AdminTestFactory(); app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token());
        using var response = await client.PostAsJsonAsync("/api/v1/admin/users", new
        { email = "invalid-email", fullName = "Synthetic", role = "teacher", departmentId = AuthTestFactory.DepartmentId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("email", out _)); Assert.Equal(0, app.Management.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("teacher")]
    [InlineData("head")]
    // Exercise every administration route through real JWT and role authorization middleware.
    public async Task Administration_is_inaccessible_without_an_active_admin(string? role)
    {
        using var app = new AdminTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = role ?? "teacher" };
        using var client = app.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        var id = Guid.NewGuid();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, ""), (HttpMethod.Get, $"/{id}"), (HttpMethod.Post, ""),
            (HttpMethod.Put, $"/{id}"), (HttpMethod.Delete, $"/{id}"), (HttpMethod.Post, "/reconcile")
        })
        {
            using var request = new HttpRequestMessage(method, "/api/v1/admin/users" + path);
            request.Content = JsonContent.Create(new { });
            using var response = await client.SendAsync(request);
            Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, app.Management.Calls);
    }

    [Fact]
    // Check the admin response contract, pagination, route dispatch and soft-delete status.
    public async Task Admin_routes_return_contract_profiles_and_use_the_verified_actor()
    {
        using var app = new AdminTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        Assert.Null(app.Services.GetService<IAdminBootstrapper>());
        using var created = await client.PostAsJsonAsync("/api/v1/admin/users", new
        { email = "new@example.invalid", fullName = "New User", role = "teacher", departmentId = AuthTestFactory.DepartmentId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.EndsWith($"/api/v1/admin/users/{app.Management.Profile.Id}", created.Headers.Location!.ToString());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(8, body.EnumerateObject().Count());
        Assert.True(body.TryGetProperty("createdAt", out _));
        Assert.False(body.TryGetProperty("password", out _));
        Assert.Equal(AuthTestFactory.Teacher.Id, app.Management.LastActor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/admin/users/{app.Management.Profile.Id}")).StatusCode);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/users?page=2&pageSize=5&q=Test");
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(5, app.Management.Query!.PageSize);
        Assert.Equal("Test", app.Management.Query.Q);
        using var updated = await client.PutAsJsonAsync($"/api/v1/admin/users/{app.Management.Profile.Id}", new
        { email = "new@example.invalid", fullName = "New Name", role = "teacher", departmentId = AuthTestFactory.DepartmentId, isActive = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/admin/users/{app.Management.Profile.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/admin/users/reconcile", null)).StatusCode);
    }

    [Theory]
    [InlineData("{\"email\":\"bad\",\"fullName\":\"Name\",\"role\":\"teacher\"}")]
    [InlineData("{\"email\":\"test@example.invalid\",\"role\":\"teacher\"}")]
    [InlineData("{\"email\":\"test@example.invalid\",\"fullName\":\"Name\",\"role\":\"teacher\",\"password\":\"not-accepted\"}")]
    // The public API never accepts a password or silently ignores a provisioning input typo.
    public async Task Invalid_or_password_bearing_create_inputs_are_rejected(string json)
    {
        using var app = new AdminTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        using var response = await client.PostAsync("/api/v1/admin/users", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Management.Calls);
    }

    [Fact]
    // A missing activation flag must not silently disable a user.
    public async Task Update_requires_an_explicit_activation_flag()
    {
        using var app = new AdminTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        using var response = await client.PutAsJsonAsync($"/api/v1/admin/users/{Guid.NewGuid()}", new
        { email = "test@example.invalid", fullName = "Name", role = "admin" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Management.Calls);
    }

    [Fact]
    // Verify that a service conflict is returned safely by the actual GET route.
    public async Task Get_returns_safe_conflict_problem()
    {
        using var app = new AdminTestFactory();
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin" };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        app.Management.ThrowConflict = true;
        using var conflict = await client.GetAsync($"/api/v1/admin/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Conflict", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.False(problem.TryGetProperty("diagnosticType", out _));
        Assert.False(problem.TryGetProperty("diagnosticCode", out _));
    }
}

public sealed class AdminTestFactory : AuthTestFactory
{
    public FakeUserManagement Management { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserManagement>();
            services.AddSingleton<IUserManagement>(Management);
        });
    }
}

public sealed class FakeUserManagement : IUserManagement
{
    public AdminUser Profile { get; } = new(Guid.NewGuid(), "synthetic-sub", "new@example.invalid", "New User", "teacher",
        AuthTestFactory.DepartmentId, true, DateTimeOffset.UtcNow);
    public int Calls { get; private set; }
    public Guid LastActor { get; private set; }
    public UserListQuery? Query { get; private set; }
    public bool ThrowConflict { get; set; }
    public Task<AdminUserPage> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    { Calls++; Query = query; return Task.FromResult(new AdminUserPage(query.Page, query.PageSize, 1, 1, [Profile])); }
    public Task<AdminUser> GetAsync(Guid id, CancellationToken cancellationToken)
    { Calls++; if (ThrowConflict) throw new UserOperationException(409, "Conflict", "Xung đột dữ liệu.", "SyntheticException", "SyntheticCode"); return Task.FromResult(Profile); }
    public Task<AdminUser> CreateAsync(Guid actorId, CreateUserInput input, CancellationToken cancellationToken)
    { Calls++; LastActor = actorId; return Task.FromResult(Profile); }
    public Task<AdminUser> UpdateAsync(Guid actorId, Guid id, UpdateUserInput input, CancellationToken cancellationToken)
    { Calls++; LastActor = actorId; return Task.FromResult(Profile); }
    public Task DeactivateAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    { Calls++; LastActor = actorId; return Task.CompletedTask; }
    public Task<ReconcileReport> ReconcileAsync(CancellationToken cancellationToken)
    { Calls++; return Task.FromResult(new ReconcileReport(DateTimeOffset.UtcNow, 1, [])); }
}
