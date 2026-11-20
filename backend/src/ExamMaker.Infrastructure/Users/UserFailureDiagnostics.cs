using System.Text.RegularExpressions;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ExamMaker.Infrastructure.Users;

public sealed record UserFailureInfo(string ExceptionType, string ServiceCode);

public static class UserFailureDiagnostics
{
    /// <summary>Extract diagnostic categories without retaining messages, SQL values, credentials or AWS identifiers.</summary>
    public static UserFailureInfo Describe(Exception exception)
    {
        var current = exception;
        for (var depth = 0; depth < 8; depth++)
        {
            var code = current switch
            {
                AmazonServiceException aws => aws.ErrorCode,
                PostgresException postgres => postgres.SqlState,
                _ => null
            };
            if (code is not null)
                return new(current.GetType().Name, Regex.IsMatch(code, @"\A[A-Za-z0-9_.-]{1,128}\z") ? code : "Unclassified");
            if (current.InnerException is not { } inner) break;
            current = inner;
        }
        return new(current.GetType().Name, "None");
    }

    /// <summary>Log the failure category and stack only; Exception.Message can contain PII or connection details.</summary>
    public static void Log(ILogger logger, Exception exception, string stage, bool recoveryRequired)
    {
        var info = Describe(exception);
        var stack = exception.StackTrace;
        logger.LogError("User management failure at {Stage}. Exception {ExceptionType}; service code {ServiceCode}; stack {StackTrace}; recovery required {RecoveryRequired}.",
            stage, info.ExceptionType, info.ServiceCode, stack is { Length: > 8000 } ? stack[..8000] : stack, recoveryRequired);
    }
}
