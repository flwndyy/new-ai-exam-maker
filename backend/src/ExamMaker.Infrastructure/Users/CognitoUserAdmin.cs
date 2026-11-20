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
            var response = await Client.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = Pool,
                Username = email,
                DesiredDeliveryMediums = [DeliveryMediumType.EMAIL],
                ForceAliasCreation = false,
                UserAttributes = [Attribute("email", email), Attribute("name", fullName), Attribute("email_verified", "true")]
            }, cancellationToken);
            return ReadAccount(response.User.Attributes ?? [], response.User.Enabled == true, []);
        }
        catch (UsernameExistsException) { throw Conflict(); }
        catch (AliasExistsException) { throw Conflict(); }
    }

    /// <summary>Look up the immutable subject and reject any unexpected alias resolution.</summary>
    public async Task<CognitoAccount?> FindAsync(string subject, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Client.AdminGetUserAsync(new AdminGetUserRequest
            { UserPoolId = Pool, Username = subject }, cancellationToken);
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
            var page = await Client.ListUsersAsync(new ListUsersRequest
            { UserPoolId = Pool, Limit = 60, PaginationToken = next }, cancellationToken);
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
            await Client.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
            {
                UserPoolId = Pool,
                Username = profile.Subject,
                UserAttributes = [Attribute("email", profile.Email), Attribute("name", profile.FullName),
                    Attribute("email_verified", "true")]
            }, cancellationToken);
            // Restore an unverified snapshot in two steps so email-change verification cannot defer the address update.
            if (!profile.EmailVerified)
                await Client.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
                {
                    UserPoolId = Pool,
                    Username = profile.Subject,
                    UserAttributes = [Attribute("email_verified", "false")]
                }, cancellationToken);
            var desired = profile.Groups.Where(ManagedRoles.Contains).Distinct().ToArray();
            foreach (var role in current.Groups.Where(ManagedRoles.Contains).Except(desired))
                await Client.AdminRemoveUserFromGroupAsync(new AdminRemoveUserFromGroupRequest
                { UserPoolId = Pool, Username = profile.Subject, GroupName = role }, cancellationToken);
            foreach (var role in desired.Except(current.Groups))
                await Client.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
                { UserPoolId = Pool, Username = profile.Subject, GroupName = role }, cancellationToken);
        }
        catch (AliasExistsException) { throw Conflict(); }
    }

    /// <summary>Enable or disable Cognito sign-in without changing local permissions.</summary>
    public async Task SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
            await Client.AdminEnableUserAsync(new AdminEnableUserRequest
            { UserPoolId = Pool, Username = subject }, cancellationToken);
        else
            await Client.AdminDisableUserAsync(new AdminDisableUserRequest
            { UserPoolId = Pool, Username = subject }, cancellationToken);
    }

    /// <summary>Revoke refresh sessions after the database has already denied account access.</summary>
    public async Task GlobalSignOutAsync(string subject, CancellationToken cancellationToken) =>
        await Client.AdminUserGlobalSignOutAsync(new AdminUserGlobalSignOutRequest
        { UserPoolId = Pool, Username = subject }, cancellationToken);

    /// <summary>Remove only an account created by a failed provisioning operation.</summary>
    public async Task DeleteAsync(string subject, CancellationToken cancellationToken) =>
        await Client.AdminDeleteUserAsync(new AdminDeleteUserRequest
        { UserPoolId = Pool, Username = subject }, cancellationToken);

    /// <summary>Follow all group pages, including non-application groups.</summary>
    private async Task<IReadOnlyList<string>> GroupsAsync(string subject, CancellationToken cancellationToken)
    {
        var groups = new List<string>();
        string? next = null;
        do
        {
            var page = await Client.AdminListGroupsForUserAsync(new AdminListGroupsForUserRequest
            { UserPoolId = Pool, Username = subject, Limit = 60, NextToken = next }, cancellationToken);
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

    private static AttributeType Attribute(string name, string value) => new() { Name = name, Value = value };

    private static UserOperationException Conflict() => new(409, "Conflict", "Email đã tồn tại trong Cognito.");
}
