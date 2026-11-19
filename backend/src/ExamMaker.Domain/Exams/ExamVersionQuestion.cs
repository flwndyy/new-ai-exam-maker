using ExamMaker.Domain.Questions;

namespace ExamMaker.Domain.Exams;

// Liên kết câu nguồn và lưu bản sao nội dung khi gửi duyệt đề.
public class ExamVersionQuestion
{
    public Guid ExamVersionId { get; set; }
    public ExamVersion? ExamVersion { get; set; }
    public int SortOrder { get; set; }
    public decimal Points { get; set; }
    public Guid SourceQuestionId { get; set; }
    public Question? SourceQuestion { get; set; }
    public int SourceRevision { get; set; }

    // Được để trống khi Draft; Application điền snapshot lúc Submit.
    public string? Type { get; set; }
    public string? Difficulty { get; set; }
    public string? StemSnapshot { get; set; }
    public string? PayloadSnapshot { get; set; }
    public string? ExplanationSnapshot { get; set; }
}
