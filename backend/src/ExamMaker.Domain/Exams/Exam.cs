using ExamMaker.Domain.Catalog;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Exams;

// Danh tính của đề; nội dung và trạng thái nằm ở từng phiên bản.
public class Exam
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public Subject? Subject { get; set; }
    public int Grade { get; set; }
    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid CreatedBy { get; set; }
    public User? Creator { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ExamVersion> Versions { get; set; } = new List<ExamVersion>();
}
