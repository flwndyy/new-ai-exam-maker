namespace ExamMaker.Domain.Catalog;

public class Subject
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid DepartmentId { get; set; }

    public Department? Department { get; set; }

    public ICollection<Topic> Topics { get; set; } = [];
}
