using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Auditing;

// Nhật ký thao tác. EntityId trỏ tới đối tượng theo EntityType.
public class AuditLog
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public User? Actor { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
