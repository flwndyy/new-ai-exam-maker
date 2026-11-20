using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace ExamMaker.Api.Authorization;

public static class AccessPolicies
{
    // Only administrators may manage users or publish content.
    public const string Admin = "Admin";
    // Teachers and department heads may create content; administrators may not.
    public const string ContentAuthor = "ContentAuthor";
    // Restrict access to the same department, except for administrators.
    public const string SameDepartment = "SameDepartment";
    // Require content owned by the current teacher or department head.
    public const string Owner = "Owner";
    // Allow department heads or administrators to review, but never their own content.
    public const string Reviewer = "Reviewer";
}

// Represent the permission evaluated by the authorization handler.
public sealed record ContentAccessRequirement(string Permission) : IAuthorizationRequirement;

public sealed class ContentAccessHandler(ICurrentUser currentUser)
    : AuthorizationHandler<ContentAccessRequirement, ContentAccessResource>
{
    /// <summary>Apply shared application rules and preserve the specific self-review failure reason.</summary>
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ContentAccessRequirement requirement, ContentAccessResource resource)
    {
        var user = currentUser.User;
        if (user is null || !user.IsActive || context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;
        if (requirement.Permission == AccessPolicies.Reviewer)
        {
            var decision = AccessRules.Review(user, resource);
            if (decision == ReviewAccess.SelfReview)
                context.Fail(new AuthorizationFailureReason(this, ContentAuthorizationResults.SelfReview));
            else if (decision == ReviewAccess.Allowed)
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
        var permitted = requirement.Permission switch
        {
            AccessPolicies.SameDepartment => AccessRules.IsSameDepartment(user, resource),
            AccessPolicies.Owner => AccessRules.IsOwner(user, resource),
            _ => false
        };
        if (permitted) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
