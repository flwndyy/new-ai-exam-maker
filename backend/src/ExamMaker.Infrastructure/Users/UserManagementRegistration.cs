using Amazon;
using Amazon.CognitoIdentityProvider;
using ExamMaker.Application.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExamMaker.Infrastructure.Users;

public static class UserManagementRegistration
{
    /// <summary>Register lazy AWS clients using the default credential chain, never embedded credentials.</summary>
    public static IServiceCollection AddUserManagement(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IAmazonCognitoIdentityProvider>(_ =>
        {
            var region = configuration["Cognito:Region"];
            if (string.IsNullOrWhiteSpace(region)) throw new InvalidOperationException("Cognito:Region is required.");
            return new AmazonCognitoIdentityProviderClient(new AmazonCognitoIdentityProviderConfig
            { RegionEndpoint = RegionEndpoint.GetBySystemName(region), Timeout = UserManagementDeadlines.AwsCall, MaxErrorRetry = 0 });
        });
        services.AddScoped<ICognitoUserAdmin>(provider =>
            new CognitoUserAdmin(() => provider.GetRequiredService<IAmazonCognitoIdentityProvider>(), configuration));
        services.AddScoped<UserManagementService>();
        services.AddScoped<IUserManagement>(provider => provider.GetRequiredService<UserManagementService>());
        return services;
    }

    /// <summary>Opt into bootstrap capability only in the private deployment tool.</summary>
    public static IServiceCollection AddAdminBootstrapper(this IServiceCollection services) =>
        services.AddScoped<IAdminBootstrapper, AdminBootstrapper>();
}
