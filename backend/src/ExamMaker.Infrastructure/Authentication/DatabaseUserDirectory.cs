using ExamMaker.Application.Authentication;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ExamMaker.Infrastructure.Authentication;

public sealed class DatabaseUserDirectory(ExamMakerDbContext database, IMemoryCache cache) : IUserDirectory
{
    private sealed record DisplayProfile(string Email, string FullName, string? DepartmentName);

    /// <summary>Read current permissions on each request; cache only display data for one minute.</summary>
    public async Task<AuthenticatedUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var access = await database.Users.AsNoTracking().Where(x => x.CognitoSub == subject)
            .Select(x => new { x.Id, x.Role, x.DepartmentId, x.IsActive }).SingleOrDefaultAsync(cancellationToken);
        if (access is null) return null;

        // Include the department in the cache key to avoid reusing its previous display name.
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
