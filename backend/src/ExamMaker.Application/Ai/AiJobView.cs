namespace ExamMaker.Application.Ai;

/// <summary>Mirrors the UserRef schema in docs/api/openapi.yaml.</summary>
public sealed record AiUserRef(Guid Id, string FullName);

/// <summary>Mirrors the AiJob schema in docs/api/openapi.yaml.</summary>
public sealed record AiJobView(
    Guid Id,
    AiUserRef RequestedBy,
    string Status,
    AiGenerateRequest Params,
    int Attempt,
    int MaxAttempts,
    string? ErrorCode,
    string? ErrorMessage,
    string? ModelId,
    string? PromptVersion,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<Guid> QuestionIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Mirrors the AiJobPage schema (PageMeta + items).</summary>
public sealed record AiJobPage(
    IReadOnlyList<AiJobView> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);
