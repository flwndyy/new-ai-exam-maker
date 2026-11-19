using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Api.Ai;

public sealed class AiRateLimitOptions
{
    public int PermitLimit { get; set; } = 5;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

public static class AiRateLimiting
{
    /// <summary>Policy name to apply with RequireRateLimiting on POST /ai/generate.</summary>
    public const string PolicyName = "ai-generate";

    /// <summary>
    /// Adds a per-user fixed-window limiter. This is a burst guard in front of the daily quota
    /// (docs/context.md section 6); exceeding it returns 429 problem+json with code RateLimited.
    /// </summary>
    public static IServiceCollection AddAiRateLimiting(
        this IServiceCollection services, AiRateLimitOptions options)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = options.Window,
                    QueueLimit = 0,
                }));
            limiter.OnRejected = async (rejected, ct) =>
            {
                var http = rejected.HttpContext;
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? value
                    : options.Window;
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

                var problem = new ProblemDetails
                {
                    Type = "https://exammaker.example/problems/rate-limited",
                    Title = "Bạn thao tác quá nhanh, vui lòng thử lại sau.",
                    Status = StatusCodes.Status429TooManyRequests,
                    Instance = http.Request.Path,
                };
                problem.Extensions["code"] = "RateLimited";

                await http.Response.WriteAsJsonAsync(
                    problem, options: null, contentType: "application/problem+json", cancellationToken: ct);
            };
        });

        return services;
    }

    // The user id comes from the token subject; anonymous callers fall back to the remote address.
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue("sub")
        ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
}
