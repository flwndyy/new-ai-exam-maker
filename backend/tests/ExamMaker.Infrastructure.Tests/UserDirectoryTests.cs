using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ExamMaker.Infrastructure.Tests;

public sealed class UserDirectoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact]
    // Dùng PostgreSQL thật để kiểm cache hiển thị không giữ quyền hoặc trạng thái tài khoản cũ.
    public async Task Cached_display_never_hides_role_department_or_active_changes()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var firstDepartment = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var secondDepartment = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var subject = Guid.NewGuid().ToString();
        var user = new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = subject,
            Email = "test@example.invalid",
            FullName = "Synthetic user",
            Role = "teacher",
            DepartmentId = firstDepartment,
            IsActive = true
        };
        database.Users.Add(user);
        await database.SaveChangesAsync();
        var directory = new DatabaseUserDirectory(database, cache);
        Assert.Equal("teacher", (await directory.FindBySubjectAsync(subject, default))!.Role);
        user.Role = "head";
        user.DepartmentId = secondDepartment;
        await database.SaveChangesAsync();
        var changed = await directory.FindBySubjectAsync(subject, default);
        Assert.Equal("head", changed!.Role);
        Assert.Equal(secondDepartment, changed.DepartmentId);
        Assert.Equal(await database.Departments.Where(x => x.Id == secondDepartment).Select(x => x.Name).SingleAsync(), changed.DepartmentName);
        user.IsActive = false;
        await database.SaveChangesAsync();
        Assert.False((await directory.FindBySubjectAsync(subject, default))!.IsActive);
        database.Users.Remove(user);
        await database.SaveChangesAsync();
        Assert.Null(await directory.FindBySubjectAsync(subject, default));
    }

    [PostgresFact]
    // User mới được tạo phải đọc được ngay; admin được phép không thuộc tổ.
    public async Task Missing_users_are_not_cached_and_admin_can_have_no_department()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var directory = new DatabaseUserDirectory(database, cache);
        var subject = Guid.NewGuid().ToString();
        Assert.Null(await directory.FindBySubjectAsync(subject, default));
        database.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = subject,
            Email = "admin@example.invalid",
            FullName = "Synthetic admin",
            Role = "admin",
            IsActive = true
        });
        await database.SaveChangesAsync();
        var admin = await directory.FindBySubjectAsync(subject, default);
        Assert.NotNull(admin);
        Assert.Equal("admin", admin.Role);
        Assert.Null(admin.DepartmentId);
        Assert.Null(admin.DepartmentName);
    }
}
