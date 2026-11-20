using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using ExamMaker.Application.Users;
using ExamMaker.Infrastructure.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Infrastructure.Tests;

public sealed class CognitoUserAdminTests
{
    private static CognitoUserAdmin Gateway(StubCognitoClient client) => new(() => client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Cognito:UserPoolId"] = "ap-southeast-1_EXAMPLE" }).Build());

    [Fact]
    public void Resolving_the_gateway_does_not_load_aws_credentials()
    {
        var services = new ServiceCollection();
        services.AddUserManagement(new ConfigurationBuilder().Build());
        services.AddScoped<IAmazonCognitoIdentityProvider>(_ => throw new InvalidOperationException("AWS credentials must not be requested yet."));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICognitoUserAdmin>());
    }

    [Fact]
    // Check the actual AWS request contract without sending invitations or accessing an AWS account.
    public async Task Invitation_uses_generated_temporary_password_without_stealing_an_existing_alias()
    {
        using var client = new StubCognitoClient();
        var account = await Gateway(client).CreateAsync("test@example.invalid", "Test", default);
        Assert.Equal("subject-1", account.Subject);
        Assert.Null(client.Created!.TemporaryPassword);
        Assert.Null(client.Created.MessageAction);
        Assert.False(client.Created.ForceAliasCreation == true);
        Assert.Equal(new[] { "EMAIL" }, client.Created.DesiredDeliveryMediums);
    }

    [Fact]
    public async Task Role_updates_follow_group_pages_and_preserve_unrelated_groups()
    {
        using var client = new StubCognitoClient();
        await Gateway(client).SetProfileAsync(new("subject-1", "new@example.invalid", "New", true, true, ["head"]), default);
        Assert.Equal(new[] { "teacher" }, client.Removed);
        Assert.Equal(new[] { "head" }, client.Added);
        Assert.DoesNotContain("outside-group", client.Removed);
        Assert.Equal("subject-1", client.Updated!.Username);
        Assert.Contains(client.Updated.UserAttributes, x => x.Name == "email" && x.Value == "new@example.invalid");
        Assert.Contains(client.Updated.UserAttributes, x => x.Name == "email_verified" && x.Value == "true");
        Assert.Equal(2, client.GroupPages);
    }

    [Fact]
    public async Task Reconciliation_reads_every_user_and_group_page()
    {
        using var client = new StubCognitoClient();
        var accounts = await Gateway(client).ListAsync(default);
        Assert.Equal(new[] { "subject-1", "subject-2" }, accounts.Select(x => x.Subject));
        Assert.Equal(2, client.UserPages);
        Assert.Equal(4, client.GroupPages);
        Assert.All(accounts, x => Assert.Contains("outside-group", x.Groups));
    }

    [Fact]
    public async Task Restoring_an_unverified_snapshot_updates_the_email_before_resetting_verification()
    {
        using var client = new StubCognitoClient();
        await Gateway(client).SetProfileAsync(new("subject-1", "old@example.invalid", "Old", false, true, ["teacher"]), default);
        Assert.Equal(2, client.Updates.Count);
        Assert.Contains(client.Updates[0].UserAttributes, x => x.Name == "email" && x.Value == "old@example.invalid");
        Assert.Contains(client.Updates[0].UserAttributes, x => x.Name == "email_verified" && x.Value == "true");
        Assert.Single(client.Updates[1].UserAttributes);
        Assert.Equal("false", client.Updates[1].UserAttributes[0].Value);
    }

    [Fact]
    public async Task Subject_mismatch_is_rejected_before_profile_mutation()
    {
        using var client = new StubCognitoClient { MismatchedSubject = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway(client).SetProfileAsync(
            new("subject-1", "new@example.invalid", "New", true, true, ["admin"]), default));
        Assert.Null(client.Updated);
        Assert.Empty(client.Added);
    }
}

public sealed class StubCognitoClient() : AmazonCognitoIdentityProviderClient(new AnonymousAWSCredentials(),
    new AmazonCognitoIdentityProviderConfig { RegionEndpoint = RegionEndpoint.APSoutheast1 })
{
    public AdminCreateUserRequest? Created { get; private set; }
    public AdminUpdateUserAttributesRequest? Updated { get; private set; }
    public List<AdminUpdateUserAttributesRequest> Updates { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Added { get; } = [];
    public int UserPages { get; private set; }
    public int GroupPages { get; private set; }
    public bool MismatchedSubject { get; set; }
    private static List<AttributeType> Attributes(string subject) =>
        [new() { Name = "sub", Value = subject }, new() { Name = "email", Value = "test@example.invalid" },
            new() { Name = "name", Value = "Test" }, new() { Name = "email_verified", Value = "true" }];

    public override Task<AdminCreateUserResponse> AdminCreateUserAsync(AdminCreateUserRequest request, CancellationToken cancellationToken = default)
    {
        Created = request;
        return Task.FromResult(new AdminCreateUserResponse { User = new UserType { Attributes = Attributes("subject-1"), Enabled = true } });
    }
    public override Task<AdminGetUserResponse> AdminGetUserAsync(AdminGetUserRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdminGetUserResponse { UserAttributes = Attributes(MismatchedSubject ? "other-subject" : request.Username), Enabled = true });
    public override Task<AdminListGroupsForUserResponse> AdminListGroupsForUserAsync(AdminListGroupsForUserRequest request, CancellationToken cancellationToken = default)
    {
        GroupPages++;
        return Task.FromResult(new AdminListGroupsForUserResponse
        {
            Groups = [new GroupType { GroupName = request.NextToken is null ? "teacher" : "outside-group" }],
            NextToken = request.NextToken is null ? "next-group" : null
        });
    }
    public override Task<ListUsersResponse> ListUsersAsync(ListUsersRequest request, CancellationToken cancellationToken = default)
    {
        UserPages++;
        return Task.FromResult(new ListUsersResponse
        {
            Users = [new UserType { Attributes = Attributes(request.PaginationToken is null ? "subject-1" : "subject-2"), Enabled = true }],
            PaginationToken = request.PaginationToken is null ? "next-user" : null
        });
    }
    public override Task<AdminUpdateUserAttributesResponse> AdminUpdateUserAttributesAsync(AdminUpdateUserAttributesRequest request, CancellationToken cancellationToken = default)
    { Updated = request; Updates.Add(request); return Task.FromResult(new AdminUpdateUserAttributesResponse()); }
    public override Task<AdminRemoveUserFromGroupResponse> AdminRemoveUserFromGroupAsync(AdminRemoveUserFromGroupRequest request, CancellationToken cancellationToken = default)
    { Removed.Add(request.GroupName); return Task.FromResult(new AdminRemoveUserFromGroupResponse()); }
    public override Task<AdminAddUserToGroupResponse> AdminAddUserToGroupAsync(AdminAddUserToGroupRequest request, CancellationToken cancellationToken = default)
    { Added.Add(request.GroupName); return Task.FromResult(new AdminAddUserToGroupResponse()); }
}
