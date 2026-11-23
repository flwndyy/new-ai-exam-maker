using Amazon;
using Amazon.BedrockRuntime;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>Services used only by ExamMaker.Worker; call after <c>AddExamMakerPersistence</c> and <c>AddExamMakerAi</c>.</summary>
public static class AiWorkerServiceCollectionExtensions
{
    public const string ModelIdKey = BedrockOptions.SectionName + ":ModelId";

    /// <summary>The worker can run only with both a queue URL and a model id.</summary>
    public static bool IsAiWorkerConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[AiServiceCollectionExtensions.QueueUrlKey])
        && !string.IsNullOrWhiteSpace(configuration[ModelIdKey]);

    public static IServiceCollection AddExamMakerAiWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(Bind<AiWorkerOptions>(configuration, AiWorkerOptions.SectionName));
        services.AddSingleton(Bind<AiGenerationOptions>(configuration, AiGenerationOptions.SectionName));
        var bedrock = Bind<BedrockOptions>(configuration, BedrockOptions.SectionName);
        services.AddSingleton(bedrock);

        // Credentials come from the EC2 instance profile. No SDK retries: SQS redelivery counts and backs them off.
        // The HTTP timeout must outlast Bedrock:TimeoutSeconds (the adapter's own limit), or the SDK default of 100 s
        // would cut long generations short.
        services.AddSingleton<IAmazonBedrockRuntime>(_ =>
        {
            var config = new AmazonBedrockRuntimeConfig
            {
                MaxErrorRetry = 0,
                Timeout = TimeSpan.FromSeconds(bedrock.TimeoutSeconds + 30),
            };
            var region = configuration["AWS:Region"];
            if (!string.IsNullOrWhiteSpace(region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            return new AmazonBedrockRuntimeClient(config);
        });
        services.AddSingleton<IAiModelClient>(provider => new BedrockAiModelClient(
            provider.GetRequiredService<IAmazonBedrockRuntime>(), provider.GetRequiredService<BedrockOptions>()));

        services.AddScoped<EfAiJobWorkStore>();
        services.AddScoped<IAiJobWorkStore>(provider => provider.GetRequiredService<EfAiJobWorkStore>());
        services.AddScoped<IAiJobRecoveryStore>(provider => provider.GetRequiredService<EfAiJobWorkStore>());
        services.AddScoped<AiJobProcessor>();
        services.AddScoped(provider => new AiJobMessageHandler(
            provider.GetRequiredService<AiJobProcessor>(), provider.GetRequiredService<ExamMaker.Application.Ai.IAiJobQueue>(),
            provider.GetRequiredService<ILogger<AiJobMessageHandler>>()));
        services.AddScoped<AiJobRecovery>();

        return services;
    }

    private static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var options = new T();
        configuration.GetSection(section).Bind(options);
        return options;
    }
}
