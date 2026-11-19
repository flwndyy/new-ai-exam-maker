using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Exams;

// Một lần biên soạn của đề, có vòng đời duyệt và xuất bản riêng.
public class ExamVersion
{
    public Guid Id { get; set; }
    public Guid ExamId { get; set; }
    public Exam? Exam { get; set; }
    public int VersionNo { get; set; }
    public string Status { get; set; } = "Draft";
    public int DurationMinutes { get; set; }
    public decimal TotalPoints { get; set; }

    // Chỉ có hash sau khi Application chụp snapshot lúc Submit.
    public string? ContentHash { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public User? Reviewer { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public Guid? PublishedBy { get; set; }
    public User? Publisher { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ExamVersionQuestion> Questions { get; set; } = new List<ExamVersionQuestion>();
}
