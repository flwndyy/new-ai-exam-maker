namespace ExamMaker.Application.Authentication;

// Hồ sơ đã đối chiếu DB; không lấy quyền từ claim role/group trong token.
public sealed record AuthenticatedUser(
    Guid Id, string CognitoSub, string Email, string FullName, string Role,
    Guid? DepartmentId, string? DepartmentName, bool IsActive);

// Tách hợp đồng đọc user khỏi EF Core để API không tự chứa truy vấn database.
public interface IUserDirectory
{
    /// <summary>Tìm tài khoản nội bộ theo sub; trả null nếu chưa được admin tạo.</summary>
    Task<AuthenticatedUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
}

// Cho controller/use case đọc hồ sơ đã xác thực mà không phải tự giải mã JWT lần nữa.
public interface ICurrentUser
{
    AuthenticatedUser? User { get; }
}
