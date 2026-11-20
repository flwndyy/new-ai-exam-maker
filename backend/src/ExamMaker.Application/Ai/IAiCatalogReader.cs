namespace ExamMaker.Application.Ai;

public sealed record AiTopicInfo(Guid SubjectId, int Grade);

/// <summary>Catalog lookups used to reject generation requests the worker could never save.</summary>
public interface IAiCatalogReader
{
    Task<bool> SubjectExistsAsync(Guid subjectId, CancellationToken ct);

    Task<AiTopicInfo?> FindTopicAsync(Guid topicId, CancellationToken ct);
}
