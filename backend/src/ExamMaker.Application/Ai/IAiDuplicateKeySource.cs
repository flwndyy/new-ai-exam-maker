namespace ExamMaker.Application.Ai;

/// <summary>Reads the duplicate keys of questions already in the bank for one subject and topic.</summary>
public interface IAiDuplicateKeySource
{
    /// <summary>
    /// Returns <c>AiDuplicateFilter.Key</c> values for every question (any status, any author) with the
    /// given subject and topic, so the worker can drop generated questions that already exist.
    /// </summary>
    Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId, CancellationToken ct);
}
