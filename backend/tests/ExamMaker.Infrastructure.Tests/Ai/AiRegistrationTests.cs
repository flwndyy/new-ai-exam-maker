using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class AiRegistrationTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
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
}
