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
        var actor = await database.Users.AsNoTracking()
            .Where(u => u.Id == actorId)
            .Select(u => new { u.Id, u.Role, u.DepartmentId, u.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (actor is null || !actor.IsActive)
            throw new AuditException(401, "Unauthorized", "Tài khoản không hợp lệ hoặc đã bị khóa.");

        if (actor.Role != Roles.Admin && actor.Role != Roles.Head)
            throw new AuditException(403, "Forbidden", "Bạn không có quyền xem nhật ký audit.");

        Guid? headDepartmentId = null;
        if (actor.Role == Roles.Head)
        {
            if (actor.DepartmentId is not Guid deptId)
                throw new AuditException(403, "Forbidden", "Tổ trưởng chưa được gán tổ chuyên môn.");

            headDepartmentId = deptId;
        }

        var (entityType, action, fromUtc, toUtc, offset) = ValidateQuery(query);

        IQueryable<AuditLog> logs = database.AuditLogs.AsNoTracking();

        if (headDepartmentId is Guid scopedDeptId)
        {
            // Only entries explicitly scoped to the head's department are visible;
            // departmentless entries (e.g., admin provisioning or legacy unresolvable rows) remain admin-only.
            logs = logs.Where(log => log.DepartmentId == scopedDeptId);
        }
        else
        {
            // Dual-department move events emit a primary row for the source department and a secondary row
            // (IsSecondary = true) for the target department; administrators see the event once.
            logs = logs.Where(log => !log.IsSecondary);
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
            logs = logs.Where(x => x.CreatedAt <= to);

        var totalItems = await logs.CountAsync(cancellationToken);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);

        var items = await logs
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
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

    /// <summary>Validate pagination, offset overflow, entityType enum, and UTC time bounds before querying the database.</summary>
    private static (string? EntityType, string? Action, DateTime? FromUtc, DateTime? ToUtc, int Offset) ValidateQuery(AuditLogQuery query)
    {
        if (query.Page < 1)
            throw new AuditException(400, "ValidationFailed", "Số trang phải lớn hơn hoặc bằng 1.", "page");

        if (query.PageSize is < 1 or > 100)
            throw new AuditException(400, "ValidationFailed", "Kích thước trang phải nằm trong khoảng 1 đến 100.", "pageSize");

        long rawOffset = (long)(query.Page - 1) * query.PageSize;
        if (rawOffset > int.MaxValue)
            throw new AuditException(400, "ValidationFailed", "Vị trí phân trang vượt quá giới hạn hỗ trợ.", "page");

        string? entityType = string.IsNullOrWhiteSpace(query.EntityType) ? null : query.EntityType.Trim();
        if (entityType is not null && !ValidEntityTypes.Contains(entityType))
            throw new AuditException(400, "ValidationFailed", "Loại đối tượng audit không hợp lệ.", "entityType");

        string? action = string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim();
        DateTime? fromUtc = query.From?.UtcDateTime;
        DateTime? toUtc = query.To?.UtcDateTime;

        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
            throw new AuditException(400, "ValidationFailed", "Thời điểm bắt đầu không được lớn hơn thời điểm kết thúc.", "from");

        return (entityType, action, fromUtc, toUtc, (int)rawOffset);
    }
}