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
        // The API rejects generation for subjects outside the requester's department (B8), so synthetic
        // requesters belong to the department of the subject used by Request().
        var departmentId = await context.Subjects
            .Where(s => s.Id == Request().SubjectId)
            .Select(s => s.DepartmentId)
            .SingleAsync();
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

    /// <summary>A teacher in a freshly created department that owns none of the seeded subjects.</summary>
    public static async Task<Guid> CreateUserInOtherDepartmentAsync(PostgresFixture fixture)
    {
        await using var context = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N");
        var department = new ExamMaker.Domain.Catalog.Department { Id = Guid.NewGuid(), Name = "AI test dept " + suffix };
        context.Departments.Add(department);
        var user = new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = "ai-other-" + suffix,
            Email = $"ai-other-{suffix}@example.invalid",
            FullName = "Synthetic other-department teacher",
            Role = "teacher",
            DepartmentId = department.Id,
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

    /// <summary>Model output with <paramref name="count"/> valid MultipleChoice questions, unique per call (no duplicates).</summary>
    public static string ModelOutput(int count)
    {
        var tag = Guid.NewGuid().ToString("N");
        return "{\"questions\":[" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            $$"""{"stem":"Stem {{i}} {{tag}}","payload":{"options":[{"label":"A","text":"a{{i}}"},{"label":"B","text":"b{{i}}"},{"label":"C","text":"c{{i}}"},{"label":"D","text":"d{{i}}"}],"correctLabel":"B"},"explanation":null}""")) + "]}";
    }

    public static AiGenerateRequest Request() => new(
        SubjectId: Guid.Parse("20000000-0000-0000-0000-000000000001"),
        TopicId: Guid.Parse("30000000-0000-0000-0000-000000000011"),
        Grade: 11,
        Type: "MultipleChoice",
        Difficulty: "Application",
        Count: 5,
        Note: "focus on kinematics");
}
