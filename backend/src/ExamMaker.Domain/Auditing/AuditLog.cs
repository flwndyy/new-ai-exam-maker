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

    /// <summary>Shared prefix for user department transfer notes ("department &lt;old&gt;-&gt;&lt;new&gt;") so writers and redaction stay coupled.</summary>
    public const string UserDepartmentMovePrefix = "department ";

    /// <summary>Shared key for catalog department metadata ("department=&lt;guid&gt;") so writers and redaction stay coupled.</summary>
    public const string CatalogDepartmentKey = "department=";

    /// <summary>Shared key for catalog subject metadata ("subject=&lt;guid&gt;") so cross-department topic moves are also redacted for heads.</summary>
    public const string CatalogSubjectKey = "subject=";

    /// <summary>Format a user department transition note token using the shared prefix.</summary>
    public static string FormatUserDepartmentChange(Guid? fromDepartmentId, Guid? toDepartmentId) =>
        $"{UserDepartmentMovePrefix}{fromDepartmentId?.ToString() ?? "none"}->{toDepartmentId?.ToString() ?? "none"}";

    /// <summary>Format a catalog department metadata token using the shared key.</summary>
    public static string FormatCatalogDepartment(Guid departmentId) =>
        $"{CatalogDepartmentKey}{departmentId}";

    /// <summary>Format a catalog subject metadata token using the shared key.</summary>
    public static string FormatCatalogSubject(Guid subjectId) =>
        $"{CatalogSubjectKey}{subjectId}";

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
