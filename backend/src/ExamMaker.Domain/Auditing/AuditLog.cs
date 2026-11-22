using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Auditing;

// A null actor identifies a system task; Note must identify that task.
public class AuditLog
{
    public const string SecondaryMoveSuffix = " [move-target]";

    public Guid Id { get; set; }
    public Guid? ActorId { get; set; }
    public User? Actor { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    // Snapshot the target entity's department at the time of the event so moves/deletes never leak or hide history.
    public Guid? DepartmentId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

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
        DateTime? createdAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            EntityType = entityType,
            EntityId = entityId,
            DepartmentId = departmentId,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Note = note,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

    /// <summary>Strip the internal secondary-move marker from the note returned to API callers.</summary>
    public static string? CleanNote(string? note) =>
        note is not null && note.EndsWith(SecondaryMoveSuffix, StringComparison.Ordinal)
            ? note[..^SecondaryMoveSuffix.Length]
            : note;
}
