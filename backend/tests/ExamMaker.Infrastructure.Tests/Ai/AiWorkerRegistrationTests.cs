using Amazon.BedrockRuntime;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class AiWorkerRegistrationTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:ExamMaker"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        ["Sqs:AiJobsQueueUrl"] = "https://sqs.ap-southeast-1.amazonaws.com/000000000000/ai-jobs",
        ["Bedrock:ModelId"] = "example-model",
        ["AWS:Region"] = "ap-southeast-1",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExamMakerPersistence(configuration);
        services.AddExamMakerAi(configuration);
        services.AddExamMakerAiWorker(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Worker_registration_resolves_handler_and_recovery_in_a_scope()
    {
        using var provider = Build(Settings());
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AiJobMessageHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AiJobRecovery>());
        Assert.IsType<BedrockAiModelClient>(provider.GetRequiredService<IAiModelClient>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<IAiJobWorkStore>(),
            scope.ServiceProvider.GetRequiredService<IAiJobRecoveryStore>());
    }

    [Fact]
    public void Bedrock_sdk_client_does_not_retry_by_itself()
    {
        using var provider = Build(Settings());

        Assert.Equal(0, provider.GetRequiredService<IAmazonBedrockRuntime>().Config.MaxErrorRetry);
    }

    [Fact]
    public void Bedrock_region_comes_from_Bedrock_Region_before_AWS_Region()
    {
        // O12 stores /exammaker/<env>/Bedrock/Region; AWS:Region stays the fallback for the other AWS clients.
        var settings = Settings();
        settings["Bedrock:Region"] = "us-east-1";
        using var provider = Build(settings);

        Assert.Equal(Amazon.RegionEndpoint.USEast1, provider.GetRequiredService<IAmazonBedrockRuntime>().Config.RegionEndpoint);
    }

    [Fact]
    public void Bedrock_region_falls_back_to_AWS_Region()
    {
        using var provider = Build(Settings());

        Assert.Equal(Amazon.RegionEndpoint.APSoutheast1, provider.GetRequiredService<IAmazonBedrockRuntime>().Config.RegionEndpoint);
    }

    [Fact]
    public void Bedrock_http_timeout_does_not_cut_calls_before_the_configured_timeout()
    {
        // Without an explicit Timeout the SDK's HttpClient default (100 s) would end calls before Bedrock:TimeoutSeconds.
        var settings = Settings();
        settings["Bedrock:TimeoutSeconds"] = "150";
        using var provider = Build(settings);

        Assert.True(provider.GetRequiredService<IAmazonBedrockRuntime>().Config.Timeout > TimeSpan.FromSeconds(150));
    }

    [Fact]
    public void Binds_worker_options_with_defaults()
    {
        using var provider = Build(Settings());

        var options = provider.GetRequiredService<AiWorkerOptions>();
        Assert.Equal((2, 300, 60, 10, 60, 10, 15), (options.MaxConcurrency, options.LeaseSeconds, options.HeartbeatSeconds,
            options.JobTimeoutMinutes, options.RecoveryIntervalSeconds, options.RequeueAfterMinutes, options.StaleQueuedWarnMinutes));
        Assert.Equal(120, provider.GetRequiredService<BedrockOptions>().TimeoutSeconds);
        Assert.False(provider.GetRequiredService<AiGenerationOptions>().VerifyAnswers);
    }

    [Fact]
    public void Binds_configured_worker_and_generation_values()
    {
        var settings = Settings();
        settings["Worker:MaxConcurrency"] = "4";
        settings["AiGeneration:VerifyAnswers"] = "true";
        using var provider = Build(settings);

        Assert.Equal(4, provider.GetRequiredService<AiWorkerOptions>().MaxConcurrency);
        Assert.True(provider.GetRequiredService<AiGenerationOptions>().VerifyAnswers);
    }

    [Theory]
    [InlineData("Sqs:AiJobsQueueUrl")]
    [InlineData("Bedrock:ModelId")]
    public void IsAiWorkerConfigured_false_when_model_or_queue_missing(string missingKey)
    {
        var settings = Settings();
        settings[missingKey] = " ";

        Assert.False(AiWorkerServiceCollectionExtensions.IsAiWorkerConfigured(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Fact]
    public void IsAiWorkerConfigured_true_with_queue_and_model()
    {
        Assert.True(AiWorkerServiceCollectionExtensions.IsAiWorkerConfigured(
            new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()));
    }
}
