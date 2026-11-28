namespace ExamMaker.Application.Ai;

public sealed record AiTopicInfo(Guid SubjectId, int Grade);

/// <summary>Catalog lookups used to reject generation requests the worker could never save.</summary>
public interface IAiCatalogReader
{
    /// <summary>Returns the subject's department, or null when the subject does not exist.</summary>
    Task<Guid?> FindSubjectDepartmentAsync(Guid subjectId, CancellationToken ct);

    Task<AiTopicInfo?> FindTopicAsync(Guid topicId, CancellationToken ct);
}
