using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ExamMaker.Api.Authentication;
using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ExamMaker.Api.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    // Thiếu token phải nhận 401 đúng định dạng, không truy vấn tài khoản.
    public async Task Missing_token_returns_problem_and_bearer_challenge()
    {
        using var app = new AuthTestFactory();
        using var client = app.CreateClient();
        await AssertProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "Unauthorized");
        Assert.Equal(0, app.Directory.Reads);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("id-token")]
    [InlineData("no-sub")]
    [InlineData("no-exp")]
    [InlineData("no-use")]
    [InlineData("no-client")]
    [InlineData("signature")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("malformed")]
    [InlineData("duplicate-client")]
    [InlineData("duplicate-sub")]
    // Thử token sai/hết hạn/giả mạo và bảo đảm chúng bị chặn trước khi truy cập DB.
    public async Task Invalid_tokens_are_rejected_before_database_access(string fault)
    {
        using var app = new AuthTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token(fault));
        await AssertProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "Unauthorized");
        Assert.Equal(0, app.Directory.Reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("bad-role")]
    [InlineData("no-department")]
    // User chưa tồn tại, bị khóa hoặc có quyền không hợp lệ đều không được đăng nhập.
    public async Task Unavailable_local_users_cannot_authenticate(string state)
    {
        using var app = new AuthTestFactory();
        app.Directory.User = state switch
        {
            "missing" => null,
            "disabled" => AuthTestFactory.Teacher with { IsActive = false },
            "bad-role" => AuthTestFactory.Teacher with { Role = "root" },
            _ => AuthTestFactory.Teacher with { DepartmentId = null }
        };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        await AssertProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no-aud")]
    // Kiểm hồ sơ /me lấy từ DB; claim admin giả trong token không làm tăng quyền.
    public async Task Me_returns_database_profile_and_does_not_trust_token_roles(string? tokenMode)
    {
        using var app = new AuthTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token(tokenMode));
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AuthTestFactory.Teacher.Id, me.GetProperty("id").GetGuid());
        Assert.Equal("teacher", me.GetProperty("role").GetString());
        Assert.Equal("Synthetic teacher", me.GetProperty("fullName").GetString());
        Assert.Equal(AuthTestFactory.Teacher.DepartmentId, me.GetProperty("departmentId").GetGuid());
        Assert.Equal(7, me.EnumerateObject().Count());
        Assert.False(me.TryGetProperty("cognitoSub", out _));
        Assert.False(me.TryGetProperty("token", out _));
        await AssertProblemAsync(await client.GetAsync("/_auth-tests/admin"), HttpStatusCode.Forbidden, "Forbidden");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_auth-tests/author")).StatusCode);
    }

    [Fact]
    // Giữ nguyên token nhưng đổi/khóa tài khoản để kiểm quyền được cập nhật ở request sau.
    public async Task Account_changes_take_effect_with_the_same_token_on_the_next_request()
    {
        using var app = new AuthTestFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        app.Directory.User = AuthTestFactory.Teacher with { Role = "admin", DepartmentId = null, DepartmentName = null };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_auth-tests/admin")).StatusCode);
        await AssertProblemAsync(await client.GetAsync("/_auth-tests/author"), HttpStatusCode.Forbidden, "Forbidden");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(JsonValueKind.Null, me.GetProperty("departmentId").ValueKind);
        app.Directory.User = AuthTestFactory.Teacher with { IsActive = false };
        await AssertProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "Unauthorized");
        using var anonymous = app.CreateClient();
        await AssertProblemAsync(await anonymous.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    // Chưa cấu hình Cognito thì từ chối đăng nhập, nhưng health check vẫn chạy.
    public async Task Unconfigured_auth_fails_closed_but_health_remains_public()
    {
        using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        await AssertProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "Unauthorized");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    // DB lỗi phải trả 500, không cấp quyền và không che lỗi hệ thống thành lỗi token.
    public async Task Database_failure_returns_a_server_error_instead_of_authenticating_or_masking_it_as_401()
    {
        using var app = new AuthTestFactory();
        app.Directory.ThrowOnRead = true;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic database failure", body);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("InternalError", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    /// <summary>Đối chiếu lỗi HTTP với định dạng code/traceId và không để lộ token.</summary>
    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
            if (status == HttpStatusCode.Unauthorized) Assert.Contains(response.Headers.WwwAuthenticate, x => x.Scheme == "Bearer");
        }
    }
}

public sealed class AuthTestFactory : WebApplicationFactory<Program>
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;
    public const string Issuer = "https://cognito-idp.ap-southeast-1.amazonaws.com/ap-southeast-1_EXAMPLE";
    public const string ClientId = "exampleclient";
    public static readonly Guid DepartmentId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly AuthenticatedUser Teacher = new(
        Guid.Parse("40000000-0000-0000-0000-000000000001"), "synthetic-sub", "test@example.invalid",
        "Synthetic teacher", "teacher", DepartmentId, "Synthetic department", true);
    public FakeUserDirectory Directory { get; } = new();

    // Tạo khóa RSA tạm riêng cho test host, không sử dụng khóa hoặc token thật.
    public AuthTestFactory() => key = new RsaSecurityKey(rsa) { KeyId = "synthetic-key" };

    /// <summary>Thay discovery và kho user trong test host; production không có đường bỏ qua xác thực.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cognito:Region"] = "ap-southeast-1",
            ["Cognito:UserPoolId"] = "ap-southeast-1_EXAMPLE",
            ["Cognito:ClientId"] = ClientId
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);
            services.AddControllers().AddApplicationPart(typeof(AuthenticationProbeController).Assembly);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var discovery = new OpenIdConnectConfiguration { Issuer = Issuer };
                discovery.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            });
        });
    }

    /// <summary>Tạo token RSA tạm và các lỗi có chủ đích để thử toàn bộ pipeline JWT.</summary>
    public string Token(string? fault = null)
    {
        if (fault == "malformed") return "this-is-not-a-jwt";
        var claims = new List<Claim> { new("role", "admin"), new("cognito:groups", "admin") };
        if (fault != "no-sub") claims.Add(new Claim("sub", Teacher.CognitoSub));
        if (fault != "no-use") claims.Add(new Claim("token_use", fault == "id-token" ? "id" : "access"));
        if (fault != "no-client") claims.Add(new Claim("client_id", fault == "client" ? "other-client" : ClientId));
        if (fault == "duplicate-client") claims.Add(new Claim("client_id", ClientId));
        if (fault == "duplicate-sub") claims.Add(new Claim("sub", Teacher.CognitoSub));
        var now = DateTime.UtcNow;
        DateTime? expires = fault == "no-exp" ? null : fault == "expired" ? now.AddMinutes(-5) : now.AddMinutes(15);
        var notBefore = fault == "future" ? now.AddMinutes(5) : now.AddMinutes(-10);
        using var otherRsa = RSA.Create(2048);
        SigningCredentials? credentials = fault switch
        {
            "signature" => new SigningCredentials(new RsaSecurityKey(otherRsa) { KeyId = key.KeyId }, SecurityAlgorithms.RsaSha256),
            "algorithm" => new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = key.KeyId }, SecurityAlgorithms.HmacSha256),
            "unsigned" => null,
            _ => new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            fault == "issuer" ? "https://other.example.invalid" : Issuer,
            fault == "no-aud" ? null : ClientId, claims, notBefore, expires, credentials));
    }

    /// <summary>Giải phóng khóa ký tạm khi host kiểm thử đã dừng.</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) rsa.Dispose();
    }
}

public sealed class FakeUserDirectory : IUserDirectory
{
    public AuthenticatedUser? User { get; set; } = AuthTestFactory.Teacher;
    public bool ThrowOnRead { get; set; }
    public int Reads { get; private set; }

    /// <summary>Mô phỏng lookup DB; vẫn chạy xác thực chữ ký và policy thật trong API.</summary>
    public Task<AuthenticatedUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        Reads++;
        if (ThrowOnRead) throw new InvalidOperationException("synthetic database failure");
        return Task.FromResult(User?.CognitoSub == subject ? User : null);
    }
}

[ApiController]
[Route("_auth-tests")]
public sealed class AuthenticationProbeController : ControllerBase
{
    [HttpGet("admin")]
    [Authorize(Policy = AccessPolicies.Admin)]
    // Endpoint chỉ nằm trong test assembly để kiểm policy quản trị.
    public IActionResult Admin() => Ok();

    [HttpGet("author")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    // Endpoint chỉ nằm trong test assembly để kiểm quyền tạo nội dung của teacher/head.
    public IActionResult Author() => Ok();
}
