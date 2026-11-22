using System.Globalization;
using ExamMaker.Application.Auditing;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

/// <summary>Expose read-only audit log queries for department heads and administrators.</summary>
[ApiController]
[Authorize(Roles = Roles.Head + "," + Roles.Admin)]
[Route("api/v1/audit-logs")]
public sealed class AuditLogsController(IAuditLogReader auditLogs, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Return paginated audit logs ordered newest first; heads are scoped to their own department.</summary>
    [HttpGet]
    public Task<AuditLogPage> List([FromQuery] AuditLogQueryRequest request, CancellationToken cancellationToken)
    {
        var query = new AuditLogQuery(
            Page: ParseIntOrDefault(request.Page, 1, "page", "Số trang không hợp lệ."),
            PageSize: ParseIntOrDefault(request.PageSize, 20, "pageSize", "Kích thước trang không hợp lệ."),
            EntityType: request.EntityType,
            EntityId: ParseOptionalGuid(request.EntityId, "entityId", "Mã đối tượng không hợp lệ."),
            ActorId: ParseOptionalGuid(request.ActorId, "actorId", "Mã người thao tác không hợp lệ."),
            Action: request.Action,
            From: ParseOptionalUtcDateTimeOffset(request.From, "from", "Thời điểm bắt đầu không hợp lệ."),
            To: ParseOptionalUtcDateTimeOffset(request.To, "to", "Thời điểm kết thúc không hợp lệ."));

        return auditLogs.ListAsync(currentUser.User!.Id, query, cancellationToken);
    }

    private static int ParseIntOrDefault(string? raw, int defaultValue, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new AuditException(400, "ValidationFailed", message, field);
    }

    private static Guid? ParseOptionalGuid(string? raw, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Guid.TryParse(raw.Trim(), out var value))
            return value;
        throw new AuditException(400, "ValidationFailed", message, field);
    }

    private static DateTimeOffset? ParseOptionalUtcDateTimeOffset(string? raw, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Treat timestamps without an explicit offset as UTC rather than server local time.
        if (DateTimeOffset.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
            return value;
        throw new AuditException(400, "ValidationFailed", message, field);
    }
}

/// <summary>Raw query binding model so unparseable pagination/date values produce consistent AuditException responses.</summary>
public sealed record AuditLogQueryRequest(
    string? Page = null,
    string? PageSize = null,
    string? EntityType = null,
    string? EntityId = null,
    string? ActorId = null,
    string? Action = null,
    string? From = null,
    string? To = null);
