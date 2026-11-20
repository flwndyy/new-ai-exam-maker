using System.Security.Claims;
using ExamMaker.Api.Authentication;
using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ExamMaker.Api.Tests;

public sealed class AccessPolicyTests
{
    [Theory]
    [InlineData("teacher", "Owner", true, true, true)]
    [InlineData("head", "Owner", true, false, true)]
    [InlineData("admin", "Owner", true, true, false)]
    [InlineData("teacher", "Owner", false, true, false)]
    [InlineData("teacher", "SameDepartment", false, true, true)]
    [InlineData("head", "SameDepartment", false, false, false)]
    [InlineData("admin", "SameDepartment", false, false, true)]
    [InlineData("teacher", "Reviewer", false, true, false)]
    [InlineData("head", "Reviewer", false, true, true)]
    [InlineData("head", "Reviewer", false, false, false)]
    [InlineData("head", "Reviewer", true, true, false)]
    [InlineData("admin", "Reviewer", true, false, false)]
    [InlineData("admin", "Reviewer", false, false, true)]
    // Exercise role, department and ownership rules, including administrator self-review denial.
    public async Task Resource_policies_apply_role_department_and_no_self_review(
        string role, string policy, bool owned, bool sameDepartment, bool expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExamMakerAuthentication(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var user = AuthTestFactory.Teacher with { Role = role };
        scope.ServiceProvider.GetRequiredService<CurrentUser>().SetUser(user);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CognitoBearerEvents.UserIdClaim, user.Id.ToString()), new Claim(ClaimTypes.Role, role)], "Bearer"));
        var resource = new ContentAccessResource(owned ? user.Id : Guid.NewGuid(),
            sameDepartment ? user.DepartmentId!.Value : Guid.NewGuid());
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.Equal(expected, (await authorization.AuthorizeAsync(principal, resource, policy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(principal, null, policy)).Succeeded);
    }

    [Fact]
    // Reject partial Cognito configuration instead of starting with invalid settings.
    public void Partial_cognito_configuration_is_rejected()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cognito:Region"] = "ap-southeast-1"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExamMakerAuthentication(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CognitoOptions>>().Value);
    }

    [Theory]
    [InlineData("https://attacker.invalid", "pool", "client")]
    [InlineData("ap-southeast-1", "us-east-1_EXAMPLE", "client")]
    [InlineData("ap-southeast-1", "ap-southeast-1_EXAMPLE/other", "client")]
    // Reject configuration components that could produce an unintended discovery address.
    public void Invalid_discovery_components_are_not_accepted(string region, string pool, string client)
    {
        Assert.False(new CognitoOptions { Region = region, UserPoolId = pool, ClientId = client }.IsConfigured);
    }
}
