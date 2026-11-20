using ExamMaker.Api.Ai;
using ExamMaker.Api.Authentication;
using ExamMaker.Api.Users;
using ExamMaker.Infrastructure;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Persistence;
using ExamMaker.Infrastructure.Users;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddExamMakerParameterStore();

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddExamMakerPersistence(builder.Configuration);
builder.Services.AddExamMakerAi(builder.Configuration);
builder.Services.AddAiRateLimiting(
    builder.Configuration.GetSection("AiRateLimit").Get<AiRateLimitOptions>() ?? new AiRateLimitOptions());
builder.Services.AddUserManagement(builder.Configuration);
builder.Services.AddExceptionHandler<UserOperationExceptionHandler>();
builder.Services.AddControllers();
// Include stable error codes and trace identifiers, including for database failures.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
    {
        400 => "ValidationFailed",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "NotFound",
        409 => "Conflict",
        429 => "RateLimited",
        >= 500 => "InternalError",
        _ => "RequestFailed"
    });
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
});
builder.Services.AddHealthChecks();
builder.Services.AddExamMakerAuthentication(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Nginx must normalize CloudFront-Viewer-Address to one client hop first.
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
});

var app = builder.Build();

// Only the local Nginx reverse proxy is trusted to supply forwarded headers.
app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Authenticate before evaluating endpoint authorization.
app.UseAuthentication();
// After authentication so the AI limiter can partition by the authenticated user, not the IP.
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program
{
}
