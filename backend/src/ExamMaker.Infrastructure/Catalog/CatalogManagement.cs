using ExamMaker.Application.Authentication;
using ExamMaker.Application.Catalog;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Catalog;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ExamMaker.Infrastructure.Catalog;

public sealed class CatalogManagement(ExamMakerDbContext database, ILogger<CatalogManagement> logger) : ICatalogManagement
{
    /// <summary>List all departments in stable name order for any authenticated user.</summary>
    public Task<CatalogPage<DepartmentView>> DepartmentsAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        PageAsync(database.Departments.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new DepartmentView(x.Id, x.Name)), query, cancellationToken);

    /// <summary>Filter subjects by department without restricting readers to their own department.</summary>
    public Task<CatalogPage<SubjectView>> SubjectsAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        PageAsync(database.Subjects.AsNoTracking().Where(x => query.DepartmentId == null || x.DepartmentId == query.DepartmentId)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new SubjectView(x.Id, x.Name, x.DepartmentId)), query, cancellationToken);

    /// <summary>Filter chapters by subject and grade, then order them for curriculum display.</summary>
    public Task<CatalogPage<TopicView>> TopicsAsync(CatalogQuery query, CancellationToken cancellationToken) =>
        PageAsync(database.Topics.AsNoTracking().Where(x => (query.SubjectId == null || x.SubjectId == query.SubjectId)
            && (query.Grade == null || x.Grade == query.Grade)).OrderBy(x => x.Grade).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new TopicView(x.Id, x.SubjectId, x.Grade, x.Name, x.SortOrder)), query, cancellationToken);

    /// <summary>Validate pagination and return matching rows with page metadata, including empty pages.</summary>
    private static async Task<CatalogPage<T>> PageAsync<T>(IQueryable<T> source, CatalogQuery query, CancellationToken cancellationToken)
    {
        CatalogInputRules.Query(query);
        var count = await source.CountAsync(cancellationToken);
        var items = await source.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        return new(query.Page, query.PageSize, count, (int)(((long)count + query.PageSize - 1) / query.PageSize), items);
    }

    /// <summary>Create or rename a department and audit its previous and resulting label.</summary>
    public Task<DepartmentView> SaveDepartmentAsync(Guid actorId, Guid? id, DepartmentInput input, CancellationToken cancellationToken)
    {
        var name = CatalogInputRules.Name(input.Name, 200);
        return WriteAsync(actorId, async () =>
        {
            var target = id ?? Guid.NewGuid();
            var before = id is null ? null : await database.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target, cancellationToken)
                ?? throw Missing();
            if (id is null) database.Departments.Add(new Department { Id = target, Name = name });
            else await database.Departments.Where(x => x.Id == target).ExecuteUpdateAsync(x => x.SetProperty(d => d.Name, name), cancellationToken);
            Audit(actorId, "Department", target, target, id is null, before?.Name, name);
            return new DepartmentView(target, name);
        }, cancellationToken);
    }

    /// <summary>Create or move a subject; existing composite foreign keys reject inconsistent moves.</summary>
    public Task<SubjectView> SaveSubjectAsync(Guid actorId, Guid? id, SubjectInput input, CancellationToken cancellationToken)
    {
        var name = CatalogInputRules.Name(input.Name, 200);
        var department = CatalogInputRules.Parent(input.DepartmentId);
        return WriteAsync(actorId, async () =>
        {
            var target = id ?? Guid.NewGuid();
            var before = id is null ? null : await database.Subjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target, cancellationToken)
                ?? throw Missing();
            if (!await database.Departments.AnyAsync(x => x.Id == department, cancellationToken)) throw CatalogInputRules.Invalid();
            if (before is not null && before.DepartmentId != department)
                await EnsureNoActiveJobsAsync("Subject", target, cancellationToken);
            if (id is null) database.Subjects.Add(new Subject { Id = target, Name = name, DepartmentId = department });
            // ExecuteUpdate supports changes to alternate-key columns without EF tracking key mutations.
            else await database.Subjects.Where(x => x.Id == target).ExecuteUpdateAsync(x => x
                .SetProperty(s => s.Name, name).SetProperty(s => s.DepartmentId, department), cancellationToken);
            var beforeNote = before is null ? null : $"{before.Name}; {AuditLog.FormatCatalogDepartment(before.DepartmentId)}";
            var afterNote = $"{name}; {AuditLog.FormatCatalogDepartment(department)}";
            if (before is not null && before.DepartmentId != department)
            {
                Audit(actorId, "Subject", target, before.DepartmentId, false, beforeNote, afterNote);
                Audit(actorId, "Subject", target, department, false, beforeNote, afterNote, secondaryMove: true);
                var childTopics = await database.Topics.AsNoTracking()
                    .Where(t => t.SubjectId == target)
                    .OrderBy(t => t.Grade).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
                    .ToListAsync(cancellationToken);
                foreach (var topic in childTopics)
                {
                    var topicBefore = $"{topic.Name}; {AuditLog.FormatCatalogSubject(target)}; grade={topic.Grade}; order={topic.SortOrder}; {AuditLog.FormatCatalogDepartment(before.DepartmentId)}";
                    var topicAfter = $"{topic.Name}; {AuditLog.FormatCatalogSubject(target)}; grade={topic.Grade}; order={topic.SortOrder}; {AuditLog.FormatCatalogDepartment(department)}";
                    Audit(actorId, "Topic", topic.Id, before.DepartmentId, false, topicBefore, topicAfter);
                    Audit(actorId, "Topic", topic.Id, department, false, topicBefore, topicAfter, secondaryMove: true);
                }
            }
            else
            {
                Audit(actorId, "Subject", target, department, id is null, beforeNote, afterNote);
            }
            return new SubjectView(target, name, department);
        }, cancellationToken);
    }

    /// <summary>Create or update chapter metadata while preserving referenced subject and grade identities.</summary>
    public Task<TopicView> SaveTopicAsync(Guid actorId, Guid? id, TopicInput input, CancellationToken cancellationToken)
    {
        var name = CatalogInputRules.Name(input.Name, 300);
        var subject = CatalogInputRules.Parent(input.SubjectId);
        if (input.Grade is not (10 or 11 or 12) || input.SortOrder is null or < 0) throw CatalogInputRules.Invalid();
        var grade = input.Grade.Value;
        var order = input.SortOrder.Value;
        return WriteAsync(actorId, async () =>
        {
            var target = id ?? Guid.NewGuid();
            var before = id is null ? null : await database.Topics.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target, cancellationToken)
                ?? throw Missing();
            var subjectDepartmentId = await database.Subjects.AsNoTracking()
                .Where(x => x.Id == subject).Select(x => (Guid?)x.DepartmentId).SingleOrDefaultAsync(cancellationToken)
                ?? throw CatalogInputRules.Invalid();
            var beforeDepartmentId = before is null || before.SubjectId == subject
                ? subjectDepartmentId
                : await database.Subjects.AsNoTracking()
                    .Where(x => x.Id == before.SubjectId).Select(x => x.DepartmentId).SingleAsync(cancellationToken);
            if (before is not null && (before.SubjectId != subject || before.Grade != grade))
                await EnsureNoActiveJobsAsync("Topic", target, cancellationToken);
            if (id is null) database.Topics.Add(new Topic { Id = target, Name = name, SubjectId = subject, Grade = grade, SortOrder = order });
            else await database.Topics.Where(x => x.Id == target).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, name)
                .SetProperty(t => t.SubjectId, subject).SetProperty(t => t.Grade, grade).SetProperty(t => t.SortOrder, order), cancellationToken);
            var beforeNote = before is null ? null : $"{before.Name}; {AuditLog.FormatCatalogSubject(before.SubjectId)}; grade={before.Grade}; order={before.SortOrder}; {AuditLog.FormatCatalogDepartment(beforeDepartmentId)}";
            var afterNote = $"{name}; {AuditLog.FormatCatalogSubject(subject)}; grade={grade}; order={order}; {AuditLog.FormatCatalogDepartment(subjectDepartmentId)}";
            if (before is not null && beforeDepartmentId != subjectDepartmentId)
            {
                Audit(actorId, "Topic", target, beforeDepartmentId, false, beforeNote, afterNote);
                Audit(actorId, "Topic", target, subjectDepartmentId, false, beforeNote, afterNote, secondaryMove: true);
            }
            else
            {
                Audit(actorId, "Topic", target, subjectDepartmentId, id is null, beforeNote, afterNote);
            }
            return new TopicView(target, subject, grade, name, order);
        }, cancellationToken);
    }

    /// <summary>Delete only an unreferenced department and keep its audit event after deletion.</summary>
    public Task DeleteDepartmentAsync(Guid actorId, Guid id, CancellationToken cancellationToken) =>
        DeleteAsync(actorId, id, database.Departments.Where(x => x.Id == id), "Department", cancellationToken);

    /// <summary>Delete only a subject with no dependent chapters, questions or exams.</summary>
    public Task DeleteSubjectAsync(Guid actorId, Guid id, CancellationToken cancellationToken) =>
        DeleteAsync(actorId, id, database.Subjects.Where(x => x.Id == id), "Subject", cancellationToken);

    /// <summary>Delete only a chapter that is not referenced by a bank question.</summary>
    public Task DeleteTopicAsync(Guid actorId, Guid id, CancellationToken cancellationToken) =>
        DeleteAsync(actorId, id, database.Topics.Where(x => x.Id == id), "Topic", cancellationToken);

    /// <summary>Perform restricted deletion and its audit atomically; a repeated deletion returns NotFound.</summary>
    private Task DeleteAsync<T>(Guid actorId, Guid id, IQueryable<T> source, string entityType, CancellationToken cancellationToken) where T : class =>
        WriteAsync(actorId, async () =>
        {
            var target = entityType switch
            {
                "Department" => await database.Departments.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { Note = x.Name, DepartmentId = (Guid?)x.Id }).SingleOrDefaultAsync(cancellationToken),
                "Subject" => await database.Subjects.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { Note = x.Name + "; department=" + x.DepartmentId, DepartmentId = (Guid?)x.DepartmentId }).SingleOrDefaultAsync(cancellationToken),
                _ => await database.Topics.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { Note = x.Name + "; subject=" + x.SubjectId + "; grade=" + x.Grade + "; order=" + x.SortOrder, DepartmentId = (Guid?)x.Subject!.DepartmentId }).SingleOrDefaultAsync(cancellationToken)
            };
            if (target is null) throw Missing();
            await EnsureNoActiveJobsAsync(entityType, id, cancellationToken);
            if (await source.ExecuteDeleteAsync(cancellationToken) == 0) throw Missing();
            database.AuditLogs.Add(AuditLog.Create(
                actorId,
                entityType,
                id,
                target.DepartmentId,
                "Delete" + entityType,
                note: $"Deleted: {target.Note}"));
            return true;
        }, cancellationToken);

    /// <summary>Serialize catalog/user administration, recheck authority, and commit the mutation and audit together.</summary>
    private async Task<T> WriteAsync<T>(Guid actorId, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            // Share A4's lock so a role downgrade cannot race the final administrator check.
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683401)", cancellationToken);
            if (!await database.Users.AsNoTracking().AnyAsync(x => x.Id == actorId && x.IsActive && x.Role == Roles.Admin, cancellationToken))
                throw new CatalogException(403, "Forbidden", "Tài khoản không còn quyền quản trị.");
            // Exclude in-flight job creation until the active-job check and catalog change commit together.
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683402)", cancellationToken);
            var result = await operation();
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is PostgresException || exception is DbUpdateException { InnerException: PostgresException })
        {
            var error = (exception as PostgresException ?? exception.InnerException as PostgresException)!;
            // Log diagnostics without raw PostgreSQL messages, which can contain submitted data.
            logger.LogWarning("Catalog write failed: {Type}, SQLSTATE {SqlState}, stack {Stack}", exception.GetType().Name, error.SqlState, exception.StackTrace);
            if (error.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.LockNotAvailable)
                throw new CatalogException(409, "Conflict", "Danh mục đã tồn tại, đang được tham chiếu hoặc thao tác quản trị đang bận.");
            throw new CatalogException(500, "InternalError", "Không thể lưu danh mục. Hãy tải lại dữ liệu trước khi thử lại.");
        }
    }

    /// <summary>Protect JSON catalog references until queued/running jobs have finished generating questions.</summary>
    private async Task EnsureNoActiveJobsAsync(string entityType, Guid id, CancellationToken cancellationToken)
    {
        if (entityType == "Department") return;
        var key = entityType == "Subject" ? "subjectId" : "topicId";
        var busy = await database.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM ai_generation_jobs
                WHERE status IN ('Queued', 'Running')
                AND lower(params ->> {key}) = {id.ToString()}
            ) AS "Value"
            """).SingleAsync(cancellationToken);
        if (busy) throw new CatalogException(409, "Conflict", "Danh mục đang được job AI chờ hoặc đang chạy sử dụng.");
    }

    /// <summary>Record before/after catalog metadata without leaving successful audits for rolled-back changes.</summary>
    private void Audit(Guid actorId, string entityType, Guid id, Guid departmentId, bool created, string? before, string after, bool secondaryMove = false) =>
        database.AuditLogs.Add(AuditLog.Create(
            actorId,
            entityType,
            id,
            departmentId,
            (created ? "Create" : "Update") + entityType,
            note: $"Before: {before ?? "(new)"}; after: {after}",
            isSecondary: secondaryMove));

    /// <summary>Return a safe not-found response for missing update and delete targets.</summary>
    private static CatalogException Missing() => new(404, "NotFound", "Không tìm thấy danh mục.");
}
