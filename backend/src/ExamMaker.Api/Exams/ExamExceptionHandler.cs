using ExamMaker.Application.Exams;
using Microsoft.AspNetCore.Diagnostics;

namespace ExamMaker.Api.Exams;

public sealed class ExamExceptionHandler : IExceptionHandler
{
    /// <summary>Translate workflow failures into safe ProblemDetails, including field errors and unapproved source identifiers.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ExamException error) return false;
        if (error.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier };
        if (error.Field is not null) extensions["errors"] = new Dictionary<string, string[]> { [error.Field] = [error.Message] };
        if (error.InvalidQuestionIds is not null) extensions["invalidQuestionIds"] = error.InvalidQuestionIds;
        await Results.Problem(statusCode: error.StatusCode, title: error.Message, instance: context.Request.Path, extensions: extensions).ExecuteAsync(context);
        return true;
    }
}
