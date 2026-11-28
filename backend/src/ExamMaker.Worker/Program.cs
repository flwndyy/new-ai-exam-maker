using ExamMaker.Infrastructure;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Persistence;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddExamMakerParameterStore();

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var aiWorkerConfigured = AiWorkerServiceCollectionExtensions.IsAiWorkerConfigured(builder.Configuration);
if (aiWorkerConfigured)
{
    builder.Services.AddExamMakerPersistence(builder.Configuration);
    builder.Services.AddExamMakerAi(builder.Configuration);
    builder.Services.AddExamMakerAiWorker(builder.Configuration);
    builder.Services.AddHostedService<AiJobConsumerService>();
    builder.Services.AddHostedService<AiJobRecoveryService>();
}
else if (!builder.Environment.IsDevelopment())
{
    // Outside local development a missing queue or model is a deployment error, not a reason to idle silently.
    throw new InvalidOperationException(
        $"The AI worker needs '{AiServiceCollectionExtensions.QueueUrlKey}' and '{AiWorkerServiceCollectionExtensions.ModelIdKey}'.");
}

var host = builder.Build();

if (!aiWorkerConfigured)
{
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
        "AI worker is not configured ({QueueKey}, {ModelKey}); no AI jobs will be processed.",
        AiServiceCollectionExtensions.QueueUrlKey, AiWorkerServiceCollectionExtensions.ModelIdKey);
}

await host.RunAsync();
