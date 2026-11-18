namespace ExamMaker.Domain.Catalog;

/// <summary>A curriculum chapter of a subject for one grade (10, 11 or 12).</summary>
public class Topic
{
    public static readonly int[] ValidGrades = [10, 11, 12];

    public Guid Id { get; set; }

    public Guid SubjectId { get; set; }

    public Subject? Subject { get; set; }

    public int Grade { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
