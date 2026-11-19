using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace ExamMaker.Api.Authorization;

public static class AccessPolicies
{
    // Chỉ quản trị viên được sử dụng chức năng quản trị/publish.
    public const string Admin = "Admin";
    // Giáo viên và tổ trưởng tạo nội dung; admin không được tạo nội dung.
    public const string ContentAuthor = "ContentAuthor";
    // Giới hạn tài nguyên theo tổ, trừ quản trị viên.
    public const string SameDepartment = "SameDepartment";
    // Kiểm tra tài nguyên do chính giáo viên/tổ trưởng hiện tại tạo.
    public const string Owner = "Owner";
    // Duyệt nội dung: tổ trưởng cùng tổ hoặc admin, nhưng không được tự duyệt.
    public const string Reviewer = "Reviewer";
}

// Controller/use case phải tải các ID này từ DB, không nhận quyền do client tự khai báo.
public sealed record ContentAccessResource(Guid CreatedBy, Guid DepartmentId);
// Đại diện điều kiện mà handler phải kiểm tra trước khi chấp nhận một policy.
public sealed record ContentAccessRequirement(string Permission) : IAuthorizationRequirement;

public sealed class ContentAccessHandler(ICurrentUser currentUser)
    : AuthorizationHandler<ContentAccessRequirement, ContentAccessResource>
{
    /// <summary>Kiểm role/tổ/ownership từ DB; head và admin đều không được tự duyệt.</summary>
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ContentAccessRequirement requirement, ContentAccessResource resource)
    {
        var user = currentUser.User;
        if (user is null || !user.IsActive || context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;
        var permitted = requirement.Permission switch
        {
            AccessPolicies.SameDepartment => user.Role == "admin" || user.DepartmentId == resource.DepartmentId,
            AccessPolicies.Owner => user.Role is "teacher" or "head" && user.Id == resource.CreatedBy,
            AccessPolicies.Reviewer => user.Id != resource.CreatedBy
                && (user.Role == "admin" || user.Role == "head" && user.DepartmentId == resource.DepartmentId),
            _ => false
        };
        if (permitted) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
