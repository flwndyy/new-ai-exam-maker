using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ExamMaker.Application.Authentication;

namespace ExamMaker.Application.Users;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateUserInput(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(200, MinimumLength = 1)] string FullName,
    [Required] string Role, Guid? DepartmentId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateUserInput(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(200, MinimumLength = 1)] string FullName,
    [Required] string Role, Guid? DepartmentId, [Required] bool? IsActive);

public sealed record UserListQuery(int Page = 1, int PageSize = 20, string? Role = null,
    Guid? DepartmentId = null, bool? IsActive = null, string? Q = null);
public sealed record AdminUser(Guid Id, string CognitoSub, string Email, string FullName,
    string Role, Guid? DepartmentId, bool IsActive, DateTimeOffset CreatedAt);
public sealed record AdminUserPage(int Page, int PageSize, int TotalItems, int TotalPages, IReadOnlyList<AdminUser> Items);
public sealed record UserMismatch(Guid? UserId, string Email, string? DbRole,
    IReadOnlyList<string> CognitoGroups, string Issue);
public sealed record ReconcileReport(DateTimeOffset CheckedAt, int TotalUsers, IReadOnlyList<UserMismatch> Mismatches);

public sealed class UserOperationException(int statusCode, string code, string message,
    string? diagnosticType = null, string? diagnosticCode = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    // Safe server diagnostics are not serialized into API responses.
    public string? DiagnosticType { get; } = diagnosticType;
    public string? DiagnosticCode { get; } = diagnosticCode;
}

public interface IUserManagement
{
    Task<AdminUserPage> ListAsync(UserListQuery query, CancellationToken cancellationToken);
    Task<AdminUser> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminUser> CreateAsync(Guid actorId, CreateUserInput input, CancellationToken cancellationToken);
    Task<AdminUser> UpdateAsync(Guid actorId, Guid id, UpdateUserInput input, CancellationToken cancellationToken);
    Task DeactivateAsync(Guid actorId, Guid id, CancellationToken cancellationToken);
    Task<ReconcileReport> ReconcileAsync(CancellationToken cancellationToken);
}

// Only the private deployment tool registers an implementation of this interface.
public interface IAdminBootstrapper
{
    Task<AdminUser> BootstrapAsync(string email, string fullName, CancellationToken cancellationToken);
}

// The gateway isolates AWS transport from user-management rules and database tests.
public sealed record CognitoAccount(string Subject, string Email, string FullName,
    bool EmailVerified, bool Enabled, IReadOnlyList<string> Groups);

public interface ICognitoUserAdmin
{
    Task<CognitoAccount> CreateAsync(string email, string fullName, CancellationToken cancellationToken);
    Task AssignRoleAsync(string subject, string role, CancellationToken cancellationToken);
    Task<CognitoAccount?> FindAsync(string subject, CancellationToken cancellationToken);
    Task<IReadOnlyList<CognitoAccount>> ListAsync(CancellationToken cancellationToken);
    Task SetProfileAsync(CognitoAccount profile, CancellationToken cancellationToken);
    Task SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken);
    Task GlobalSignOutAsync(string subject, CancellationToken cancellationToken);
    Task DeleteAsync(string subject, CancellationToken cancellationToken);
}

public static class UserInputRules
{
    /// <summary>Normalize and validate administrator input before touching AWS or PostgreSQL.</summary>
    public static CreateUserInput Normalize(CreateUserInput input)
    {
        var normalized = input with { Email = (input.Email ?? "").Trim().ToLowerInvariant(), FullName = (input.FullName ?? "").Trim() };
        if (normalized.Email.Length is < 1 or > 254 || !new EmailAddressAttribute().IsValid(normalized.Email)
            || normalized.FullName.Length is < 1 or > 200
            || normalized.Role is not (Roles.Teacher or Roles.Head or Roles.Admin)
            || normalized.DepartmentId == Guid.Empty
            || normalized.Role != Roles.Admin && normalized.DepartmentId is null)
            throw new UserOperationException(400, "ValidationFailed", "Thông tin người dùng hoặc tổ chuyên môn không hợp lệ.");
        return normalized;
    }

    /// <summary>Prevent administrators from removing their own administrative access.</summary>
    public static void EnsureNotSelfLockout(Guid actorId, Guid targetId, string role, bool active)
    {
        if (actorId == targetId && (role != Roles.Admin || !active))
            throw new UserOperationException(409, "Conflict", "Không thể tự hạ vai trò hoặc vô hiệu hóa chính mình.");
    }
}
