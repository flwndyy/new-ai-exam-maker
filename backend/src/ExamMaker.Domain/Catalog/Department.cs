namespace ExamMaker.Domain.Catalog;

/// <summary>A professional department (to chuyen mon). Head users are users with role 'head' in this department.</summary>
public class Department
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<Subject> Subjects { get; set; } = [];
}
