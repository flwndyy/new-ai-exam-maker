using ExamMaker.Api.Authentication;
using ExamMaker.Infrastructure;
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
builder.Services.AddControllers();
// Bổ sung mã lỗi ổn định và mã tra log, kể cả khi DB đang lỗi.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
    {
        400 => "ValidationFailed",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "NotFound",
        >= 500 => "InternalError",
        _ => "RequestFailed"
    });
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
});
builder.Services.AddHealthChecks();
builder.Services.AddExamMakerAuthentication(builder.Configuration);

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Xác thực trước, sau đó mới kiểm tra quyền truy cập endpoint.
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program
{
}
