using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ExamMaker.Api.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Api.Tests;

public sealed class AiRateLimitingTests
{
    private static async Task<WebApplication> StartAsync(int permitLimit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAiRateLimiting(new AiRateLimitOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", user.ToString())], "test"));
            }

            await next();
        });
        app.UseRateLimiter();
        app.MapPost("/ai/generate", () => Results.Accepted())
            .RequireRateLimiting(AiRateLimiting.PolicyName);

        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Post(string user) =>
        new(HttpMethod.Post, "/ai/generate") { Headers = { { "X-Test-User", user } } };

    [Fact]
    public async Task RequestsBeyondPermitLimit_Get429ProblemJsonWithRateLimitedCodeAndRetryAfter()
    {
        await using var app = await StartAsync(permitLimit: 2);
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Post("alice"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Post("alice"))).StatusCode);

        using var limited = await client.SendAsync(Post("alice"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.InRange(int.Parse(retryAfter!.Single()), 1, 60);

        using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("RateLimited", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task RejectedResponse_UsesSharedProblemDetailsPipeline_WithSingleTraceId()
    {
        var customized = false;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            customized = true;
            context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
        });
        builder.Services.AddAiRateLimiting(new AiRateLimitOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) });

        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/ai/generate", () => Results.Accepted()).RequireRateLimiting(AiRateLimiting.PolicyName);
        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.SendAsync(Post("alice"));
        using var limited = await client.SendAsync(Post("alice"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(customized);
        using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("RateLimited", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task EachUserHasAnIndependentLimit()
    {
        await using var app = await StartAsync(permitLimit: 1);
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Post("alice"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Post("alice"))).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Post("bob"))).StatusCode);
    }
}
