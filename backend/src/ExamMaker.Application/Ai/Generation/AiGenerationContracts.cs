using System.Text.Json;

namespace ExamMaker.Application.Ai.Generation;

/// <summary>
/// Everything needed to generate one job's questions. Catalog names are loaded by the worker from the database;
/// <see cref="Note"/> is the teacher's untrusted free text (prompt injection risk).
/// </summary>
public sealed record AiGenerationSpec(
    Guid SubjectId,
    Guid TopicId,
    int Grade,
    string Type,
    string Difficulty,
    int Count,
    string SubjectName,
    string TopicName,
    string? Note);

/// <summary>A validated question ready to be stored as Draft with source=AI and (ai_job_id, ai_ordinal).</summary>
public sealed record AiGeneratedQuestion(int Ordinal, string Stem, JsonElement Payload, string? Explanation);
