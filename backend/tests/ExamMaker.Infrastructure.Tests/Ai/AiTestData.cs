using ExamMaker.Application.Ai;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

internal static class AiTestData
{
    // Every test uses its own user and its own far-future UTC date, so tests that share the
    // class fixture database never see each other's usage rows.
    public static async Task<Guid> CreateUserAsync(PostgresFixture fixture)
    {
        await using var context = fixture.CreateContext();
        var departmentId = await context.Departments.Select(d => d.Id).FirstAsync();
        // users.email is unique (case-insensitive) since A4, so every synthetic user needs its own address.
        var suffix = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = "ai-test-" + suffix,
            Email = $"ai-test-{suffix}@example.invalid",
            FullName = "Synthetic teacher",
            Role = "teacher",
            DepartmentId = departmentId,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A user in the department of the subject used by <see cref="Request"/>, so the worker accepts the job.</summary>
    public static async Task<Guid> CreateSubjectUserAsync(PostgresFixture fixture, string role = "teacher", bool isActive = true)
    {
        await using var context = fixture.CreateContext();
        var departmentId = await context.Subjects
            .Where(s => s.Id == Request().SubjectId)
            .Select(s => s.DepartmentId)
            .SingleAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = "ai-worker-" + suffix,
            Email = $"ai-worker-{suffix}@example.invalid",
            FullName = "Synthetic worker teacher",
            Role = role,
            DepartmentId = role == "admin" ? null : departmentId,
            IsActive = isActive,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Creates a Queued job through the API-side store (reserves one job on a unique far-future day).</summary>
    public static async Task<Guid> CreateQueuedJobAsync(PostgresFixture fixture, Guid userId)
    {
        await using var context = fixture.CreateContext();
        var jobId = await new EfAiJobStore(context).TryCreateQueuedJobAsync(
            userId, Request(), UniqueDate(), jobsLimit: 10, maxAttempts: 3, CancellationToken.None);
        return jobId!.Value;
    }

    public static async Task ExecuteAsync(PostgresFixture fixture, FormattableString sql)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    public static DateOnly UniqueDate() =>
        new DateOnly(2100, 1, 1).AddDays(Random.Shared.Next(0, 36_000));

    public static AiGenerateRequest Request() => new(
        SubjectId: Guid.Parse("20000000-0000-0000-0000-000000000001"),
        TopicId: Guid.Parse("30000000-0000-0000-0000-000000000011"),
        Grade: 11,
        Type: "MultipleChoice",
        Difficulty: "Application",
        Count: 5,
        Note: "focus on kinematics");
}
