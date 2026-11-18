using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ExamMaker.Infrastructure;

public static class ParameterStoreConfiguration
{
    public static void AddExamMakerParameterStore(this IConfigurationManager configuration)
    {
        var path = configuration["ParameterStore:Path"];

        if (!string.IsNullOrWhiteSpace(path))
        {
           var ssmBuilder = new ConfigurationBuilder();
ssmBuilder.AddSystemsManager(path);

var sources = configuration.Sources;
var environmentIndex = sources
    .ToList()
    .FindIndex(source => source is EnvironmentVariablesConfigurationSource env
        && string.IsNullOrEmpty(env.Prefix));

try
{
    sources.Insert(
        environmentIndex >= 0 ? environmentIndex : sources.Count,
        ssmBuilder.Sources.Single());
}
catch (Exception)
{
    Console.Error.WriteLine(
        "Failed to load startup configuration from AWS Systems Manager Parameter Store.");
    throw;
}
        }
    }
}
