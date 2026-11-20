using ExamMaker.Application.Users;
using ExamMaker.Infrastructure;
using ExamMaker.Infrastructure.Persistence;
using ExamMaker.Infrastructure.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

try
{
    return await BootstrapAsync();
}
catch (UserOperationException exception)
{
    Console.Error.WriteLine($"Bootstrap failed ({exception.Code}). Check configuration and run read-only reconciliation before recovery.");
    return 1;
}
catch
{
    Console.Error.WriteLine("Bootstrap failed. Check AWS permissions, configuration and database connectivity.");
    return 1;
}

/// <summary>Run private administrator provisioning without logging account identifiers or credentials.</summary>
static async Task<int> BootstrapAsync()
{
    // Read sensitive deployment settings from the environment or Parameter Store, never command-line arguments.
    var configuration = new ConfigurationManager();
    configuration.AddEnvironmentVariables();
    configuration.AddExamMakerParameterStore();
    var email = Environment.GetEnvironmentVariable("Bootstrap__Email");
    var fullName = Environment.GetEnvironmentVariable("Bootstrap__FullName");
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName))
    {
        Console.Error.WriteLine("Set Bootstrap__Email and Bootstrap__FullName before running this private tool.");
        return 1;
    }
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddExamMakerPersistence(configuration);
    services.AddUserManagement(configuration);
    using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await scope.ServiceProvider.GetRequiredService<IUserManagement>().BootstrapAsync(email, fullName, timeout.Token);
    Console.WriteLine("Administrator provisioned. Use the Cognito invitation to change the temporary password.");
    return 0;
}
