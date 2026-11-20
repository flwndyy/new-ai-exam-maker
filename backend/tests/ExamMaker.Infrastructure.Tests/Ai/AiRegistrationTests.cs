using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class AiRegistrationTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings, string? environment = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (environment is not null)
        {
            services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        }

        // Mirrors Program.cs: the DbContext comes from the persistence registration.
        services.AddExamMakerPersistence(configuration);
        services.AddExamMakerAi(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["ConnectionStrings:ExamMaker"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        ["Sqs:AiJobsQueueUrl"] = "https://sqs.ap-southeast-1.amazonaws.com/000000000000/ai-jobs",
        ["AWS:Region"] = "ap-southeast-1",
    };

    [Fact]
    public void Message_body_contains_only_the_job_id()
    {
        var jobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var body = SqsAiJobQueue.CreateMessageBody(jobId);

        using var json = JsonDocument.Parse(body);
        var properties = json.RootElement.EnumerateObject().ToList();
        var only = Assert.Single(properties);
        Assert.Equal("jobId", only.Name);
        Assert.Equal(jobId, only.Value.GetGuid());
    }

    [Fact]
    public void Quota_options_default_to_the_decisions_in_context_md()
    {
        using var provider = BuildProvider(BaseSettings());

        var options = provider.GetRequiredService<AiQuotaOptions>();

        Assert.Equal(10, options.JobsPerUserPerDay);
        Assert.Equal(10, options.MaxQuestionsPerJob);
        Assert.Equal(100_000, options.TokensPerUserPerDay);
        Assert.Equal(500_000, options.SystemTokensPerDay);
    }

    [Fact]
    public void Quota_options_are_overridable_from_configuration()
    {
        var settings = BaseSettings();
        settings["AiQuota:JobsPerUserPerDay"] = "3";
        settings["AiQuota:SystemTokensPerDay"] = "1000";
        using var provider = BuildProvider(settings);

        var options = provider.GetRequiredService<AiQuotaOptions>();

        Assert.Equal(3, options.JobsPerUserPerDay);
        Assert.Equal(1000, options.SystemTokensPerDay);
        Assert.Equal(10, options.MaxQuestionsPerJob);
    }

    [Fact]
    public void Generation_service_resolves_with_all_dependencies_in_a_scope()
    {
        using var provider = BuildProvider(BaseSettings());
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AiGenerationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AiJobQueries>());
        Assert.IsType<EfAiJobReader>(scope.ServiceProvider.GetRequiredService<IAiJobReader>());
        Assert.IsType<EfAiCatalogReader>(scope.ServiceProvider.GetRequiredService<IAiCatalogReader>());
    }

    [Fact]
    public void Missing_queue_url_fails_with_a_clear_message_when_the_queue_is_resolved()
    {
        var settings = BaseSettings();
        settings.Remove("Sqs:AiJobsQueueUrl");
        using var provider = BuildProvider(settings);

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IAiJobQueue>());

        Assert.Contains("Sqs:AiJobsQueueUrl", error.Message);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Missing_queue_url_still_fails_outside_development(string environment)
    {
        var settings = BaseSettings();
        settings.Remove("Sqs:AiJobsQueueUrl");
        using var provider = BuildProvider(settings, environment);

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IAiJobQueue>());

        Assert.Contains("Sqs:AiJobsQueueUrl", error.Message);
    }

    [Fact]
    public async Task Missing_queue_url_in_development_uses_a_logging_queue_that_accepts_jobs()
    {
        var settings = BaseSettings();
        settings.Remove("Sqs:AiJobsQueueUrl");
        using var provider = BuildProvider(settings, Environments.Development);

        var queue = provider.GetRequiredService<IAiJobQueue>();

        Assert.IsType<LoggingAiJobQueue>(queue);
        await queue.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
    }

    [Fact]
    public void Configured_queue_url_uses_sqs_even_in_development()
    {
        using var provider = BuildProvider(BaseSettings(), Environments.Development);

        Assert.IsType<SqsAiJobQueue>(provider.GetRequiredService<IAiJobQueue>());
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "ExamMaker.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
