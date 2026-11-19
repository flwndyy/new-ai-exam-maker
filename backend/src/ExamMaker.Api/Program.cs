using ExamMaker.Api.Ai;
using ExamMaker.Infrastructure;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Persistence;
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
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();

public partial class Program
{
}
