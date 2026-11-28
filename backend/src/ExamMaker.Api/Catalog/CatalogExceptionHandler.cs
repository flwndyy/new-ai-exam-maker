using ExamMaker.Application.Catalog;
using Microsoft.AspNetCore.Diagnostics;

namespace ExamMaker.Api.Catalog;

public sealed class CatalogExceptionHandler : IExceptionHandler
{
    /// <summary>Translate catalog failures into ProblemDetails without exposing database diagnostics.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CatalogException error) return false;
        await Results.Problem(statusCode: error.StatusCode, title: error.Message, instance: context.Request.Path,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }
}
