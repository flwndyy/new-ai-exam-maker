namespace ExamMaker.Application.Ai;

/// <summary>Mirrors the AiQuota schema in docs/api/openapi.yaml.</summary>
public sealed record AiQuotaSnapshot(
    DateOnly Date,
    int JobsLimit,
    int JobsUsed,
    long TokensLimit,
    long TokensUsed,
    int MaxQuestionsPerJob,
    DateTimeOffset ResetsAt);
