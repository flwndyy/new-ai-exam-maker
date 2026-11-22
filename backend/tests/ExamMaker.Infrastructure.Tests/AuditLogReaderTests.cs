using ExamMaker.Application.Auditing;
using ExamMaker.Application.Authentication;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Questions;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Auditing;
using ExamMaker.Infrastructure.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly Guid headB = Guid.NewGuid();
    private readonly Guid teacherA = Guid.NewGuid();
    private readonly Guid teacherB = Guid.NewGuid();
    private readonly Guid admin = Guid.NewGuid();

    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    /// <summary>Seed synthetic users, entities, and audit logs across two departments.</summary>
    private async Task<(Guid QuestionA, Guid QuestionB, Guid ExamA, Guid DeletedQuestionA, Guid DeletedSubjectA, Guid SystemLog)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        db.Users.AddRange(
            new User { Id = headA, CognitoSub = headA.ToString(), Email = "heada@example.invalid", FullName = "Head A", Role = Roles.Head, DepartmentId = DeptA },
            new User { Id = headB, CognitoSub = headB.ToString(), Email = "headb@example.invalid", FullName = "Head B", Role = Roles.Head, DepartmentId = DeptB },
            // Simulate teacherA having later moved to DeptB after acting on DeptA entities.
            new User { Id = teacherA, CognitoSub = teacherA.ToString(), Email = "teachera@example.invalid", FullName = "Teacher A", Role = Roles.Teacher, DepartmentId = DeptB },
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
        var deletedSubjectA = Guid.NewGuid();
        var systemLogId = Guid.NewGuid();
        var t0 = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        db.AuditLogs.AddRange(
            AuditLog.Create(teacherA, "Question", qA.Id, DeptA, "CreateQuestion", null, "Draft", createdAt: t0),
            AuditLog.Create(headA, "Question", qA.Id, DeptA, "ApproveQuestion", "Submitted", "Approved", createdAt: t0.AddMinutes(5)),
            AuditLog.Create(teacherA, "Question", deletedQuestionA, DeptA, "CreateQuestion", null, "Draft", createdAt: t0.AddMinutes(8)),
            AuditLog.Create(teacherA, "Question", deletedQuestionA, DeptA, "DeleteQuestion", "Draft", null, createdAt: t0.AddMinutes(10)),
            AuditLog.Create(admin, "Subject", deletedSubjectA, DeptA, "DeleteSubject", null, null, "Deleted: Old subject", t0.AddMinutes(12)),
            AuditLog.Create(teacherA, "Exam", examA.Id, DeptA, "CreateExam", null, null, createdAt: t0.AddMinutes(15)),
            AuditLog.Create(teacherB, "Question", qB.Id, DeptB, "CreateQuestion", null, "Draft", createdAt: t0.AddMinutes(20)),
            AuditLog.Create(null, "User", admin, null, "CreateUser", null, "Active", "Bootstrap administrator CLI.", t0.AddMinutes(25), id: systemLogId));

        await db.SaveChangesAsync();
        return (qA.Id, qB.Id, examA.Id, deletedQuestionA, deletedSubjectA, systemLogId);
    }

    [PostgresFact]
    public async Task Head_only_sees_department_logs_even_after_actor_moves_or_entity_is_deleted()
    {
        var (qA, qB, examA, deletedQuestionA, deletedSubjectA, systemLog) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        // Head A still sees Create/Delete history of deleted entities and actions by moved teacherA.
        var headAPage = await reader.ListAsync(headA, new AuditLogQuery(), CancellationToken.None);
        Assert.Equal(6, headAPage.TotalItems);
        Assert.DoesNotContain(headAPage.Items, x => x.EntityId == qB || x.Id == systemLog);
        Assert.Equal(2, headAPage.Items.Count(x => x.EntityId == deletedQuestionA));
        Assert.Contains(headAPage.Items, x => x.EntityId == deletedSubjectA && x.Action == "DeleteSubject");
        Assert.Contains(headAPage.Items, x => x.EntityId == examA);
        Assert.Contains(headAPage.Items, x => x.EntityId == qA);

        // Head B only sees DeptB logs and never sees DeptA logs even though teacherA is now in DeptB.
        var headBPage = await reader.ListAsync(headB, new AuditLogQuery(), CancellationToken.None);
        Assert.Single(headBPage.Items);
        Assert.Equal(qB, headBPage.Items[0].EntityId);

        var adminPage = await reader.ListAsync(admin, new AuditLogQuery(), CancellationToken.None);
        Assert.Equal(8, adminPage.TotalItems);
        // Verify newest-first ordering and null actor for system task.
        Assert.Equal(systemLog, adminPage.Items[0].Id);
        Assert.Null(adminPage.Items[0].Actor);
    }

    [PostgresFact]
    public async Task Filters_and_utc_time_bounds_work_with_pagination()
    {
        var (qA, _, _, _, _, _) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        var from = new DateTimeOffset(2026, 11, 20, 10, 5, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 20, 10, 12, 0, TimeSpan.Zero);

        // Both From (10:05) and To (10:12) are inclusive: 10:05, 10:08, 10:10, 10:12.
        var bounded = await reader.ListAsync(headA, new AuditLogQuery(From: from, To: to), CancellationToken.None);
        Assert.Equal(4, bounded.TotalItems);
        Assert.All(bounded.Items, x => Assert.True(x.CreatedAt >= from.UtcDateTime && x.CreatedAt <= to.UtcDateTime));

        var equalBounds = await reader.ListAsync(headA, new AuditLogQuery(From: from, To: from), CancellationToken.None);
        Assert.Single(equalBounds.Items);

        var filtered = await reader.ListAsync(admin, new AuditLogQuery(EntityType: "Question", EntityId: qA, Action: "ApproveQuestion"), CancellationToken.None);
        Assert.Single(filtered.Items);
        Assert.Equal("Head A", filtered.Items[0].Actor?.FullName);

        var paged = await reader.ListAsync(admin, new AuditLogQuery(Page: 2, PageSize: 2), CancellationToken.None);
        Assert.Equal(8, paged.TotalItems);
        Assert.Equal(4, paged.TotalPages);
        Assert.Equal(2, paged.Items.Count);
    }

    [PostgresFact]
    public async Task Teacher_and_invalid_queries_are_rejected()
    {
        await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        var forbidden = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(teacherA, new AuditLogQuery(Page: 0, EntityType: "Invalid"), CancellationToken.None));
        Assert.Equal(403, forbidden.StatusCode);

        var invalidType = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(admin, new AuditLogQuery(EntityType: "Invalid"), CancellationToken.None));
        Assert.Equal(400, invalidType.StatusCode);
        Assert.Equal("ValidationFailed", invalidType.Code);

        var overflow = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(admin, new AuditLogQuery(Page: int.MaxValue, PageSize: 20), CancellationToken.None));
        Assert.Equal(400, overflow.StatusCode);
        Assert.Equal("ValidationFailed", overflow.Code);

        var badRange = await Assert.ThrowsAsync<AuditException>(() => reader.ListAsync(admin, new AuditLogQuery(
            From: new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)), CancellationToken.None));
        Assert.Equal(400, badRange.StatusCode);
        Assert.Equal("ValidationFailed", badRange.Code);
    }

    [PostgresFact]
    public async Task Departmentless_admin_logs_stay_hidden_from_head_even_if_user_later_moves_to_department()
    {
        await SeedAsync();
        await using var db = fixture.CreateContext();
        var movedAdmin = new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = "moved-admin",
            Email = "movedadmin@example.invalid",
            FullName = "Moved Admin",
            Role = Roles.Admin,
            DepartmentId = null
        };
        var adminProvisionLogId = Guid.NewGuid();
        db.Users.Add(movedAdmin);
        db.AuditLogs.Add(AuditLog.Create(
            admin,
            "User",
            movedAdmin.Id,
            null,
            "CreateUser",
            null,
            "Active",
            "Administrator provisioned an invited account.",
            DateTime.UtcNow,
            id: adminProvisionLogId));
        await db.SaveChangesAsync();

        // Simulate demoting and assigning the former administrator to DeptB.
        movedAdmin.Role = Roles.Teacher;
        movedAdmin.DepartmentId = DeptB;
        await db.SaveChangesAsync();

        var reader = new AuditLogReader(db);
        var headBPage = await reader.ListAsync(headB, new AuditLogQuery(), CancellationToken.None);
        Assert.DoesNotContain(headBPage.Items, x => x.Id == adminProvisionLogId);

        var adminPage = await reader.ListAsync(admin, new AuditLogQuery(EntityId: movedAdmin.Id), CancellationToken.None);
        Assert.Contains(adminPage.Items, x => x.Id == adminProvisionLogId);
    }

    [PostgresFact]
    public async Task Migration_backfills_existing_logs_so_head_retains_history_after_post_upgrade_delete()
    {
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261120071750_AddUserAdministration");

        var localHeadA = Guid.NewGuid();
        var localTeacherA = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var createLogId = Guid.NewGuid();
        var updateLogId = Guid.NewGuid();
        var t0 = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (id, cognito_sub, email, full_name, role, department_id, is_active, created_at)
            VALUES
                ({localHeadA}, {"sub-head-" + localHeadA}, {"head-" + localHeadA + "@example.invalid"}, {"Local Head A"}, {Roles.Head}, {DeptA}, true, {t0}),
                ({localTeacherA}, {"sub-teacher-" + localTeacherA}, {"teacher-" + localTeacherA + "@example.invalid"}, {"Local Teacher A"}, {Roles.Teacher}, {DeptA}, true, {t0});

            INSERT INTO questions (id, subject_id, topic_id, grade, department_id, created_by, type, difficulty, stem, payload, status, revision, source, ai_verification, ai_reviewed_by_author, created_at, updated_at)
            VALUES ({questionId}, {SubjectA}, {TopicA}, 10, {DeptA}, {localTeacherA}, 'MultipleChoice', 'Recognition', 'Pre-migration question', {Payload}::jsonb, 'Draft', 1, 'Manual', 'NotChecked', false, {t0}, {t0});

            INSERT INTO audit_logs (id, actor_id, entity_type, entity_id, action, from_status, to_status, note, created_at)
            VALUES
                ({createLogId}, {localTeacherA}, 'Question', {questionId}, 'CreateQuestion', NULL, 'Draft', 'Manual question; revision=1', {t0}),
                ({updateLogId}, {localTeacherA}, 'Question', {questionId}, 'UpdateQuestion', 'Draft', 'Draft', 'revision=1->1', {t0.AddMinutes(1)});
            """);

        // Upgrade to latest migration (AddAuditLogDepartmentId) which must backfill department_id.
        await db.Database.MigrateAsync();

        // Delete the question after upgrade; Create/Update history must remain visible to Head A.
        var questions = new QuestionManagement(db, NullLogger<QuestionManagement>.Instance);
        await questions.DeleteAsync(localTeacherA, questionId, CancellationToken.None);

        var reader = new AuditLogReader(db);
        var headPage = await reader.ListAsync(localHeadA, new AuditLogQuery(EntityId: questionId), CancellationToken.None);
        Assert.Equal(3, headPage.TotalItems);
        Assert.Contains(headPage.Items, x => x.Id == createLogId && x.Action == "CreateQuestion");
        Assert.Contains(headPage.Items, x => x.Id == updateLogId && x.Action == "UpdateQuestion");
        Assert.Contains(headPage.Items, x => x.Action == "DeleteQuestion");
    }

    [PostgresFact]
    public async Task Migration_leaves_mutable_department_entity_logs_null_so_prior_department_history_is_never_leaked()
    {
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261120071750_AddUserAdministration");

        var localAdmin = Guid.NewGuid();
        var localHeadB = Guid.NewGuid();
        var movedUser = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var userLogId = Guid.NewGuid();
        var adminLogId = Guid.NewGuid();
        var subjectLogId = Guid.NewGuid();
        var topicLogId = Guid.NewGuid();
        var t0 = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (id, cognito_sub, email, full_name, role, department_id, is_active, created_at)
            VALUES
                ({localAdmin}, {"sub-admin-" + localAdmin}, {"admin-" + localAdmin + "@example.invalid"}, {"Local Admin"}, {Roles.Admin}, NULL, true, {t0}),
                ({localHeadB}, {"sub-headb-" + localHeadB}, {"headb-" + localHeadB + "@example.invalid"}, {"Local Head B"}, {Roles.Head}, {DeptB}, true, {t0}),
                ({movedUser}, {"sub-moved-" + movedUser}, {"moved-" + movedUser + "@example.invalid"}, {"Moved User"}, {Roles.Teacher}, {DeptB}, true, {t0});

            INSERT INTO subjects (id, name, department_id)
            VALUES ({subjectId}, {"Subject " + subjectId}, {DeptB});

            INSERT INTO topics (id, subject_id, grade, name, sort_order)
            VALUES ({topicId}, {subjectId}, 10, {"Topic " + topicId}, 1);

            INSERT INTO audit_logs (id, actor_id, entity_type, entity_id, action, from_status, to_status, note, created_at)
            VALUES
                ({userLogId}, {localAdmin}, 'User', {movedUser}, 'CreateUser', NULL, 'Active', 'Administrator provisioned an invited account.', {t0}),
                ({adminLogId}, {localAdmin}, 'User', {localAdmin}, 'CreateUser', NULL, 'Active', 'Bootstrap administrator CLI.', {t0}),
                ({subjectLogId}, {localAdmin}, 'Subject', {subjectId}, 'CreateSubject', NULL, NULL, 'Before: (new); after: Subject', {t0}),
                ({topicLogId}, {localAdmin}, 'Topic', {topicId}, 'CreateTopic', NULL, NULL, 'Before: (new); after: Topic', {t0.AddMinutes(1)});
            """);

        await db.Database.MigrateAsync();

        var reader = new AuditLogReader(db);
        // Because User, Subject, and Topic can move across departments over time, legacy pre-migration rows stay NULL (admin-only).
        var headBPage = await reader.ListAsync(localHeadB, new AuditLogQuery(), CancellationToken.None);
        Assert.DoesNotContain(headBPage.Items, x => x.Id == userLogId);
        Assert.DoesNotContain(headBPage.Items, x => x.Id == adminLogId);
        Assert.DoesNotContain(headBPage.Items, x => x.Id == subjectLogId);
        Assert.DoesNotContain(headBPage.Items, x => x.Id == topicLogId);

        var adminPage = await reader.ListAsync(localAdmin, new AuditLogQuery(), CancellationToken.None);
        Assert.Contains(adminPage.Items, x => x.Id == userLogId);
        Assert.Contains(adminPage.Items, x => x.Id == adminLogId);
        Assert.Contains(adminPage.Items, x => x.Id == subjectLogId);
        Assert.Contains(adminPage.Items, x => x.Id == topicLogId);
    }

    [PostgresFact]
    public async Task Migration_leaves_pre_deleted_entity_logs_null_instead_of_assigning_moved_actor_department()
    {
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261120071750_AddUserAdministration");

        var localAdmin = Guid.NewGuid();
        var localHeadA = Guid.NewGuid();
        var localHeadB = Guid.NewGuid();
        var movedTeacher = Guid.NewGuid();
        var preDeletedQuestionId = Guid.NewGuid();
        var createLogId = Guid.NewGuid();
        var deleteLogId = Guid.NewGuid();
        var t0 = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (id, cognito_sub, email, full_name, role, department_id, is_active, created_at)
            VALUES
                ({localAdmin}, {"sub-admin-" + localAdmin}, {"admin-" + localAdmin + "@example.invalid"}, {"Local Admin"}, {Roles.Admin}, NULL, true, {t0}),
                ({localHeadA}, {"sub-heada-" + localHeadA}, {"heada-" + localHeadA + "@example.invalid"}, {"Local Head A"}, {Roles.Head}, {DeptA}, true, {t0}),
                ({localHeadB}, {"sub-headb-" + localHeadB}, {"headb-" + localHeadB + "@example.invalid"}, {"Local Head B"}, {Roles.Head}, {DeptB}, true, {t0}),
                ({movedTeacher}, {"sub-teacher-" + movedTeacher}, {"teacher-" + movedTeacher + "@example.invalid"}, {"Moved Teacher"}, {Roles.Teacher}, {DeptB}, true, {t0});

            INSERT INTO audit_logs (id, actor_id, entity_type, entity_id, action, from_status, to_status, note, created_at)
            VALUES
                ({createLogId}, {movedTeacher}, 'Question', {preDeletedQuestionId}, 'CreateQuestion', NULL, 'Draft', 'Manual question; revision=1', {t0}),
                ({deleteLogId}, {movedTeacher}, 'Question', {preDeletedQuestionId}, 'DeleteQuestion', 'Draft', NULL, 'revision=1', {t0.AddMinutes(5)});
            """);

        await db.Database.MigrateAsync();

        var reader = new AuditLogReader(db);

        // Head B must NOT see preDeletedQuestionId even though movedTeacher is currently in DeptB.
        var headBPage = await reader.ListAsync(localHeadB, new AuditLogQuery(EntityId: preDeletedQuestionId), CancellationToken.None);
        Assert.Equal(0, headBPage.TotalItems);

        // Admin sees the unresolvable pre-deleted entity logs.
        var adminPage = await reader.ListAsync(localAdmin, new AuditLogQuery(EntityId: preDeletedQuestionId), CancellationToken.None);
        Assert.Equal(2, adminPage.TotalItems);
    }

    [PostgresFact]
    public async Task Dual_department_moves_are_visible_to_both_heads_without_duplicating_for_admin()
    {
        await SeedAsync();
        await using var db = fixture.CreateContext();
        var reader = new AuditLogReader(db);

        // Move subject (with a child topic) from DeptA to DeptB via CatalogManagement.
        var catalog = new ExamMaker.Infrastructure.Catalog.CatalogManagement(db, NullLogger<ExamMaker.Infrastructure.Catalog.CatalogManagement>.Instance);
        var subject = await catalog.SaveSubjectAsync(admin, null, new("Movable Subject", DeptA), CancellationToken.None);
        var childTopic = await catalog.SaveTopicAsync(admin, null, new(subject.Id, 10, "Child Topic", 1), CancellationToken.None);
        await catalog.SaveSubjectAsync(admin, subject.Id, new("Movable Subject", DeptB), CancellationToken.None);

        var headASubjectLogs = await reader.ListAsync(headA, new AuditLogQuery(EntityType: "Subject", EntityId: subject.Id, Action: "UpdateSubject"), CancellationToken.None);
        var headBSubjectLogs = await reader.ListAsync(headB, new AuditLogQuery(EntityType: "Subject", EntityId: subject.Id, Action: "UpdateSubject"), CancellationToken.None);
        var adminSubjectLogs = await reader.ListAsync(admin, new AuditLogQuery(EntityType: "Subject", EntityId: subject.Id, Action: "UpdateSubject"), CancellationToken.None);

        Assert.Equal(1, headASubjectLogs.TotalItems);
        Assert.Equal(1, headBSubjectLogs.TotalItems);
        Assert.Equal(1, adminSubjectLogs.TotalItems);
        // Head A sees DeptA but DeptB is redacted to "other"; Head B sees DeptB but DeptA is redacted to "other"; Admin sees both GUIDs.
        Assert.Contains($"department={DeptA}", headASubjectLogs.Items[0].Note);
        Assert.DoesNotContain(DeptB.ToString(), headASubjectLogs.Items[0].Note);
        Assert.Contains("department=other", headASubjectLogs.Items[0].Note);
        Assert.Contains($"department={DeptB}", headBSubjectLogs.Items[0].Note);
        Assert.DoesNotContain(DeptA.ToString(), headBSubjectLogs.Items[0].Note);
        Assert.Contains("department=other", headBSubjectLogs.Items[0].Note);
        Assert.Contains($"department={DeptA}", adminSubjectLogs.Items[0].Note);
        Assert.Contains($"department={DeptB}", adminSubjectLogs.Items[0].Note);

        // Child topic also receives a department-move audit visible to both heads and once to admin.
        var headATopicMoveLogs = await reader.ListAsync(headA, new AuditLogQuery(EntityType: "Topic", EntityId: childTopic.Id, Action: "UpdateTopic"), CancellationToken.None);
        var headBTopicMoveLogs = await reader.ListAsync(headB, new AuditLogQuery(EntityType: "Topic", EntityId: childTopic.Id, Action: "UpdateTopic"), CancellationToken.None);
        var adminTopicMoveLogs = await reader.ListAsync(admin, new AuditLogQuery(EntityType: "Topic", EntityId: childTopic.Id, Action: "UpdateTopic"), CancellationToken.None);
        Assert.Equal(1, headATopicMoveLogs.TotalItems);
        Assert.Equal(1, headBTopicMoveLogs.TotalItems);
        Assert.Equal(1, adminTopicMoveLogs.TotalItems);

        // Moving a topic directly from a DeptB subject to a DeptA subject redacts the foreign subject and department GUIDs for each head.
        var subjectInA = await catalog.SaveSubjectAsync(admin, null, new("Target Subject A", DeptA), CancellationToken.None);
        await catalog.SaveTopicAsync(admin, childTopic.Id, new(subjectInA.Id, 10, "Child Topic", 1), CancellationToken.None);
        var headADirectTopicLogs = await reader.ListAsync(headA, new AuditLogQuery(EntityType: "Topic", EntityId: childTopic.Id, Action: "UpdateTopic"), CancellationToken.None);
        var headBDirectTopicLogs = await reader.ListAsync(headB, new AuditLogQuery(EntityType: "Topic", EntityId: childTopic.Id, Action: "UpdateTopic"), CancellationToken.None);
        Assert.DoesNotContain(subject.Id.ToString(), headADirectTopicLogs.Items[0].Note);
        Assert.DoesNotContain(DeptB.ToString(), headADirectTopicLogs.Items[0].Note);
        Assert.Contains($"subject={subjectInA.Id}", headADirectTopicLogs.Items[0].Note);
        Assert.DoesNotContain(subjectInA.Id.ToString(), headBDirectTopicLogs.Items[0].Note);
        Assert.DoesNotContain(DeptA.ToString(), headBDirectTopicLogs.Items[0].Note);
        Assert.Contains($"subject={subject.Id}", headBDirectTopicLogs.Items[0].Note);

        // User-entered notes (such as ReviewNote ending with "[move-target]") are never hidden or truncated.
        var trickyLog = AuditLog.Create(headA, "ExamVersion", Guid.NewGuid(), DeptA, "RejectExamVersion", "Submitted", "Rejected", "Sai đáp án [move-target]");
        db.AuditLogs.Add(trickyLog);
        await db.SaveChangesAsync();
        var adminRejectLogs = await reader.ListAsync(admin, new AuditLogQuery(EntityId: trickyLog.EntityId), CancellationToken.None);
        var headARejectLogs = await reader.ListAsync(headA, new AuditLogQuery(EntityId: trickyLog.EntityId), CancellationToken.None);
        Assert.Equal(1, adminRejectLogs.TotalItems);
        Assert.Equal("Sai đáp án [move-target]", adminRejectLogs.Items[0].Note);
        Assert.Equal("Sai đáp án [move-target]", headARejectLogs.Items[0].Note);

        // User move DeptA -> DeptB -> NULL (admin) -> DeptA via UserManagementService.
        var cognito = new FakeCognitoUserAdmin();
        cognito.Accounts[admin.ToString()] = new(admin.ToString(), "admin@example.invalid", "Admin", true, true, [Roles.Admin]);
        var users = new ExamMaker.Infrastructure.Users.UserManagementService(db, cognito, NullLogger<ExamMaker.Infrastructure.Users.UserManagementService>.Instance);
        var createdUser = await users.CreateAsync(admin, new("movable@example.invalid", "Movable", Roles.Teacher, DeptA), CancellationToken.None);

        // Move DeptA -> DeptB: 2 rows in DB, 1 visible to Head A, 1 to Head B, 1 to Admin, with other department GUID redacted for heads.
        await users.UpdateAsync(admin, createdUser.Id, new("movable@example.invalid", "Movable", Roles.Teacher, DeptB, true), CancellationToken.None);
        var headAUserLogs = await reader.ListAsync(headA, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None);
        var headBUserLogs = await reader.ListAsync(headB, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None);
        var adminUserLogs = await reader.ListAsync(admin, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None);
        Assert.Equal(1, headAUserLogs.TotalItems);
        Assert.Equal(1, headBUserLogs.TotalItems);
        Assert.Equal(1, adminUserLogs.TotalItems);
        Assert.Contains($"department {DeptA}->other", headAUserLogs.Items[0].Note);
        Assert.DoesNotContain(DeptB.ToString(), headAUserLogs.Items[0].Note);
        Assert.Contains($"department other->{DeptB}", headBUserLogs.Items[0].Note);
        Assert.DoesNotContain(DeptA.ToString(), headBUserLogs.Items[0].Note);
        Assert.Contains($"department {DeptA}->{DeptB}", adminUserLogs.Items[0].Note);

        // Move DeptB -> NULL (promote to admin): only 1 row written (for DeptB), no NULL duplicate.
        await users.UpdateAsync(admin, createdUser.Id, new("movable@example.invalid", "Movable", Roles.Admin, null, true), CancellationToken.None);
        Assert.Equal(3, await db.AuditLogs.CountAsync(x => x.EntityId == createdUser.Id && x.Action == "UpdateUser"));
        Assert.Equal(2, (await reader.ListAsync(headB, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None)).TotalItems);
        Assert.Equal(2, (await reader.ListAsync(admin, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None)).TotalItems);

        // Move NULL -> DeptA (demote back to DeptA): 1 row written for DeptA.
        await users.UpdateAsync(admin, createdUser.Id, new("movable@example.invalid", "Movable", Roles.Teacher, DeptA, true), CancellationToken.None);
        Assert.Equal(4, await db.AuditLogs.CountAsync(x => x.EntityId == createdUser.Id && x.Action == "UpdateUser"));
        Assert.Equal(2, (await reader.ListAsync(headA, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None)).TotalItems);
        Assert.Equal(3, (await reader.ListAsync(admin, new AuditLogQuery(EntityId: createdUser.Id, Action: "UpdateUser"), CancellationToken.None)).TotalItems);
    }
}
