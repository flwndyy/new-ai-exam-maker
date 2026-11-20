namespace ExamMaker.Application.Authentication;

// Database-verified profile; token roles and groups do not grant application permissions.
public sealed record AuthenticatedUser(
    Guid Id, string CognitoSub, string Email, string FullName, string Role,
    Guid? DepartmentId, string? DepartmentName, bool IsActive);

// Keep user lookup independent of EF Core and the API layer.
public interface IUserDirectory
{
    /// <summary>Find a local account by sub; return null when no account has been provisioned.</summary>
    Task<AuthenticatedUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
}

// Expose the verified request profile without decoding the JWT again.
public interface ICurrentUser
{
    AuthenticatedUser? User { get; }
}
