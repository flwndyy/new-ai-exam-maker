namespace ExamMaker.Application.Auditing;

/// <summary>Query parameters for filtering and paginating audit log entries.</summary>
public sealed record AuditLogQuery(
    int Page = 1,
    int PageSize = 20,
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? ActorId = null,
    string? Action = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>Lightweight user reference for the actor who triggered an audit entry.</summary>
public sealed record AuditLogActor(Guid Id, string FullName);

/// <summary>Read-only audit log entry returned by the API; Actor is null for system tasks.</summary>
public sealed record AuditLogView(
    Guid Id,
    AuditLogActor? Actor,
    string EntityType,
    Guid EntityId,
    string Action,
    string? FromStatus,
    string? ToStatus,
    string? Note,
    DateTime CreatedAt);

/// <summary>Paginated list of audit log entries.</summary>
public sealed record AuditLogPage(
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    IReadOnlyList<AuditLogView> Items);

/// <summary>Domain/validation error raised during audit log querying, mapped to ProblemDetails.</summary>
public sealed class AuditException(int statusCode, string code, string message, string? field = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

/// <summary>Provide department-scoped and filterable read access to audit logs.</summary>
public interface IAuditLogReader
{
    /// <summary>List audit logs visible to the specified actor according to their role and department.</summary>
    Task<AuditLogPage> ListAsync(Guid actorId, AuditLogQuery query, CancellationToken cancellationToken);
}