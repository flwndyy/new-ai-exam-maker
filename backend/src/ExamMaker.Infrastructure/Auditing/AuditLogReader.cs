using ExamMaker.Application.Auditing;
using ExamMaker.Application.Authentication;
using ExamMaker.Domain.Auditing;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Auditing;

public sealed class AuditLogReader(ExamMakerDbContext database) : IAuditLogReader
{
    private static readonly HashSet<string> ValidEntityTypes =
    [
        "Question", "Exam", "ExamVersion", "QuestionSet",
        "User", "AiJob", "Department", "Subject", "Topic"
    ];

    /// <summary>Return paginated audit logs ordered newest first, scoped to the head's department.</summary>
    public async Task<AuditLogPage> ListAsync(Guid actorId, AuditLogQuery query, CancellationToken cancellationToken)
    {
        var (entityType, action, fromUtc, toUtc) = ValidateQuery(query);

        var actor = await database.Users.AsNoTracking()
            .Where(u => u.Id == actorId)
            .Select(u => new { u.Id, u.Role, u.DepartmentId, u.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (actor is null || !actor.IsActive)
            throw new AuditException(401, "Unauthorized", "Tài khoản không hợp lệ hoặc đã bị khóa.");

        if (actor.Role != Roles.Admin && actor.Role != Roles.Head)
            throw new AuditException(403, "Forbidden", "Bạn không có quyền xem nhật ký audit.");

        IQueryable<AuditLog> logs = database.AuditLogs.AsNoTracking();

        if (actor.Role == Roles.Head)
        {
            if (actor.DepartmentId is not Guid deptId)
                throw new AuditException(403, "Forbidden", "Trưởng bộ môn chưa được gán tổ chuyên môn.");

            logs = FilterByDepartment(logs, deptId);
        }

        if (entityType is not null)
            logs = logs.Where(x => x.EntityType == entityType);

        if (query.EntityId is Guid entityId)
            logs = logs.Where(x => x.EntityId == entityId);

        if (query.ActorId is Guid filterActorId)
            logs = logs.Where(x => x.ActorId == filterActorId);

        if (action is not null)
            logs = logs.Where(x => x.Action == action);

        if (fromUtc is DateTime from)
            logs = logs.Where(x => x.CreatedAt >= from);

        if (toUtc is DateTime to)
            logs = logs.Where(x => x.CreatedAt < to);

        var totalItems = await logs.CountAsync(cancellationToken);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);

        var items = await logs
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new AuditLogView(
                x.Id,
                x.Actor == null ? null : new AuditLogActor(x.Actor.Id, x.Actor.FullName),
                x.EntityType,
                x.EntityId,
                x.Action,
                x.FromStatus,
                x.ToStatus,
                x.Note,
                x.CreatedAt))
            .ToListAsync(cancellationToken);

        return new AuditLogPage(query.Page, query.PageSize, totalItems, totalPages, items);
    }

    /// <summary>Restrict audit logs to entities belonging to the head's department (or deleted questions/sets acted on by department members).</summary>
    private IQueryable<AuditLog> FilterByDepartment(IQueryable<AuditLog> logs, Guid deptId) =>
        logs.Where(log =>
            (log.EntityType == "Question" && (
                database.Questions.Any(q => q.Id == log.EntityId && q.DepartmentId == deptId)
                || (log.Action == "DeleteQuestion" && log.Actor != null && log.Actor.DepartmentId == deptId)))
            || (log.EntityType == "Exam" && database.Exams.Any(e => e.Id == log.EntityId && e.DepartmentId == deptId))
            || (log.EntityType == "ExamVersion" && database.ExamVersions.Any(v => v.Id == log.EntityId && v.Exam!.DepartmentId == deptId))
            || (log.EntityType == "QuestionSet" && (
                database.QuestionSetItems.Any(i => i.QuestionSetId == log.EntityId && i.Question!.DepartmentId == deptId)
                || (log.Actor != null && log.Actor.DepartmentId == deptId)))
            || (log.EntityType == "AiJob" && database.AiGenerationJobs.Any(j =>
                j.Id == log.EntityId && database.Users.Any(u => u.Id == j.RequestedBy && u.DepartmentId == deptId)))
            || (log.EntityType == "User" && database.Users.Any(u => u.Id == log.EntityId && u.DepartmentId == deptId))
            || (log.EntityType == "Department" && log.EntityId == deptId)
            || (log.EntityType == "Subject" && database.Subjects.Any(s => s.Id == log.EntityId && s.DepartmentId == deptId))
            || (log.EntityType == "Topic" && database.Topics.Any(t => t.Id == log.EntityId && t.Subject!.DepartmentId == deptId)));

    /// <summary>Validate pagination, entityType enum, and UTC time bounds before querying the database.</summary>
    private static (string? EntityType, string? Action, DateTime? FromUtc, DateTime? ToUtc) ValidateQuery(AuditLogQuery query)
    {
        if (query.Page < 1)
            throw new AuditException(400, "ValidationError", "Số trang phải lớn hơn hoặc bằng 1.", "page");

        if (query.PageSize is < 1 or > 100)
            throw new AuditException(400, "ValidationError", "Kích thước trang phải nằm trong khoảng 1 đến 100.", "pageSize");

        string? entityType = string.IsNullOrWhiteSpace(query.EntityType) ? null : query.EntityType.Trim();
        if (entityType is not null && !ValidEntityTypes.Contains(entityType))
            throw new AuditException(400, "ValidationError", "Loại đối tượng audit không hợp lệ.", "entityType");

        string? action = string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim();
        DateTime? fromUtc = query.From?.UtcDateTime;
        DateTime? toUtc = query.To?.UtcDateTime;

        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            throw new AuditException(400, "ValidationError", "Thời điểm bắt đầu phải nhỏ hơn thời điểm kết thúc.", "from");

        return (entityType, action, fromUtc, toUtc);
    }
}