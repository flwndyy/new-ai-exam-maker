using ExamMaker.Application.Ai;
using ExamMaker.Domain.Users;
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

    public static DateOnly UniqueDate() =>
        new DateOnly(2100, 1, 1).AddDays(Random.Shared.Next(0, 36_000));

    public static AiGenerateRequest Request() => new(
        SubjectId: Guid.NewGuid(),
        TopicId: Guid.NewGuid(),
        Grade: 11,
        Type: "MultipleChoice",
        Difficulty: "Application",
        Count: 5,
        Note: "focus on kinematics");
}
