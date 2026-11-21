using ExamMaker.Api.Ai;
using ExamMaker.Api.Authentication;
using ExamMaker.Api.Catalog;
using ExamMaker.Api.Questions;
using ExamMaker.Api.Users;
using ExamMaker.Application.Catalog;
using ExamMaker.Application.Questions;
using ExamMaker.Infrastructure;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Catalog;
using ExamMaker.Infrastructure.Persistence;
using ExamMaker.Infrastructure.Questions;
using ExamMaker.Infrastructure.Users;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Serilog;
using System.Net;
using System.Text.Json;

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
// Keep catalog persistence scoped to the request and translate its safe application errors.
builder.Services.AddScoped<ICatalogManagement, CatalogManagement>();
builder.Services.AddExceptionHandler<CatalogExceptionHandler>();
// Keep the question workflow scoped so its row changes and audit share one DbContext transaction.
builder.Services.AddScoped<IQuestionManagement, QuestionManagement>();
builder.Services.AddExceptionHandler<QuestionExceptionHandler>();
builder.Services.AddControllers(options =>
    options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .ConfigureApiBehaviorOptions(options =>
    {
        var defaultFactory = options.InvalidModelStateResponseFactory;
        // Preserve standard ProblemDetails while giving binding and attribute validation the same JSON field keys.
        options.InvalidModelStateResponseFactory = context =>
        {
            var result = defaultFactory(context);
            if (result is ObjectResult { Value: ValidationProblemDetails problem })
            {
                problem.Extensions.TryAdd("code", "ValidationFailed");
                problem.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
                var normalized = problem.Errors.GroupBy(x => NormalizeValidationKey(x.Key))
                    .ToDictionary(x => x.Key, x => x.SelectMany(e => e.Value).ToArray());
                problem.Errors.Clear();
                foreach (var error in normalized) problem.Errors.Add(error.Key, error.Value);
            }
            return result;
        };
    });
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

// JSON converter errors use paths such as $.grade; clients use the same camelCase keys as application validation.
static string NormalizeValidationKey(string key)
{
    if (key is "" or "$") return "body";
    if (key.StartsWith("$.", StringComparison.Ordinal)) key = key[2..];
    return string.Join('.', key.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}

public partial class Program
{
}
