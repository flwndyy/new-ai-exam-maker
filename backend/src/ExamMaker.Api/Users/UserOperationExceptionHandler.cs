using ExamMaker.Application.Users;
using Microsoft.AspNetCore.Diagnostics;

namespace ExamMaker.Api.Users;

public sealed class UserOperationExceptionHandler : IExceptionHandler
{
    /// <summary>Return safe, documented problem codes without leaking AWS responses or database details.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not UserOperationException error) return false;
        await Results.Problem(statusCode: error.StatusCode, title: error.Message, instance: context.Request.Path,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }
}
