using ExamMaker.Application.Auditing;
using ExamMaker.Application.Authentication;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Questions;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Auditing;

namespace ExamMaker.Infrastructure.Tests;

public sealed class AuditLogReaderTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private static readonly Guid DeptA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid DeptB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid SubjectA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SubjectB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid TopicA = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TopicB = Guid.Parse("30000000-0000-0000-0000-000000000026");
    private const string Payload = """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""";

    private readonly Guid headA = Guid.NewGuid();
    private readonly Guid teacherA = Guid.NewGuid();
    private readonly Guid teacherB = Guid.NewGuid();
    private readonly Guid admin = Guid.NewGuid();

    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    /// <summary>Seed synthetic users, entities, and audit logs across two departments.</summary>
    private async Task<(Guid QuestionA, Guid QuestionB, Guid ExamA, Guid DeletedQuestionA, Guid SystemLog)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        db.Users.AddRange(
            new User { Id = headA, CognitoSub = headA.ToString(), Email = "heada@example.invalid", FullName = "Head A", Role = Roles.Head, DepartmentId = DeptA },
            new User { Id = teacherA, CognitoSub = teacherA.ToString(), Email = "teachera@example.invalid", FullName = "Teacher A", Role = Roles.Teacher, DepartmentId = DeptA },
            new User { Id = teacherB, CognitoSub = teacherB.ToString(), Email = "teacherb@example.invalid", FullName = "Teacher B", Role = Roles.Teacher, DepartmentId = DeptB },
            new User { Id = admin, CognitoSub = admin.ToString(), Email = "admin@example.invalid", FullName = "Admin", Role = Roles.Admin, DepartmentId = null });

        var qA = new Question
        {
            Id = Guid.NewGuid(),
            SubjectId = SubjectA,
            TopicId = TopicA,
            Grade = 10,
            DepartmentId = DeptA,
            CreatedBy = teacherA,
            Type = "MultipleChoice",
            Difficulty = "Recognition",
            Stem = "Q A",
            Payload = Payload,
            Status = "Approved"
        };
        var qB = new Question
        {
            Id = Guid.NewGuid(),
            SubjectId = SubjectB,
            TopicId = TopicB,
            Grade = 10,
            DepartmentId = DeptB,
            CreatedBy = teacherB,
            Type = "MultipleChoice",
            Difficulty = "Recognition",
            Stem = "Q B",
            Payload = Payload,
            Status = "Draft"
        };
        var examA = new Exam
        {
            Id = Guid.NewGuid(),
            Title = "Exam A",
            SubjectId = SubjectA,
            Grade = 10,
            DepartmentId = DeptA,
            CreatedBy = teacherA
        };
        db.Questions.AddRange(qA, qB);
        db.Exams.Add(examA);

        var deletedQuestionA = Guid.NewGuid();
        var systemLogId = Guid.NewGuid();
        var t0 = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        db.AuditLogs.AddRange(
            new AuditLog { Id = Guid.NewGuid(), ActorId = teacherA, EntityType = "Question", EntityId = qA.Id, Action = "CreateQuestion", FromStatus = null, ToStatus = "Draft", CreatedAt = t0 },
            new AuditLog { Id = Guid.NewGuid(), ActorId = headA, EntityType = "Question", EntityId = qA.Id, Action = "ApproveQuestion", FromStatus = "Submitted", ToStatus = "Approved", CreatedAt = t0.AddMinutes(5) },
            new AuditLog { Id = Guid.NewGuid(), ActorId = teacherA, EntityType = "Question", EntityId = deletedQuestionA, Action = "DeleteQuestion", FromStatus = "Draft", ToStatus = null, CreatedAt = t0.AddMinutes(10) },
            new AuditLog { Id = Guid.NewGuid(), ActorId = teacherA, EntityType = "Exam", EntityId = examA.Id, Action = "CreateExam", FromStatus = null, ToStatus = null, CreatedAt = t0.AddMinutes(15) },
            new AuditLog { Id = Guid.NewGuid(), ActorId = teacherB, EntityType = "Question", EntityId = qB.Id, Action = "CreateQuestion", FromStatus = null, ToStatus = "Draft", CreatedAt = t0.AddMinutes(20) },
            new AuditLog { Id = systemLogId, ActorId = null, EntityType = "User", EntityId = admin, Action = "CreateUser", FromStatus = null, ToStatus = "Active", Note = "Bootstrap administrator CLI.", CreatedAt = t0.AddMinutes(25) });

        await db.SaveChangesAsync();
        return (qA.Id, qB.Id, examA.Id, deletedQuestionA, systemLogId);
    }

    [PostgresFact]
    public async Task Head_only_sees_department_logs_and_admin_sees_all_including_system_logs()
    {
        var (qA, qB, examA, deletedQuestionA, systemLog) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        var headPage = await reader.ListAsync(headA, new AuditLogQuery(), CancellationToken.None);
        Assert.Equal(4, headPage.TotalItems);
        Assert.DoesNotContain(headPage.Items, x => x.EntityId == qB || x.Id == systemLog);
        Assert.Contains(headPage.Items, x => x.EntityId == deletedQuestionA && x.Action == "DeleteQuestion");
        Assert.Contains(headPage.Items, x => x.EntityId == examA);
        Assert.Contains(headPage.Items, x => x.EntityId == qA);

        var adminPage = await reader.ListAsync(admin, new AuditLogQuery(), CancellationToken.None);
        Assert.Equal(6, adminPage.TotalItems);
        // Verify newest-first ordering and null actor for system task.
        Assert.Equal(systemLog, adminPage.Items[0].Id);
        Assert.Null(adminPage.Items[0].Actor);
    }

    [PostgresFact]
    public async Task Filters_and_utc_time_bounds_work_with_pagination()
    {
        var (qA, _, _, _, _) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        var from = new DateTimeOffset(2026, 11, 20, 10, 5, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 20, 10, 15, 0, TimeSpan.Zero);

        // From is inclusive (10:05), To is exclusive (10:15).
        var bounded = await reader.ListAsync(headA, new AuditLogQuery(From: from, To: to), CancellationToken.None);
        Assert.Equal(2, bounded.TotalItems);
        Assert.All(bounded.Items, x => Assert.True(x.CreatedAt >= from.UtcDateTime && x.CreatedAt < to.UtcDateTime));

        var filtered = await reader.ListAsync(admin, new AuditLogQuery(EntityType: "Question", EntityId: qA, Action: "ApproveQuestion"), CancellationToken.None);
        Assert.Single(filtered.Items);
        Assert.Equal("Head A", filtered.Items[0].Actor?.FullName);

        var paged = await reader.ListAsync(admin, new AuditLogQuery(Page: 2, PageSize: 2), CancellationToken.None);
        Assert.Equal(6, paged.TotalItems);
        Assert.Equal(3, paged.TotalPages);
        Assert.Equal(2, paged.Items.Count);
    }

    [PostgresFact]
    public async Task Teacher_and_invalid_queries_are_rejected()
    {
        await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        var forbidden = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(teacherA, new AuditLogQuery(), CancellationToken.None));
        Assert.Equal(403, forbidden.StatusCode);

        var invalidType = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(admin, new AuditLogQuery(EntityType: "Invalid"), CancellationToken.None));
        Assert.Equal(400, invalidType.StatusCode);

        var badRange = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(admin, new AuditLogQuery(
            From: new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)), CancellationToken.None));
        Assert.Equal(400, badRange.StatusCode);
    }
}
