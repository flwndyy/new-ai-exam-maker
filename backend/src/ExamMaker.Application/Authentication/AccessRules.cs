namespace ExamMaker.Application.Authentication;

public static class Roles
{
    public const string Teacher = "teacher";
    public const string Head = "head";
    public const string Admin = "admin";
}

// Resource identifiers must come from persisted content, not client permission claims.
public sealed record ContentAccessResource(Guid CreatedBy, Guid DepartmentId);

public enum ReviewAccess
{
    Forbidden,
    Allowed,
    SelfReview
}

public static class AccessRules
{
    /// <summary>Allow active administrators or active users in the resource department.</summary>
    public static bool IsSameDepartment(AuthenticatedUser user, ContentAccessResource resource) =>
        user.IsActive && (user.Role == Roles.Admin
            || user.Role is Roles.Teacher or Roles.Head && user.DepartmentId == resource.DepartmentId);

    /// <summary>Allow active teachers or heads to manage their own content.</summary>
    public static bool IsOwner(AuthenticatedUser user, ContentAccessResource resource) =>
        user.IsActive && user.Role is Roles.Teacher or Roles.Head && user.Id == resource.CreatedBy;

    /// <summary>Check reviewer eligibility before distinguishing self-review from other denials.</summary>
    public static ReviewAccess Review(AuthenticatedUser user, ContentAccessResource resource)
    {
        if (!user.IsActive || !(user.Role == Roles.Admin
            || user.Role == Roles.Head && user.DepartmentId == resource.DepartmentId))
            return ReviewAccess.Forbidden;
        return user.Id == resource.CreatedBy ? ReviewAccess.SelfReview : ReviewAccess.Allowed;
    }
}
