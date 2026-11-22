using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Auditing;

// A null actor identifies a system task; Note must identify that task.
public class AuditLog
{
    internal AuditLog() { }

    public Guid Id { get; init; }
    public Guid? ActorId { get; init; }
    public User? Actor { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    // Snapshot the target entity's department at the time of the event so moves/deletes never leak or hide history.
    public Guid? DepartmentId { get; init; }
    // Marks the target-department row of a dual-department move so administrators only see the event once.
    public bool IsSecondary { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? FromStatus { get; init; }
    public string? ToStatus { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Create an audit log row with an explicit department snapshot (null only for departmentless system/admin events).</summary>
    public static AuditLog Create(
        Guid? actorId,
        string entityType,
        Guid entityId,
        Guid? departmentId,
        string action,
        string? fromStatus = null,
        string? toStatus = null,
        string? note = null,
        DateTime? createdAt = null,
        bool isSecondary = false,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            ActorId = actorId,
            EntityType = entityType,
            EntityId = entityId,
            DepartmentId = departmentId,
            IsSecondary = isSecondary,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Note = note,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
}
