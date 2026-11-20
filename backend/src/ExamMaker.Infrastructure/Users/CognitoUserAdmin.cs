using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Users;
using Microsoft.Extensions.Configuration;

namespace ExamMaker.Infrastructure.Users;

public sealed class CognitoUserAdmin(Func<IAmazonCognitoIdentityProvider> clientFactory, IConfiguration configuration) : ICognitoUserAdmin
{
    // Resolve credentials only for an AWS operation; local profile reads do not need an AWS session.
    private IAmazonCognitoIdentityProvider Client => clientFactory();
    private string Pool => configuration["Cognito:UserPoolId"]
        ?? throw new InvalidOperationException("Cognito:UserPoolId is required for user administration.");
    private static readonly string[] ManagedRoles = [Roles.Teacher, Roles.Head, Roles.Admin];

    /// <summary>Create an invited account; Cognito generates and emails its temporary password.</summary>
    public async Task<CognitoAccount> CreateAsync(string email, string fullName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await AwsAsync((client, token) => client.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = Pool,
                Username = email,
                DesiredDeliveryMediums = [DeliveryMediumType.EMAIL],
                ForceAliasCreation = false,
                UserAttributes = [Attribute("email", email), Attribute("name", fullName), Attribute("email_verified", "true")]
            }, token), cancellationToken);
            return ReadAccount(response.User.Attributes ?? [], response.User.Enabled == true, []);
        }
        catch (UsernameExistsException) { throw Conflict(); }
        catch (AliasExistsException) { throw Conflict(); }
    }

    /// <summary>Assign the initial application group without rereading or rewriting the just-created profile.</summary>
    public async Task AssignRoleAsync(string subject, string role, CancellationToken cancellationToken)
    {
        if (!ManagedRoles.Contains(role)) throw new ArgumentException("Unknown application role.", nameof(role));
        await AwsAsync((client, token) => client.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
        { UserPoolId = Pool, Username = subject, GroupName = role }, token), cancellationToken);
    }

    /// <summary>Look up the immutable subject and reject any unexpected alias resolution.</summary>
    public async Task<CognitoAccount?> FindAsync(string subject, CancellationToken cancellationToken)
    {
        try
        {
            var response = await AwsAsync((client, token) => client.AdminGetUserAsync(new AdminGetUserRequest
            { UserPoolId = Pool, Username = subject }, token), cancellationToken);
            var account = ReadAccount(response.UserAttributes ?? [], response.Enabled == true,
                await GroupsAsync(subject, cancellationToken));
            if (account.Subject != subject) throw new InvalidOperationException("Cognito subject mismatch.");
            return account;
        }
        catch (UserNotFoundException) { return null; }
    }

    /// <summary>Read every page of pool users and their groups for read-only reconciliation.</summary>
    public async Task<IReadOnlyList<CognitoAccount>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<CognitoAccount>();
        string? next = null;
        do
        {
            var page = await AwsAsync((client, token) => client.ListUsersAsync(new ListUsersRequest
            { UserPoolId = Pool, Limit = 60, PaginationToken = next }, token), cancellationToken);
            foreach (var user in page.Users ?? [])
            {
                var account = ReadAccount(user.Attributes ?? [], user.Enabled == true, []);
                result.Add(account with { Groups = await GroupsAsync(account.Subject, cancellationToken) });
            }
            next = page.PaginationToken;
        } while (!string.IsNullOrEmpty(next));
        return result;
    }

    /// <summary>Synchronize trusted admin attributes and managed roles, preserving unrelated groups.</summary>
    public async Task SetProfileAsync(CognitoAccount profile, CancellationToken cancellationToken)
    {
        try
        {
            var current = await FindAsync(profile.Subject, cancellationToken)
                ?? throw new UserNotFoundException("Account no longer exists.");
            await AwsAsync((client, token) => client.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
            {
                UserPoolId = Pool,
                Username = profile.Subject,
                UserAttributes = [Attribute("email", profile.Email), Attribute("name", profile.FullName),
                    Attribute("email_verified", "true")]
            }, token), cancellationToken);
            // Restore an unverified snapshot in two steps so email-change verification cannot defer the address update.
            if (!profile.EmailVerified)
                await AwsAsync((client, token) => client.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
                {
                    UserPoolId = Pool,
                    Username = profile.Subject,
                    UserAttributes = [Attribute("email_verified", "false")]
                }, token), cancellationToken);
            var desired = profile.Groups.Where(ManagedRoles.Contains).Distinct().ToArray();
            foreach (var role in current.Groups.Where(ManagedRoles.Contains).Except(desired))
                await AwsAsync((client, token) => client.AdminRemoveUserFromGroupAsync(new AdminRemoveUserFromGroupRequest
                { UserPoolId = Pool, Username = profile.Subject, GroupName = role }, token), cancellationToken);
            foreach (var role in desired.Except(current.Groups))
                await AwsAsync((client, token) => client.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
                { UserPoolId = Pool, Username = profile.Subject, GroupName = role }, token), cancellationToken);
        }
        catch (AliasExistsException) { throw Conflict(); }
    }

    /// <summary>Enable or disable Cognito sign-in without changing local permissions.</summary>
    public async Task SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
            await AwsAsync((client, token) => client.AdminEnableUserAsync(new AdminEnableUserRequest
            { UserPoolId = Pool, Username = subject }, token), cancellationToken);
        else
            await AwsAsync((client, token) => client.AdminDisableUserAsync(new AdminDisableUserRequest
            { UserPoolId = Pool, Username = subject }, token), cancellationToken);
    }

    /// <summary>Revoke refresh sessions after the database has already denied account access.</summary>
    public async Task GlobalSignOutAsync(string subject, CancellationToken cancellationToken) =>
        await AwsAsync((client, token) => client.AdminUserGlobalSignOutAsync(new AdminUserGlobalSignOutRequest
        { UserPoolId = Pool, Username = subject }, token), cancellationToken);

    /// <summary>Remove only an account created by a failed provisioning operation.</summary>
    public async Task DeleteAsync(string subject, CancellationToken cancellationToken) =>
        await AwsAsync((client, token) => client.AdminDeleteUserAsync(new AdminDeleteUserRequest
        { UserPoolId = Pool, Username = subject }, token), cancellationToken);

    /// <summary>Follow all group pages, including non-application groups.</summary>
    private async Task<IReadOnlyList<string>> GroupsAsync(string subject, CancellationToken cancellationToken)
    {
        var groups = new List<string>();
        string? next = null;
        do
        {
            var page = await AwsAsync((client, token) => client.AdminListGroupsForUserAsync(new AdminListGroupsForUserRequest
            { UserPoolId = Pool, Username = subject, Limit = 60, NextToken = next }, token), cancellationToken);
            groups.AddRange((page.Groups ?? []).Select(x => x.GroupName));
            next = page.NextToken;
        } while (!string.IsNullOrEmpty(next));
        return groups;
    }

    /// <summary>Parse a remote profile and require its immutable subject before using it for mutations.</summary>
    private static CognitoAccount ReadAccount(List<AttributeType> attributes, bool enabled, IReadOnlyList<string> groups)
    {
        var subject = attributes.SingleOrDefault(x => x.Name == "sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("Cognito returned no subject.");
        return new(subject, attributes.SingleOrDefault(x => x.Name == "email")?.Value ?? "",
            attributes.SingleOrDefault(x => x.Name == "name")?.Value ?? "",
            attributes.SingleOrDefault(x => x.Name == "email_verified")?.Value == "true", enabled, groups);
    }

    /// <summary>Bound async SDK calls with cancellation; ClientConfig.Timeout alone does not affect async calls.</summary>
    private async Task<T> AwsAsync<T>(Func<IAmazonCognitoIdentityProvider, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UserManagementDeadlines.AwsCall);
        return await call(Client, timeout.Token);
    }

    private static AttributeType Attribute(string name, string value) => new() { Name = name, Value = value };

    private static UserOperationException Conflict() => new(409, "Conflict", "Email đã tồn tại trong Cognito.");
}
