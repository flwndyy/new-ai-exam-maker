using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using ExamMaker.Infrastructure.Users;

namespace ExamMaker.Infrastructure.Tests;

public sealed class UserFailureDiagnosticsTests
{
    [Fact]
    public void Diagnostics_include_failure_category_without_raw_messages_or_credentials()
    {
        var logger = new UserDiagnosticLogger();
        var aws = new AmazonServiceException("synthetic-secret test@example.invalid") { ErrorCode = "AccessDeniedException" };
        UserFailureDiagnostics.Log(logger, new InvalidOperationException("synthetic-wrapper", aws), "Compensation", true);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("AmazonServiceException", message);
        Assert.Contains("AccessDeniedException", message);
        Assert.Contains("Compensation", message);
        Assert.DoesNotContain("synthetic-secret", message);
        Assert.DoesNotContain("test@example.invalid", message);
        Assert.DoesNotContain("synthetic-wrapper", message);
        Assert.Equal("Unclassified", UserFailureDiagnostics.Describe(new AmazonServiceException("ignored") { ErrorCode = "unsafe@value" }).ServiceCode);
    }
}

public sealed class UserDiagnosticLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
