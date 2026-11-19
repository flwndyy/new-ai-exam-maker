using ExamMaker.Application.Authentication;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ExamMaker.Infrastructure.Authentication;

public sealed class DatabaseUserDirectory(ExamMakerDbContext database, IMemoryCache cache) : IUserDirectory
{
    private sealed record DisplayProfile(string Email, string FullName, string? DepartmentName);

    /// <summary>Đọc quyền mới nhất mỗi request; chỉ cache thông tin hiển thị trong một phút.</summary>
    public async Task<AuthenticatedUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var access = await database.Users.AsNoTracking().Where(x => x.CognitoSub == subject)
            .Select(x => new { x.Id, x.Role, x.DepartmentId, x.IsActive }).SingleOrDefaultAsync(cancellationToken);
        if (access is null) return null;

        // Khóa cache gồm user và tổ, tránh dùng lại tên tổ cũ khi tài khoản chuyển tổ.
        var key = (typeof(DatabaseUserDirectory), access.Id, access.DepartmentId);
        if (!cache.TryGetValue<DisplayProfile>(key, out var display))
        {
            display = await database.Users.AsNoTracking().Where(x => x.Id == access.Id)
                .Select(x => new DisplayProfile(x.Email, x.FullName, x.Department == null ? null : x.Department.Name))
                .SingleOrDefaultAsync(cancellationToken);
            if (display is null) return null;
            cache.Set(key, display, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
                Size = 1
            });
        }
        return new AuthenticatedUser(access.Id, subject, display!.Email, display.FullName,
            access.Role, access.DepartmentId, display.DepartmentName, access.IsActive);
    }
}
