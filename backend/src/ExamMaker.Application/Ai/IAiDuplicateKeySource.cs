namespace ExamMaker.Application.Ai;

/// <summary>Reads the duplicate keys of questions already in the bank for one subject, topic and type.</summary>
public interface IAiDuplicateKeySource
{
    /// <summary>
    /// Returns <c>AiDuplicateFilter.Key</c> values for every question (any status, any author) with the
    /// given subject, topic and type, so the worker can drop generated questions that already exist. Keys are
    /// prefixed by type, so other types could never match; filtering by type only saves IO and JSON parsing.
    /// </summary>
    Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId, string type, CancellationToken ct);
}
