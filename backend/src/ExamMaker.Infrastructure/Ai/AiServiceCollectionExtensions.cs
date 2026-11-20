using Amazon;
using Amazon.SQS;
using ExamMaker.Application.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ExamMaker.Infrastructure.Ai;

public static class AiServiceCollectionExtensions
{
    public const string QueueUrlKey = "Sqs:AiJobsQueueUrl";

    public static IServiceCollection AddExamMakerAi(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new AiQuotaOptions();
        configuration.GetSection(AiQuotaOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddSingleton(TimeProvider.System);

        // Credentials come from the EC2 instance profile; the region is optional configuration.
        services.AddSingleton<IAmazonSQS>(_ =>
        {
            var region = configuration["AWS:Region"];
            return string.IsNullOrWhiteSpace(region)
                ? new AmazonSQSClient()
                : new AmazonSQSClient(RegionEndpoint.GetBySystemName(region));
        });

        // The URL is resolved lazily so hosts without SQS configured (tests, tooling) still start.
        services.AddSingleton<IAiJobQueue>(provider =>
        {
            var queueUrl = configuration[QueueUrlKey];
            if (string.IsNullOrWhiteSpace(queueUrl))
            {
                // Only local development may run without a queue; elsewhere a missing URL is a deployment error.
                if (provider.GetService<IHostEnvironment>()?.IsDevelopment() == true)
                {
                    return ActivatorUtilities.CreateInstance<LoggingAiJobQueue>(provider);
                }

                throw new InvalidOperationException($"Configuration value '{QueueUrlKey}' is not set.");
            }

            return new SqsAiJobQueue(provider.GetRequiredService<IAmazonSQS>(), queueUrl);
        });

        services.AddScoped<IAiCatalogReader, EfAiCatalogReader>();
        services.AddScoped<IAiUsageReader, EfAiUsageReader>();
        services.AddScoped<IAiJobStore, EfAiJobStore>();
        services.AddScoped<IAiJobReader, EfAiJobReader>();
        services.AddScoped<AiGenerationService>();
        services.AddScoped<AiJobQueries>();

        return services;
    }
}
