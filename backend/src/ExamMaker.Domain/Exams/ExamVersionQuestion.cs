using ExamMaker.Domain.Questions;

namespace ExamMaker.Domain.Exams;

// Draft links become immutable content snapshots when submitted.
public class ExamVersionQuestion
{
    public Guid ExamVersionId { get; set; }
    public ExamVersion? ExamVersion { get; set; }
    public int SortOrder { get; set; }
    public decimal Points { get; set; }
    public Guid SourceQuestionId { get; set; }
    public Question? SourceQuestion { get; set; }
    public int SourceRevision { get; set; }

    // Nullable while editable; the Submit trigger requires the snapshot fields.
    // Explanation remains optional, matching the source question.
    public string? Type { get; set; }
    public string? Difficulty { get; set; }
    public string? StemSnapshot { get; set; }
    public string? PayloadSnapshot { get; set; }
    public string? ExplanationSnapshot { get; set; }
}
