namespace ExamMaker.Application.Ai;

/// <summary>Input of POST /ai/generate. <see cref="Note"/> is untrusted data (prompt injection risk).</summary>
public sealed record AiGenerateRequest(
    Guid SubjectId,
    Guid TopicId,
    int Grade,
    string Type,
    string Difficulty,
    int Count,
    string? Note);
