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
    public Task<AuditLogPage> List([FromQuery] AuditLogQuery query, CancellationToken cancellationToken) =>
        auditLogs.ListAsync(currentUser.User!.Id, query, cancellationToken);
}
