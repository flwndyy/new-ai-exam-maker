using ExamMaker.Application.Authentication;

namespace ExamMaker.Api.Authentication;

// Chỉ giữ user của request đang xử lý; không dùng singleton hoặc biến static.
public sealed class CurrentUser : ICurrentUser
{
    public AuthenticatedUser? User { get; private set; }

    /// <summary>Ghi hồ sơ DB vào scope request sau khi token đã vượt qua xác thực.</summary>
    public void SetUser(AuthenticatedUser user) => User = user;
}
