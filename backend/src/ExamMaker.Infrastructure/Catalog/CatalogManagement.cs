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
            Audit(actorId, "Department", target, id is null, before?.Name, name);
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
            if (id is null) database.Subjects.Add(new Subject { Id = target, Name = name, DepartmentId = department });
            // ExecuteUpdate supports changes to alternate-key columns without EF tracking key mutations.
            else await database.Subjects.Where(x => x.Id == target).ExecuteUpdateAsync(x => x
                .SetProperty(s => s.Name, name).SetProperty(s => s.DepartmentId, department), cancellationToken);
            Audit(actorId, "Subject", target, id is null, before is null ? null : $"{before.Name}; department={before.DepartmentId}",
                $"{name}; department={department}");
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
            if (!await database.Subjects.AnyAsync(x => x.Id == subject, cancellationToken)) throw CatalogInputRules.Invalid();
            if (id is null) database.Topics.Add(new Topic { Id = target, Name = name, SubjectId = subject, Grade = grade, SortOrder = order });
            else await database.Topics.Where(x => x.Id == target).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, name)
                .SetProperty(t => t.SubjectId, subject).SetProperty(t => t.Grade, grade).SetProperty(t => t.SortOrder, order), cancellationToken);
            Audit(actorId, "Topic", target, id is null, before is null ? null : $"{before.Name}; subject={before.SubjectId}; grade={before.Grade}; order={before.SortOrder}",
                $"{name}; subject={subject}; grade={grade}; order={order}");
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
            if (await source.ExecuteDeleteAsync(cancellationToken) == 0) throw Missing();
            database.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = actorId,
                EntityType = entityType,
                EntityId = id,
                Action = "Delete" + entityType,
                Note = "Deleted an unreferenced catalog entry."
            });
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

    /// <summary>Record before/after catalog metadata without leaving successful audits for rolled-back changes.</summary>
    private void Audit(Guid actorId, string entityType, Guid id, bool created, string? before, string after) =>
        database.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            EntityType = entityType,
            EntityId = id,
            Action = (created ? "Create" : "Update") + entityType,
            Note = $"Before: {before ?? "(new)"}; after: {after}"
        });

    /// <summary>Return a safe not-found response for missing update and delete targets.</summary>
    private static CatalogException Missing() => new(404, "NotFound", "Không tìm thấy danh mục.");
}
