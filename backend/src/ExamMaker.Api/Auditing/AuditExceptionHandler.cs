using ExamMaker.Application.Auditing;
using Microsoft.AspNetCore.Diagnostics;

namespace ExamMaker.Api.Auditing;

public sealed class AuditExceptionHandler : IExceptionHandler
{
    /// <summary>Translate AuditException into RFC 7807 ProblemDetails without leaking internal details.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AuditException error) return false;
        if (error.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier };
        if (error.Field is not null) extensions["errors"] = new Dictionary<string, string[]> { [error.Field] = [error.Message] };
        await Results.Problem(statusCode: error.StatusCode, title: error.Message, instance: context.Request.Path, extensions: extensions).ExecuteAsync(context);
        return true;
    }
}