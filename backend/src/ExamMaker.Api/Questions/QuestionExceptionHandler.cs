using ExamMaker.Application.Questions;
using Microsoft.AspNetCore.Diagnostics;

namespace ExamMaker.Api.Questions;

public sealed class QuestionExceptionHandler : IExceptionHandler
{
    /// <summary>Expose stable workflow error codes and validation fields while keeping persistence diagnostics private.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not QuestionException error) return false;
        // A live account revocation discovered after authentication must still challenge the bearer session.
        if (error.StatusCode == StatusCodes.Status401Unauthorized) context.Response.Headers.WWWAuthenticate = "Bearer";
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier };
        if (error.References is not null) extensions["references"] = error.References;
        if (error.Field is not null) extensions["errors"] = new Dictionary<string, string[]> { [error.Field] = [error.Message] };
        await Results.Problem(statusCode: error.StatusCode, title: error.Message, instance: context.Request.Path,
            extensions: extensions).ExecuteAsync(context);
        return true;
    }
}
