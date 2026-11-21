using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Auditing;

// A null actor identifies a system task; Note must identify that task.
public class AuditLog
{
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
}
