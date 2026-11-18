using Microsoft.Extensions.Configuration;

namespace ExamMaker.Infrastructure;

public static class ParameterStoreConfiguration
{
    public static void AddExamMakerParameterStore(this IConfigurationManager configuration)
    {
        var path = configuration["ParameterStore:Path"];

        if (!string.IsNullOrWhiteSpace(path))
        {
            configuration.AddSystemsManager(path);
            configuration.AddEnvironmentVariables();
        }
    }
}
